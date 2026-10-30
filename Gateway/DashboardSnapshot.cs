using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Gateway;

/// <summary>Redacted instance-local dashboard summary; gauges are observational, not atomic with totals.</summary>
internal sealed record DashboardSnapshot(int SchemaVersion, DateTimeOffset StartedAtUtc,
    DashboardHealth Health, ImmutableArray<DashboardPolicy> Policies, DashboardMetrics Metrics)
{
    public DashboardPolicyReload PolicyReload { get; init; } = new(false, "disabled", null, null, null);
}
internal sealed record DashboardPolicyReload(bool Enabled, string Status, string? Reason,
    DateTimeOffset? LastCheckedAtUtc, DateTimeOffset? LastAppliedAtUtc);
/// <summary>Local pipeline health, independent of upstream availability and capacity.</summary>
internal sealed record DashboardHealth(bool Live, bool Ready);
/// <summary>Allowlisted immutable policy metadata, without selectors or topology.</summary>
internal sealed record DashboardPolicy(string RouteId, string PolicyId, string Version, string Mode, int MaxBodyBytes)
{
    public string Transformation { get; init; } = "PAN";
    public int RuleCount { get; init; } = 1;
    public ImmutableArray<string> Transformations { get; init; } = ["PAN"];
}
/// <summary>Fixed-size lifetime totals and current inspection gauges.</summary>
internal sealed record DashboardMetrics(DashboardOutcomes Outcomes, long ValidatedMasks,
    DashboardBytes Input, DashboardBytes Output, DashboardDuration Duration, int ActiveInspections, long ReservedBodyBytes);
/// <summary>Closed outcome vocabulary, retaining the telemetry labels.</summary>
internal sealed record DashboardOutcomes(long Inspected, long Blocked,
    [property: JsonPropertyName("capacity_exceeded")] long CapacityExceeded, long Canceled,
    [property: JsonPropertyName("write_error")] long WriteError, long Faulted,
    [property: JsonPropertyName("timed_out")] long TimedOut,
    [property: JsonPropertyName("policy_blocked")] long PolicyBlocked);
/// <summary>Completed samples and byte sum; absence is not a zero-byte sample.</summary>
internal sealed record DashboardBytes(long Samples, long TotalBytes);
/// <summary>Terminal duration sample count and sum, not percentiles.</summary>
internal sealed record DashboardDuration(long Samples, double TotalMilliseconds);
