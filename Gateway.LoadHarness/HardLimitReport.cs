using System.Text;

namespace Gateway.LoadHarness;

// Closed evidence model: unknown measurements stay null and cannot pass.
internal sealed class Enforcement(long requestedBytes)
{
    public string Mechanism => "windows-job-process-memory";
    public long RequestedBytes { get; } = requestedBytes;
    public long? QueriedBytesBefore { get; set; }
    public long? QueriedBytesAfter { get; set; }
    public bool? AssignedBeforeResume { get; set; }
    public bool? MembershipBefore { get; set; }
    public bool? MembershipAfter { get; set; }
    public bool? ProcessMemoryFlagBefore { get; set; }
    public bool? ProcessMemoryFlagAfter { get; set; }
    public bool? KillOnCloseFlagBefore { get; set; }
    public bool? KillOnCloseFlagAfter { get; set; }
    public long? PeakProcessCommitBytes { get; set; }
    public bool? VerificationBefore { get; set; }
    public bool? VerificationAfter { get; set; }
    public bool? Cleanup { get; set; }
    internal bool Passes(long allowed) => RequestedBytes > 0 && QueriedBytesBefore == RequestedBytes && QueriedBytesAfter == RequestedBytes
        && AssignedBeforeResume == true && MembershipBefore == true && MembershipAfter == true
        && ProcessMemoryFlagBefore == true && ProcessMemoryFlagAfter == true && KillOnCloseFlagBefore == true && KillOnCloseFlagAfter == true
        && VerificationBefore == true && VerificationAfter == true && Cleanup == true
        && PeakProcessCommitBytes is >= 0 && PeakProcessCommitBytes <= allowed;
}

internal sealed class HardLimitRun(long requestedBytes) : IsolatedRun
{
    public Enforcement Enforcement { get; } = new(requestedBytes);
}

internal sealed record HardLimitReport(string Schema, string Status, string Classification, string SourceRevision, string Profile, int BodyBytes,
    int RequestsPerConcurrency, int Repetitions, int MaxConcurrentInspections, int[] Concurrencies, long TargetBytes, int HeadroomPercent,
    long AllowedBytes, bool ApiExcluded, bool ClientExcluded, string Notes, List<HardLimitRun> Runs, IsolatedAggregate[] Aggregates)
{
    internal const string Explanation = "Anonymous Windows Job per fresh gateway; API and client excluded by construction. Process-memory limit applies to per-process commit, not working set or a container. Exceeding the limit can fail an allocation without terminating the process. No OOM probe. Job peak includes startup, warmup, load and cooperative shutdown; private-byte self-sampling every 10ms can miss transients. Both sampled private bytes and Job peak must meet headroom, not merely the hard limit. Unknown enforcement is null and fails. Closed-loop synthetic local evidence only; no production approval or default changes. Overall timeout 10 minutes, run 2 minutes, protocol 30 seconds, stop 15 seconds.";

    internal static string Markdown(HardLimitReport r)
    {
        static string Value(object? value) => value?.ToString() ?? "unknown";
        var text = new StringBuilder($"# Hard-limit: {r.Status}\n\n{r.Classification}\n\nSource: {r.SourceRevision}\nProfile: {r.Profile}; body: {r.BodyBytes}; requests/run: {r.RequestsPerConcurrency}; repetitions: {r.Repetitions}.\nCandidate: {r.MaxConcurrentInspections}; schedule: {string.Join("/", r.Concurrencies)}.\nTarget: {r.TargetBytes}; headroom: {r.HeadroomPercent}%; allowed: {r.AllowedBytes} bytes.\nAPI excluded: {r.ApiExcluded}; client excluded: {r.ClientExcluded}.\n\n{r.Notes}\n\n|Rep|Concurrency|Total|Codes|Elapsed ms|RPS|p50 ms|p95 ms|max ms|Managed|Working set|Private|Active|Reserved|Samples|Outcomes|Durations|Released|Canary|Within limit|Passed|\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var s in r.Runs)
            text.AppendLine($"|{s.Repetition}|{s.Concurrency}|{s.Total}|{string.Join(", ", s.StatusCounts.Select(x => $"{x.Key}:{x.Value}"))}|{s.ElapsedMs:F2}|{s.RequestsPerSecond:F2}|{s.P50Ms:F2}|{s.P95Ms:F2}|{s.MaxMs:F2}|{s.Peak.ManagedBytes}|{s.Peak.WorkingSetBytes}|{s.Peak.PrivateBytes}|{s.Peak.Active}|{s.Peak.Reserved}|{s.Peak.Samples}|{s.Outcomes}|{s.Durations}|{s.BudgetReleased}|{s.Canary}|{s.WithinMemoryLimit}|{s.Passed}|");
        text.AppendLine("\nMechanism: windows-job-process-memory\n\n|Rep|Concurrency|Requested|Queried before/after|Assigned before resume|Membership before/after|Memory flag before/after|Kill flag before/after|Peak commit|Verified before/after|Cooperative cleanup|\n|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in r.Runs)
        {
            var e = s.Enforcement;
            text.AppendLine($"|{s.Repetition}|{s.Concurrency}|{e.RequestedBytes}|{Value(e.QueriedBytesBefore)}/{Value(e.QueriedBytesAfter)}|{Value(e.AssignedBeforeResume)}|{Value(e.MembershipBefore)}/{Value(e.MembershipAfter)}|{Value(e.ProcessMemoryFlagBefore)}/{Value(e.ProcessMemoryFlagAfter)}|{Value(e.KillOnCloseFlagBefore)}/{Value(e.KillOnCloseFlagAfter)}|{Value(e.PeakProcessCommitBytes)}|{Value(e.VerificationBefore)}/{Value(e.VerificationAfter)}|{Value(e.Cleanup)}|");
        }
        return text.ToString();
    }
}
