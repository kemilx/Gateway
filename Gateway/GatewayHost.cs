using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace Gateway;

public static class GatewayHost
{
    public static WebApplication Build(string[] args, string? upstream = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Framework request logs can contain URLs/query strings. This slice emits only fixed events.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddFilter((category, level) => (category == typeof(PanResponseTransform).FullName
            || category == typeof(LocalPolicyReloadService).FullName) && level >= LogLevel.Information);
        configure?.Invoke(builder);
        upstream ??= builder.Configuration["Upstream"] ?? "http://127.0.0.1:5101/";
        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Upstream must be a loopback HTTP origin for this local slice.");

        RouteConfig[] routes =
            [new RouteConfig { RouteId = "protected-payment", ClusterId = "synthetic-api",
                Match = new RouteMatch { Path = "/protected/{**rest}" },
                Transforms = [new Dictionary<string, string> { ["PathRemovePrefix"] = "/protected" }] },
             new RouteConfig { RouteId = "passthrough", ClusterId = "synthetic-api",
                Match = new RouteMatch { Path = "/passthrough/{**rest}" },
                Transforms = [new Dictionary<string, string> { ["PathRemovePrefix"] = "/passthrough" }] }];
        builder.Services.AddSingleton<IValidateOptions<PanPolicyOptions>>(new PanPolicyValidator(routes));
        builder.Services.AddOptions<PanPolicyOptions>()
            .Bind(builder.Configuration.GetSection(PanPolicyOptions.SectionName),
                binder => binder.ErrorOnUnknownConfiguration = true)
            .ValidateOnStart();
        builder.Services.AddSingleton<PanPolicyCatalog>();
        builder.Services.AddSingleton(services => new LocalPolicySnapshots(new LocalPolicySnapshot(
            services.GetRequiredService<IOptions<PanPolicyOptions>>().Value, builder.Configuration)));
        var localPolicyFile = builder.Configuration["LocalPolicySnapshotFile"];
        if (localPolicyFile is not null)
        {
            builder.Services.AddSingleton(new LocalPolicyFileReader(localPolicyFile, routes));
            builder.Services.AddHostedService<LocalPolicyReloadService>();
        }
        builder.Services.AddSingleton<InspectionBudget>();
        builder.Services.AddOptions<ProxyAdmissionOptions>()
            .Bind(builder.Configuration.GetSection(ProxyAdmissionOptions.SectionName),
                binder => binder.ErrorOnUnknownConfiguration = true)
            .Validate(options => options.MaxConcurrentProxyRequests is >= 1 and <= 1024,
                "MaxConcurrentProxyRequests must be between 1 and 1024.")
            .ValidateOnStart();
        builder.Services.AddSingleton<ProxyAdmission>();
        builder.Services.AddOptions<UpstreamHeadersDeadlineOptions>()
            .Bind(builder.Configuration.GetSection(UpstreamHeadersDeadlineOptions.SectionName),
                binder => binder.ErrorOnUnknownConfiguration = true)
            .Validate(options => options.TimeoutSeconds is null or (>= 1 and <= 120),
                "TimeoutSeconds must be between 1 and 120 when present.")
            .ValidateOnStart();
        builder.Services.AddSingleton<UpstreamHeadersDeadlineSettings>();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<TimeProvider>(builder.Services, TimeProvider.System);
        builder.Services.AddSingleton<PrivacyTelemetry>();
        builder.Services.AddSingleton<DashboardBudget>();
        builder.Services.AddSingleton<PanResponseTransform>();
        builder.Services.AddReverseProxy()
            .LoadFromMemory(
                routes,
                [new ClusterConfig { ClusterId = "synthetic-api",
                    HttpRequest = new() { ActivityTimeout = TimeSpan.FromSeconds(15) },
                    Destinations = new Dictionary<string, DestinationConfig> { ["local"] = new() { Address = upstream } } }])
            .AddTransforms(context =>
            {
                context.AddRequestTransform(transform =>
                {
                    transform.ProxyRequest.Options.Set(UpstreamHeadersEligibility.Key,
                        UpstreamHeadersEligibility.IsEligible(transform.HttpContext));
                    return ValueTask.CompletedTask;
                });
                var snapshots = context.Services.GetRequiredService<LocalPolicySnapshots>();
                if (!snapshots.IsProtected(context.Route.RouteId)) return;
                context.AddRequestTransform(transform =>
                {
                    if (transform.HttpContext.Features.Get<LocalPolicyRequestCapture>()?.Policy.RouteId != context.Route.RouteId)
                        throw new InvalidOperationException("Protected route policy capture is unavailable.");
                    transform.ProxyRequest.Headers.Remove("Accept-Encoding");
                    transform.ProxyRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                    transform.ProxyRequest.Headers.Remove("If-None-Match");
                    transform.ProxyRequest.Headers.Remove("If-Modified-Since");
                    transform.ProxyRequest.Headers.Remove("Range");
                    transform.ProxyRequest.Headers.Remove("If-Range");
                    return ValueTask.CompletedTask;
                });
                context.AddResponseTransform(transform =>
                {
                    var capture = transform.HttpContext.Features.Get<LocalPolicyRequestCapture>();
                    if (capture?.Policy.RouteId != context.Route.RouteId)
                        throw new InvalidOperationException("Protected route policy capture is unavailable.");
                    return context.Services.GetRequiredService<PanResponseTransform>()
                        .ApplyAsync(transform, capture.Policy, context.Route.ClusterId);
                });
            });
        builder.Services.AddSingleton<Yarp.ReverseProxy.Forwarder.IForwarderHttpClientFactory, UpstreamHeadersHttpClientFactory>();
        LoopbackListenerGuard.Register(builder.Services);
        var app = builder.Build();
        try
        {
            LoopbackListenerGuard.ValidateServer(app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>());
            app.UseRouting();
            app.Use(async (context, next) =>
            {
                var route = context.GetEndpoint()?.Metadata.GetMetadata<Yarp.ReverseProxy.Model.RouteModel>();
                var snapshots = context.RequestServices.GetRequiredService<LocalPolicySnapshots>();
                if (route is not null && snapshots.IsProtected(route.Config.RouteId))
                {
                    var snapshot = snapshots.Active;
                    var policy = snapshot.Catalog.Find(route.Config.RouteId);
                    if (policy is null) { context.Response.StatusCode = 503; return; }
                    context.Features.Set(new LocalPolicyRequestCapture(snapshot, policy));
                }
                await next(context);
            });
            app.UseMiddleware<ProxyAdmissionMiddleware>();
            app.MapReverseProxy();
            app.MapGatewayDashboard(routes.Select(route => route.RouteId));
            // Resolve explicitly before publishing local readiness; invalid PAN configuration must fail startup.
            app.Services.GetRequiredService<PanPolicyCatalog>();
            app.Services.GetRequiredService<LocalPolicySnapshots>();
            app.MapGatewayHealth();
            app.Services.GetRequiredService<ProxyAdmission>();
            app.Services.GetRequiredService<UpstreamHeadersDeadlineSettings>();
            // Freeze the global limits at startup, not on the first inspected response.
            app.Services.GetRequiredService<InspectionBudget>();
            // Create process-lifetime instruments and their budget-backed observable callbacks.
            app.Services.GetRequiredService<PrivacyTelemetry>();
        }
        catch
        {
            // YARP compiles transforms here and can reject options before StartAsync.
            app.DisposeAsync().GetAwaiter().GetResult();
            throw;
        }
        return app;
    }
}
