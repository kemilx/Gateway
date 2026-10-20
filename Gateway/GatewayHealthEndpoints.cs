namespace Gateway;

internal static class GatewayHealthEndpoints
{
    internal static void MapGatewayHealth(this WebApplication app)
    {
        app.MapGet("/health/live", (HttpContext context) =>
            WriteAsync(context, StatusCodes.Status200OK, "{\"status\":\"alive\"}"));
        app.MapGet("/health/ready", (HttpContext context, IHostApplicationLifetime lifetime) =>
            WriteReadinessAsync(context, lifetime));
    }

    // The host maps this only after its immutable PAN catalog has been initialized successfully.
    internal static Task WriteReadinessAsync(HttpContext context, IHostApplicationLifetime lifetime)
    {
        var ready = !lifetime.ApplicationStopping.IsCancellationRequested
            && lifetime.ApplicationStarted.IsCancellationRequested;
        return ready
            ? WriteAsync(context, StatusCodes.Status200OK, "{\"status\":\"ready\"}")
            : WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "{\"status\":\"not_ready\"}");
    }

    private static Task WriteAsync(HttpContext context, int statusCode, string body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(body, context.RequestAborted);
    }
}
