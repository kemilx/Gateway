using System.Text;
using SyntheticTelemetry;

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
        SyntheticOtlpRegistration.Add(builder.Services, builder.Logging, builder.Configuration, SyntheticOtlpRole.TestApi);
        var correlationEnabled = builder.Configuration.GetValue<bool>("TechnicalCorrelation:Enabled");
        if (correlationEnabled)
        {
            builder.Logging.AddSimpleConsole(options => options.IncludeScopes = false);
            builder.Services.Configure<LoggerFactoryOptions>(options => options.ActivityTrackingOptions = ActivityTrackingOptions.None);
            // Opt in only this fixed producer, never framework request logs (URLs/queries).
            // PostConfigure also keeps configuration reload from widening the category set.
            builder.Services.PostConfigure<LoggerFilterOptions>(options =>
            {
                options.Rules.Clear();
                options.Rules.Add(new LoggerFilterRule(null, null, LogLevel.Information,
                    (_, category, level) => category == typeof(SyntheticApi).FullName && level >= LogLevel.Information));
            });
        }
        var app = builder.Build();
        SyntheticOtlpRegistration.Start(app.Services);
        if (correlationEnabled)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SyntheticApi).FullName!);
            app.Use(async (context, next) =>
            {
                ObserveCorrelation(context, logger);
                await next(context);
            });
        }
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
        app.MapGet("/customer", async context =>
        {
            var bytes = Encoding.UTF8.GetBytes("{\"payment\":{\"cardNumber\":\"4111111111111111\"},\"contact\":{\"email\":\"user@example.test\",\"phone\":\"+1-809-555-0199\"}}");
            context.Response.Headers["X-Synthetic-Upstream"] = "test-api";
            context.Response.Headers.ETag = "\"raw-customer-v1\"";
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        });
        endpoints?.Invoke(app);
        return app;
    }

    private static void ObserveCorrelation(HttpContext context, ILogger logger)
    {
        var values = context.Request.Headers["X-Gateway-Correlation-Id"];
        if (values.Count != 1 || values[0] is not { Length: 32 } id
            || !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || !id.Any(c => c != '0')) return;
        try
        {
            if (!logger.IsEnabled(LogLevel.Information)) return;
            logger.LogInformation(new EventId(1, "TechnicalCorrelationObserved"),
                "Synthetic request received; correlation {CorrelationId}.", id);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }
}
