using System.Text;

namespace TestApi;

public static class SyntheticApi
{
    public const string Pan = "4111111111111111";
    public const string Sample = "{\"payment\":{\"cardNumber\":\"4111111111111111\",\"currency\":\"EUR\"},\"amount\":12.50,\"note\":\"synthetic-ñ-😀\",\"items\":[true,null,7]}";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null,
        Action<WebApplication>? endpoints = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        configure?.Invoke(builder);
        var app = builder.Build();
        app.MapGet("/payment", async context =>
        {
            var bytes = Encoding.UTF8.GetBytes(Sample);
            context.Response.Headers["X-Synthetic-Upstream"] = "test-api";
            context.Response.Headers.ETag = "\"raw-v1\"";
            context.Response.Headers["Content-Digest"] = "synthetic-original";
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes);
        });
        endpoints?.Invoke(app);
        return app;
    }
}
