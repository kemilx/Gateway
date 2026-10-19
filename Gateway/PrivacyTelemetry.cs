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
    TimedOut
}

public sealed class PrivacyTelemetry : IDisposable
{
    public const string MeterName = "Gateway.Privacy";
    public const string MeterVersion = "1.0.0";

    private readonly InspectionBudget budget;
    internal TimeSpan InspectionTimeout => budget.InspectionTimeout;
    private readonly Meter meter = new(MeterName, MeterVersion);
    private readonly Counter<long> outcomes;
    private readonly Histogram<double> duration;
    private readonly ObservableGauge<int> active;
    private readonly ObservableGauge<long> reservedBodyBytes;

    public PrivacyTelemetry(InspectionBudget budget)
    {
        this.budget = budget;
        outcomes = meter.CreateCounter<long>("gateway.privacy.inspection.outcomes", "{inspection}");
        duration = meter.CreateHistogram<double>("gateway.privacy.inspection.duration", "ms");
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

    private void RecordOutcome(CompiledPanPolicy policy, PrivacyInspectionOutcome outcome,
        double? durationMilliseconds)
    {
        var tags = new TagList
        {
            { "gateway.route.id", policy.RouteId },
            { "gateway.policy.id", policy.PolicyId },
            { "gateway.policy.version", policy.Version },
            { "gateway.privacy.outcome", GetOutcomeName(outcome) }
        };
        outcomes.Add(1, tags);
        if (durationMilliseconds is { } elapsed) duration.Record(elapsed, tags);
    }

    private static string GetOutcomeName(PrivacyInspectionOutcome outcome) => outcome switch
    {
        PrivacyInspectionOutcome.Inspected => "inspected",
        PrivacyInspectionOutcome.Blocked => "blocked",
        PrivacyInspectionOutcome.CapacityExceeded => "capacity_exceeded",
        PrivacyInspectionOutcome.Canceled => "canceled",
        PrivacyInspectionOutcome.WriteError => "write_error",
        PrivacyInspectionOutcome.Faulted => "faulted",
        PrivacyInspectionOutcome.TimedOut => "timed_out",
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
                telemetry.RecordOutcome(policy, outcome, clock.GetElapsedTime(StartedAt).TotalMilliseconds);
            }
            finally
            {
                lease.Dispose();
            }
        }

        public void Dispose() => Complete(PrivacyInspectionOutcome.Faulted);
    }
}
