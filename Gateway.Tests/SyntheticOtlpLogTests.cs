using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using SyntheticTelemetry;
using Xunit;

namespace Gateway.Tests;

public sealed class SyntheticOtlpLogTests
{
    private const string Correlation = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Gateway_and_api_allowed_events_are_reconstructed_with_exact_schema()
    {
        var gateway = Capture(SyntheticOtlpRole.Gateway, logger => logger.LogInformation(
            new EventId(4, "PrivacyExcluded"),
            "Privacy excluded; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} reason {Reason} rule evaluation {RuleEvaluation} correlation {CorrelationId}.",
            1, "protected-payment", "synthetic-api", "PAN", "v1", "excluded_head", "not_evaluated", Correlation));
        var api = Capture(SyntheticOtlpRole.TestApi, logger => logger.LogInformation(
            new EventId(1, "TechnicalCorrelationObserved"),
            "Synthetic request received; correlation {CorrelationId}.", Correlation));

        var gatewayRecord = Assert.Single(gateway);
        Assert.Equal(new EventId(4, "PrivacyExcluded"), gatewayRecord.EventId);
        Assert.Equal("Gateway.PanResponseTransform", gatewayRecord.Category);
        Assert.Equal(Correlation, gatewayRecord.Attributes["CorrelationId"]);
        Assert.Equal(9, gatewayRecord.Attributes.Count);
        var apiRecord = Assert.Single(api);
        Assert.Equal(new EventId(1, "TechnicalCorrelationObserved"), apiRecord.EventId);
        Assert.Equal(new[] { "{OriginalFormat}", "CorrelationId" }, apiRecord.Attributes.Keys.Order());
    }

    [Fact]
    public void Unexpected_events_exceptions_and_ambient_activity_are_dropped_or_cleaned()
    {
        using var activity = new Activity("sensitive").SetParentId("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01").Start();
        activity?.AddBaggage("token", "secret_marker");
        var records = Capture(SyntheticOtlpRole.Gateway, logger =>
        {
            logger.LogInformation(new EventId(3, "Other"), "Sensitive {Value}", "secret_marker");
            logger.Log(LogLevel.Information, new EventId(4, "PrivacyExcluded"), "bad", new InvalidOperationException("secret_marker"), static (s, _) => s);
            logger.LogInformation(new EventId(4, "PrivacyExcluded"),
                "Privacy excluded; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} reason {Reason} rule evaluation {RuleEvaluation} correlation {CorrelationId}.",
                1, "protected-payment", "synthetic-api", "PAN", "v1", "excluded_media_type", "not_evaluated", Correlation);
        });

        var record = Assert.Single(records);
        Assert.Equal(default, record.TraceId);
        Assert.Equal(default, record.SpanId);
        Assert.Null(record.TraceState);
        Assert.Null(record.Exception);
        Assert.DoesNotContain("secret_marker", string.Join('|', record.Attributes.Select(p => $"{p.Key}={p.Value}")));
    }

    [Fact]
    public void Terminal_and_rule_events_preserve_only_closed_validated_fields()
    {
        var records = Capture(SyntheticOtlpRole.Gateway, logger =>
        {
            logger.LogInformation(new EventId(5, "PrivacyInspectionCompleted"),
                "Privacy inspection completed; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} validated masks {ValidatedMasks} validated explicit sensitive matches {ValidatedExplicitSensitiveMatches} validated secondary PAN matches {ValidatedSecondaryPanMatches}.",
                1, "protected-payment", "synthetic-api", "PAN", "v1", Correlation, "inspected", 1L, 1L, 0L);
            logger.LogInformation(new EventId(6, "PrivacyRuleMatched"),
                "Privacy rule matched; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} rule {RuleId} transformation {Transformation} validated matches {ValidatedMatches} validated masks {ValidatedMasks}.",
                1, "protected-payment", "synthetic-api", "PAN", "v1", Correlation, "inspected", "primary", "PAN", 1L, 1L);
            logger.LogInformation(new EventId(6, "PrivacyRuleMatched"),
                "Privacy rule matched; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} rule {RuleId} transformation {Transformation} validated matches {ValidatedMatches} validated masks {ValidatedMasks}.",
                1, "protected-payment", "synthetic-api", "PAN", "v1", Correlation, "inspected", "additional-15", "PAN", 1L, 1L);
        });

        Assert.Equal(new[] { 5, 6 }, records.Select(record => record.EventId.Id));
        Assert.All(records, record => Assert.DoesNotContain(record.Attributes.Keys,
            key => key.Contains("Body", StringComparison.OrdinalIgnoreCase) || key.Contains("Url", StringComparison.OrdinalIgnoreCase)));
    }

    private static Snapshot[] Capture(SyntheticOtlpRole role, Action<ILogger> emit)
    {
        var exporter = new RecordingExporter();
        var settings = new SyntheticOtlpSettings(role, new Uri("http://127.0.0.1:4318/"));
        using var provider = new SyntheticOtlpLoggerProvider(settings, exporter);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        emit(factory.CreateLogger(role == SyntheticOtlpRole.Gateway ? "Gateway.PanResponseTransform" : "TestApi.SyntheticApi"));
        return exporter.Records.ToArray();
    }

    private sealed class RecordingExporter : BaseExporter<LogRecord>
    {
        internal ConcurrentQueue<Snapshot> Records { get; } = new();
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
                Records.Enqueue(new(record.EventId, record.CategoryName, record.TraceId, record.SpanId,
                    record.TraceState, record.Exception, record.Attributes?.ToDictionary() ?? []));
            return ExportResult.Success;
        }
    }

    private sealed record Snapshot(EventId EventId, string? Category, ActivityTraceId TraceId,
        ActivitySpanId SpanId, string? TraceState, Exception? Exception,
        Dictionary<string, object?> Attributes);
}
