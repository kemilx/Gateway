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

        builder.Services.AddSingleton<PanResponseTransform>();
        builder.Services.AddReverseProxy()
            .LoadFromMemory(
                [new RouteConfig { RouteId = "protected-payment", ClusterId = "synthetic-api",
                    Match = new RouteMatch { Path = "/protected/{**rest}" },
                    Transforms = [new Dictionary<string, string> { ["PathRemovePrefix"] = "/protected" }] },
                 new RouteConfig { RouteId = "passthrough", ClusterId = "synthetic-api",
                    Match = new RouteMatch { Path = "/passthrough/{**rest}" },
                    Transforms = [new Dictionary<string, string> { ["PathRemovePrefix"] = "/passthrough" }] }],
                [new ClusterConfig { ClusterId = "synthetic-api",
                    HttpRequest = new() { ActivityTimeout = TimeSpan.FromSeconds(15) },
                    Destinations = new Dictionary<string, DestinationConfig> { ["local"] = new() { Address = upstream } } }])
            .AddTransforms(context =>
            {
                if (context.Route.RouteId != "protected-payment") return;
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
                    context.Services.GetRequiredService<PanResponseTransform>().ApplyAsync(transform));
            });
        var app = builder.Build();
        app.MapReverseProxy();
        return app;
    }
}
