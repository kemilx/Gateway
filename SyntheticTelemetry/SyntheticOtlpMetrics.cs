using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace SyntheticTelemetry;

internal sealed class SyntheticOtlpMetrics : IDisposable
{
    private static readonly HashSet<string> Instruments =
    [
        "gateway.privacy.inspection.outcomes",
        "gateway.privacy.inspection.masks",
        "gateway.privacy.inspection.secondary_pan_matches",
        "gateway.privacy.inspection.explicit_sensitive_matches",
        "gateway.privacy.inspection.duration",
        "gateway.privacy.inspection.input_body_bytes",
        "gateway.privacy.inspection.output_body_bytes",
        "gateway.privacy.inspection.active",
        "gateway.privacy.inspection.reserved_body_bytes"
    ];
    private static readonly string[] Tags =
    [
        "gateway.route.id", "gateway.policy.id", "gateway.policy.version", "gateway.privacy.outcome"
    ];

    private readonly MeterProvider provider;

    internal SyntheticOtlpMetrics(SyntheticOtlpSettings settings)
    {
        var exporter = new OtlpMetricExporter(CreateOptions(settings));
        var reader = new PeriodicExportingMetricReader(exporter,
            (int)settings.MetricExportInterval.TotalMilliseconds,
            (int)settings.ExportTimeout.TotalMilliseconds);
        provider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(
                [new KeyValuePair<string, object>("service.name", settings.ServiceName)]))
            .AddMeter("Gateway.Privacy")
            .AddView(instrument => View(instrument, settings.MetricCardinalityLimit))
            .SetExemplarFilter(ExemplarFilterType.AlwaysOff)
            .SetMaxMetricStreams(9)
            .AddReader(reader)
            .Build();
    }

    public void Dispose() => provider.Dispose();

    private static MetricStreamConfiguration View(Instrument instrument, int cardinalityLimit)
    {
        if (!Instruments.Contains(instrument.Name)) return MetricStreamConfiguration.Drop;
        var gauges = instrument.Name is "gateway.privacy.inspection.active" or "gateway.privacy.inspection.reserved_body_bytes";
        return new MetricStreamConfiguration
        {
            TagKeys = gauges ? [] : Tags,
            CardinalityLimit = cardinalityLimit
        };
    }

    private static OtlpExporterOptions CreateOptions(SyntheticOtlpSettings settings) => new()
    {
        Endpoint = settings.MetricsEndpoint,
        Protocol = OtlpExportProtocol.HttpProtobuf,
        TimeoutMilliseconds = (int)settings.ExportTimeout.TotalMilliseconds,
        HttpClientFactory = static () => new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false
        }, disposeHandler: true)
    };
}
