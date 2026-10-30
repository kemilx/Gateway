using System.Collections;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace SyntheticTelemetry;

public sealed class SyntheticOtlpLoggerProvider : ILoggerProvider
{
    private readonly SyntheticOtlpSettings settings;
    private readonly ILoggerFactory targetFactory;
    private int disposed;

    public SyntheticOtlpLoggerProvider(SyntheticOtlpSettings settings)
        : this(settings, CreateExporter(settings), useBatch: true) { }

    internal SyntheticOtlpLoggerProvider(SyntheticOtlpSettings settings, BaseExporter<LogRecord> exporter)
        : this(settings, exporter, useBatch: false) { }

    private SyntheticOtlpLoggerProvider(SyntheticOtlpSettings settings, BaseExporter<LogRecord> exporter, bool useBatch)
    {
        this.settings = settings;
        targetFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = false;
            options.IncludeFormattedMessage = false;
            options.ParseStateValues = false;
            options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(
                [new KeyValuePair<string, object>("service.name", settings.ServiceName)]));
            options.AddProcessor(new SyntheticOtlpLogProcessor());
            options.AddProcessor(useBatch
                ? new BatchLogRecordExportProcessor(exporter, settings.LogQueueCapacity,
                    (int)settings.LogExportDelay.TotalMilliseconds, (int)settings.ExportTimeout.TotalMilliseconds,
                    settings.LogBatchSize)
                : new SimpleLogRecordExportProcessor(exporter));
        }));
    }

    public ILogger CreateLogger(string categoryName) => new BridgeLogger(this, categoryName);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            targetFactory.Dispose();
    }

    private bool IsCategory(string category) => settings.Role switch
    {
        SyntheticOtlpRole.Gateway => category == "Gateway.PanResponseTransform",
        SyntheticOtlpRole.TestApi => category == "TestApi.SyntheticApi",
        _ => false
    };

    private void Publish<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception)
    {
        if (level != LogLevel.Information || exception is not null || !IsCategory(category)
            || !SyntheticOtlpLogSchema.TryCreate(settings.Role, eventId, state, out var sanitized))
            return;
        try
        {
            targetFactory.CreateLogger(category).Log(level, eventId, sanitized, null,
                static (value, _) => value.Template);
        }
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }

    private static OtlpLogExporter CreateExporter(SyntheticOtlpSettings settings) => new(new OtlpExporterOptions
    {
        Endpoint = settings.LogsEndpoint,
        Protocol = OtlpExportProtocol.HttpProtobuf,
        TimeoutMilliseconds = (int)settings.ExportTimeout.TotalMilliseconds,
        HttpClientFactory = static () => new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false
        }, disposeHandler: true)
    });

    private sealed class BridgeLogger(SyntheticOtlpLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Information && owner.IsCategory(category);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => owner.Publish(category, logLevel, eventId, state, exception);
    }
}

internal static class SyntheticOtlpLogSchema
{
    private const string Event4 = "Privacy excluded; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} reason {Reason} rule evaluation {RuleEvaluation} correlation {CorrelationId}.";
    private const string Event5 = "Privacy inspection completed; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} validated masks {ValidatedMasks} validated explicit sensitive matches {ValidatedExplicitSensitiveMatches} validated secondary PAN matches {ValidatedSecondaryPanMatches}.";
    private const string Event6 = "Privacy rule matched; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} rule {RuleId} transformation {Transformation} validated matches {ValidatedMatches} validated masks {ValidatedMasks}.";
    private const string Api1 = "Synthetic request received; correlation {CorrelationId}.";
    private static readonly HashSet<string> Outcomes = ["inspected", "blocked", "capacity_exceeded", "canceled", "write_error", "faulted", "timed_out", "policy_blocked"];
    private static readonly HashSet<string> Reasons = ["excluded_head", "excluded_bodyless", "excluded_media_type"];
    private static readonly HashSet<string> Transformations = ["PAN", "Email", "Phone"];

    internal static bool TryCreate<TState>(SyntheticOtlpRole role, EventId eventId, TState state, out SanitizedState sanitized)
    {
        sanitized = null!;
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values) return false;
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in values)
            if (!map.TryAdd(pair.Key, pair.Value)) return false;

        var template = map.GetValueOrDefault("{OriginalFormat}") as string;
        bool valid = role switch
        {
            SyntheticOtlpRole.Gateway when eventId == new EventId(4, "PrivacyExcluded") =>
                template == Event4 && Exact(map, "SchemaVersion", "RouteId", "ServiceId", "PolicyId", "PolicyVersion", "Reason", "RuleEvaluation", "CorrelationId", "{OriginalFormat}")
                && Common(map) && map["Reason"] is string reason && Reasons.Contains(reason)
                && Equals(map["RuleEvaluation"], "not_evaluated"),
            SyntheticOtlpRole.Gateway when eventId == new EventId(5, "PrivacyInspectionCompleted") =>
                template == Event5 && Exact(map, "SchemaVersion", "RouteId", "ServiceId", "PolicyId", "PolicyVersion", "CorrelationId", "Outcome", "ValidatedMasks", "ValidatedExplicitSensitiveMatches", "ValidatedSecondaryPanMatches", "{OriginalFormat}")
                && Common(map) && ValidOutcome(map) && NonNegative(map, "ValidatedMasks")
                && NonNegative(map, "ValidatedExplicitSensitiveMatches") && NonNegative(map, "ValidatedSecondaryPanMatches"),
            SyntheticOtlpRole.Gateway when eventId == new EventId(6, "PrivacyRuleMatched") =>
                template == Event6 && Exact(map, "SchemaVersion", "RouteId", "ServiceId", "PolicyId", "PolicyVersion", "CorrelationId", "Outcome", "RuleId", "Transformation", "ValidatedMatches", "ValidatedMasks", "{OriginalFormat}")
                && Common(map) && ValidOutcome(map) && ValidRule(map["RuleId"] as string)
                && map["Transformation"] is string transformation && Transformations.Contains(transformation)
                && map["ValidatedMatches"] is long matches && matches > 0 && NonNegative(map, "ValidatedMasks"),
            SyntheticOtlpRole.TestApi when eventId == new EventId(1, "TechnicalCorrelationObserved") =>
                template == Api1 && Exact(map, "CorrelationId", "{OriginalFormat}") && ValidCorrelation(map["CorrelationId"] as string),
            _ => false
        };
        if (!valid) return false;
        sanitized = new SanitizedState(template!, values.Where(pair => pair.Key != "{OriginalFormat}")
            .Append(new("{OriginalFormat}", template)).ToArray());
        return true;
    }

    private static bool Common(Dictionary<string, object?> map) => map["SchemaVersion"] is 1
        && SafeId(map["RouteId"] as string) && SafeId(map["ServiceId"] as string)
        && SafeId(map["PolicyId"] as string) && SafeId(map["PolicyVersion"] as string)
        && ValidCorrelation(map["CorrelationId"] as string);
    private static bool ValidOutcome(Dictionary<string, object?> map) => map["Outcome"] is string value && Outcomes.Contains(value);
    private static bool NonNegative(Dictionary<string, object?> map, string key) => map[key] is long value && value >= 0;
    private static bool ValidRule(string? value) => value is "primary" or "secondary-pan"
        || value is { } rule && rule.StartsWith("additional-", StringComparison.Ordinal)
        && int.TryParse(rule["additional-".Length..], out var index) && index is >= 0 and <= 14
        && rule == $"additional-{index}";
    private static bool SafeId(string? value) => value is { Length: > 0 and <= 64 }
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private static bool ValidCorrelation(string? value) => value is { Length: 32 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && value.Any(c => c != '0');
    private static bool Exact(Dictionary<string, object?> map, params string[] keys) => map.Count == keys.Length && keys.All(map.ContainsKey);
}

internal sealed class SanitizedState(string template, KeyValuePair<string, object?>[] values)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    public string Template { get; } = template;
    public int Count => values.Length;
    public KeyValuePair<string, object?> this[int index] => values[index];
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => values.GetEnumerator();
}
