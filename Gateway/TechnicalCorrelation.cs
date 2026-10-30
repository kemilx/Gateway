using Microsoft.Extensions.Options;

namespace Gateway;

internal sealed class TechnicalCorrelationOptions
{
    public const string SectionName = "TechnicalCorrelation";
    public bool Enabled { get; set; }
}

// IOptions (not a monitor): all request paths and pooled transports use one startup snapshot.
internal sealed class TechnicalCorrelationSettings(IOptions<TechnicalCorrelationOptions> options)
{
    public bool Enabled { get; } = options.Value.Enabled;
}

internal sealed record TechnicalCorrelationCapture(string Id);

internal static class TechnicalCorrelationHeaders
{
    internal const string Name = "X-Gateway-Correlation-Id";
    private static readonly string[] Ambient = ["traceparent", "tracestate", "baggage", "Request-Id", "Correlation-Context"];

    internal static void Apply(HttpRequestMessage request, TechnicalCorrelationCapture capture)
    {
        request.Headers.Remove(Name);
        foreach (var header in Ambient) request.Headers.Remove(header);
        request.Headers.TryAddWithoutValidation(Name, capture.Id);
    }
}
