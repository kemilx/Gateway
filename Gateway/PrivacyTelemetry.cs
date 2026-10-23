using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Gateway;

public enum PrivacyInspectionOutcome
{
    Inspected,
    Blocked,
    CapacityExceeded,
    Canceled,
    WriteError,
    Faulted,
    TimedOut,
    PolicyBlocked
}

public sealed class PrivacyTelemetry : IDisposable
{
    public const string MeterName = "Gateway.Privacy";
    public const string MeterVersion = "1.0.0";

    private readonly InspectionBudget budget;
    private readonly object totalsGate = new();
    private readonly long[] outcomeTotals = new long[8];
    private long maskTotal, inputSamples, inputTotal, outputSamples, outputTotal, durationSamples;
    private double durationTotal;
    internal static long SaturatingAdd(long total, long value) => value <= 0 ? total
        : total > long.MaxValue - value ? long.MaxValue : total + value;
    internal static double SaturatingAdd(double total, double value) => value <= 0 ? total
        : !double.IsFinite(value) || total > double.MaxValue - value ? double.MaxValue : total + value;

    internal DashboardMetrics DashboardMetrics
    {
        get
        {
            lock (totalsGate)
            {
                var gauges = budget.Snapshot;
                return new(new(outcomeTotals[0], outcomeTotals[1], outcomeTotals[2], outcomeTotals[3],
                    outcomeTotals[4], outcomeTotals[5], outcomeTotals[6], outcomeTotals[7]), maskTotal,
                    new(inputSamples, inputTotal), new(outputSamples, outputTotal),
                    new(durationSamples, durationTotal), gauges.ActiveInspections, gauges.ReservedBodyBytes);
            }
        }
    }
    internal TimeSpan InspectionTimeout => budget.InspectionTimeout;
    private readonly Meter meter = new(MeterName, MeterVersion);
    private readonly Counter<long> outcomes;
    private readonly Counter<long> masks;
    private readonly Counter<long> secondaryPanMatches;
    private readonly Counter<long> explicitSensitiveMatches;
    private readonly Histogram<double> duration;
    private readonly Histogram<long> inputBodyBytes;
    private readonly Histogram<long> outputBodyBytes;
    private readonly ObservableGauge<int> active;
    private readonly ObservableGauge<long> reservedBodyBytes;

    public PrivacyTelemetry(InspectionBudget budget)
    {
        this.budget = budget;
        outcomes = meter.CreateCounter<long>("gateway.privacy.inspection.outcomes", "{inspection}");
        masks = meter.CreateCounter<long>("gateway.privacy.inspection.masks", "{mask}");
        secondaryPanMatches = meter.CreateCounter<long>("gateway.privacy.inspection.secondary_pan_matches", "{match}");
        explicitSensitiveMatches = meter.CreateCounter<long>("gateway.privacy.inspection.explicit_sensitive_matches", "{match}");
        duration = meter.CreateHistogram<double>("gateway.privacy.inspection.duration", "ms");
        inputBodyBytes = meter.CreateHistogram<long>("gateway.privacy.inspection.input_body_bytes", "By");
        outputBodyBytes = meter.CreateHistogram<long>("gateway.privacy.inspection.output_body_bytes", "By");
        active = meter.CreateObservableGauge<int>("gateway.privacy.inspection.active",
            () => budget.Snapshot.ActiveInspections, "{inspection}");
        reservedBodyBytes = meter.CreateObservableGauge<long>("gateway.privacy.inspection.reserved_body_bytes",
            () => budget.Snapshot.ReservedBodyBytes, "By");
    }

    public InspectionMeasurement? TryStartInspection(CompiledPanPolicy policy) => TryStartInspection(policy, TimeProvider.System);

    internal InspectionMeasurement? TryStartInspection(CompiledPanPolicy policy, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(policy);
        // Conservative start: includes scheduling/lock delay before acquisition, without
        // invoking an injectable clock under the budget lock. Never renew after acquisition.
        var startedAt = clock.GetTimestamp();
        var lease = budget.TryAcquire(policy.MaxBodyBytes);
        if (lease is null)
        {
            RecordOutcome(policy, PrivacyInspectionOutcome.CapacityExceeded, durationMilliseconds: null);
            return null;
        }

        return new InspectionMeasurement(this, policy, lease, clock, startedAt);
    }

    public void Dispose() => meter.Dispose();

    internal void RecordExclusion(CompiledPanPolicy policy, string outcome)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (outcome is not ("excluded_head" or "excluded_bodyless" or "excluded_media_type"))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        var tags = new TagList
        {
            { "gateway.route.id", policy.RouteId },
            { "gateway.policy.id", policy.PolicyId },
            { "gateway.policy.version", policy.Version },
            { "gateway.privacy.outcome", outcome }
        };
        // Exclusions are not inspections: no lease, samples or dashboard totals.
        // Synchronous consumers can still block; only ordinary failures are isolated.
        try { outcomes.Add(1, tags); }
        catch (Exception exception) when (IsOrdinaryListenerFailure(exception)) { }
    }

    private void RecordOutcome(CompiledPanPolicy policy, PrivacyInspectionOutcome outcome,
        double? durationMilliseconds, long? capturedInputBodyBytes = null, long? preparedOutputBodyBytes = null,
        long validatedMasks = 0, long validatedSecondaryPanMatches = 0, long validatedExplicitSensitiveMatches = 0)
    {
        var tags = new TagList
        {
            { "gateway.route.id", policy.RouteId },
            { "gateway.policy.id", policy.PolicyId },
            { "gateway.policy.version", policy.Version },
            { "gateway.privacy.outcome", GetOutcomeName(outcome) }
        };
        // Instance-owned, fixed-size accounting precedes all external synchronous callbacks.
        // No samples, events, policy identifiers or response data are retained here.
        lock (totalsGate)
        {
            outcomeTotals[(int)outcome] = SaturatingAdd(outcomeTotals[(int)outcome], 1);
            maskTotal = SaturatingAdd(maskTotal, validatedMasks);
            if (capturedInputBodyBytes is { } input) { inputSamples = SaturatingAdd(inputSamples, 1); inputTotal = SaturatingAdd(inputTotal, input); }
            if (preparedOutputBodyBytes is { } output) { outputSamples = SaturatingAdd(outputSamples, 1); outputTotal = SaturatingAdd(outputTotal, output); }
            if (durationMilliseconds is { } milliseconds) { durationSamples = SaturatingAdd(durationSamples, 1); durationTotal = SaturatingAdd(durationTotal, milliseconds); }
        }
        // Listener callbacks run synchronously. Isolate each publication so an ordinary
        // consumer failure cannot affect inspection or prevent the other instrument.
        // Keep tag/outcome validation above these boundaries; never log consumer errors.
        try
        {
            outcomes.Add(1, tags);
        }
        catch (Exception exception) when (IsOrdinaryListenerFailure(exception)) { }

        if (durationMilliseconds is { } elapsed)
        {
            try
            {
                duration.Record(elapsed, tags);
            }
            catch (Exception exception) when (IsOrdinaryListenerFailure(exception)) { }
        }

        if (capturedInputBodyBytes is { } bytes)
        {
            try
            {
                inputBodyBytes.Record(bytes, tags);
            }
            catch (Exception exception) when (IsOrdinaryListenerFailure(exception)) { }
        }

        if (preparedOutputBodyBytes is { } outputBytes)
        {
            try
            {
                outputBodyBytes.Record(outputBytes, tags);
            }
            catch (Exception exception) when (IsOrdinaryListenerFailure(exception)) { }
        }
        if (validatedMasks > 0)
        {
            try
            {
                masks.Add(validatedMasks, tags);
            }
            catch (Exception exception) when (IsOrdinaryListenerFailure(exception)) { }
        }
        if (validatedSecondaryPanMatches > 0)
        {
            try
            {
                secondaryPanMatches.Add(validatedSecondaryPanMatches, tags);
            }
            catch (Exception exception) when (IsOrdinaryListenerFailure(exception)) { }
        }
        if (validatedExplicitSensitiveMatches > 0)
        {
            try
            {
                explicitSensitiveMatches.Add(validatedExplicitSensitiveMatches, tags);
            }
            catch (Exception exception) when (IsOrdinaryListenerFailure(exception)) { }
        }
    }

    private static bool IsOrdinaryListenerFailure(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static string GetOutcomeName(PrivacyInspectionOutcome outcome) => outcome switch
    {
        PrivacyInspectionOutcome.Inspected => "inspected",
        PrivacyInspectionOutcome.Blocked => "blocked",
        PrivacyInspectionOutcome.CapacityExceeded => "capacity_exceeded",
        PrivacyInspectionOutcome.Canceled => "canceled",
        PrivacyInspectionOutcome.WriteError => "write_error",
        PrivacyInspectionOutcome.Faulted => "faulted",
        PrivacyInspectionOutcome.TimedOut => "timed_out",
        PrivacyInspectionOutcome.PolicyBlocked => "policy_blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    public sealed class InspectionMeasurement : IDisposable
    {
        private readonly PrivacyTelemetry telemetry;
        private readonly CompiledPanPolicy policy;
        private readonly IDisposable lease;
        private readonly TimeProvider clock;
        internal long StartedAt { get; }
        private int completed;
        internal long? CapturedInputBodyBytes { get; set; }
        internal long? PreparedOutputBodyBytes { get; set; }
        internal long ValidatedMasks { get; set; }
        internal long ValidatedSecondaryPanMatches { get; set; }
        internal long ValidatedExplicitSensitiveMatches { get; set; }

        internal InspectionMeasurement(PrivacyTelemetry telemetry, CompiledPanPolicy policy, IDisposable lease, TimeProvider clock, long startedAt)
        {
            this.clock = clock;
            StartedAt = startedAt;
            this.telemetry = telemetry;
            this.policy = policy;
            this.lease = lease;
        }

        public void Complete(PrivacyInspectionOutcome outcome)
        {
            if (outcome == PrivacyInspectionOutcome.CapacityExceeded)
                throw new ArgumentOutOfRangeException(nameof(outcome));
            if (Interlocked.Exchange(ref completed, 1) != 0) return;

            try
            {
                telemetry.RecordOutcome(policy, outcome, clock.GetElapsedTime(StartedAt).TotalMilliseconds,
                    CapturedInputBodyBytes, PreparedOutputBodyBytes, ValidatedMasks, ValidatedSecondaryPanMatches, ValidatedExplicitSensitiveMatches);
            }
            finally
            {
                lease.Dispose();
            }
        }

        public void Dispose() => Complete(PrivacyInspectionOutcome.Faulted);
    }
}
