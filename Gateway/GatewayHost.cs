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
        builder.Logging.AddFilter((category, level) => category == typeof(PanResponseTransform).FullName
            && level >= LogLevel.Information);
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
        builder.Services.AddSingleton<PanResponseTransform>();
        builder.Services.AddReverseProxy()
            .LoadFromMemory(
                routes,
                [new ClusterConfig { ClusterId = "synthetic-api",
                    HttpRequest = new() { ActivityTimeout = TimeSpan.FromSeconds(15) },
                    Destinations = new Dictionary<string, DestinationConfig> { ["local"] = new() { Address = upstream } } }])
            .AddTransforms(context =>
            {
                var policy = context.Services.GetRequiredService<PanPolicyCatalog>().Find(context.Route.RouteId);
                if (policy is null) return;
                context.AddRequestTransform(transform =>
                {
                    transform.ProxyRequest.Headers.Remove("Accept-Encoding");
                    transform.ProxyRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                    transform.ProxyRequest.Headers.Remove("If-None-Match");
                    transform.ProxyRequest.Headers.Remove("If-Modified-Since");
                    transform.ProxyRequest.Headers.Remove("Range");
                    transform.ProxyRequest.Headers.Remove("If-Range");
                    return ValueTask.CompletedTask;
                });
                context.AddResponseTransform(transform =>
                    context.Services.GetRequiredService<PanResponseTransform>().ApplyAsync(transform, policy));
            });
        var app = builder.Build();
        try { app.MapReverseProxy(); }
        catch
        {
            // YARP compiles transforms here and can reject options before StartAsync.
            app.DisposeAsync().GetAwaiter().GetResult();
            throw;
        }
        return app;
    }
}
