using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Gateway.LoadHarness;

internal sealed record CompareEnvironment(string CompiledRevision, string CompiledHarnessSha256, string CompiledGatewaySha256,
    string CompiledApiSha256, string BuildConfiguration,
    string Runtime, string OS, string Architecture, int ProcessorCount, bool ServerGC)
{
    internal static CompareEnvironment Current()
    {
        var assembly = typeof(CompareEnvironment).Assembly;
        return new(Common.Revision, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Gateway.GatewayHost).Assembly.Location))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(TestApi.SyntheticApi).Assembly.Location))),
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            RuntimeInformation.FrameworkDescription, OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other",
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, GCSettings.IsServerGC);
    }
}

internal sealed class CompareArm(string name)
{
    public string Name { get; } = name;
    public bool Valid { get; set; }
    public string? Failure { get; set; }
    public int? Warmups { get; set; }
    public long? WarmupOutcomes { get; set; }
    public long? WarmupDurations { get; set; }
    public int? ValidatedRequests { get; set; }
    public double? ClientElapsedMs { get; set; }
    public double? RequestsPerSecond { get; set; }
    public double? P50Ms { get; set; }
    public double? P95Ms { get; set; }
    public double? P99Ms { get; set; }
    public CompareWindow? Gateway { get; set; }
    internal Dictionary<string, double?> Metrics() => new()
    {
        ["p50Ms"] = P50Ms,
        ["p95Ms"] = P95Ms,
        ["p99Ms"] = P99Ms,
        ["requestsPerSecond"] = RequestsPerSecond,
        ["cpuMsPerRequest"] = CompareMath.Ratio(Gateway?.CpuMs, ValidatedRequests),
        ["allocatedBytesPerRequest"] = CompareMath.Ratio(Gateway?.AllocatedBytes, ValidatedRequests),
        ["managedPeakBytes"] = Gateway?.Peak.ManagedBytes,
        ["workingSetPeakBytes"] = Gateway?.Peak.WorkingSetBytes,
        ["privatePeakBytes"] = Gateway?.Peak.PrivateBytes
    };
}
internal sealed record CompareWindow(
    [property: JsonRequired] double ElapsedMs,
    [property: JsonRequired] double CpuMs,
    [property: JsonRequired] long AllocatedBytes,
    [property: JsonRequired] long Outcomes,
    [property: JsonRequired] long Durations,
    [property: JsonRequired] int[] GcStart,
    [property: JsonRequired] int[] GcEnd,
    [property: JsonRequired] ComparePeak Peak,
    [property: JsonRequired] bool Released);
internal sealed class ComparePeak
{
    [JsonRequired] public long ManagedBytes { get; set; }
    [JsonRequired] public long WorkingSetBytes { get; set; }
    [JsonRequired] public long PrivateBytes { get; set; }
    [JsonRequired] public int Active { get; set; }
    [JsonRequired] public int Admission { get; set; }
    [JsonRequired] public long Reserved { get; set; }
    [JsonRequired] public int Samples { get; set; }
    [JsonRequired] public int[] GcCounts { get; set; } = new int[3];
}
internal sealed record CompareDelta(double? Absolute, double? RelativePercent);
internal sealed record ComparePair(int Repetition, string[] Order, List<CompareArm> Arms)
{
    public bool Valid => Arms.Count == 2 && Arms.All(a => a.Valid && (a.Name is "baseline" or "mask")
        && a.Metrics().Values.All(v => v is not null && double.IsFinite(v.Value) && v.Value >= 0))
        && Arms.Select(a => a.Name).Distinct().Count() == 2;
    public Dictionary<string, CompareDelta>? Deltas => !Valid ? null : Arms.Single(a => a.Name == "baseline").Metrics()
        .ToDictionary(k => k.Key, k => CompareMath.Delta(k.Value, Arms.Single(a => a.Name == "mask").Metrics()[k.Key]));
}
internal sealed record CompareReport(string Status, CompareEnvironment Environment, int RequestsPerArm, int Repetitions,
    int Concurrency, List<ComparePair> Pairs, string? Failure)
{
    public string Schema => "gateway.rnf03.compare.v1";
    public string Classification => "LOCAL_OBSERVATIONAL_COMPARISON";
    public string PerformanceAcceptance => "not_evaluated";
    public long? MemoryTargetBytes => null;
    public string Configuration => "synthetic candidate: admission=8; reservation=8388608; inspections=6; inspectionTimeoutSeconds=10; headersDeadline=off; NOT application defaults admission=6/reservation=6291456";
    public string Measurement => "gateway process CPU and allocations include sampler, protocol and runtime; client window covers validated full bodies, worker window covers begin through cleanup/end; windows differ. Sampled memory peaks every 10ms can miss transients; no forced GC, no Job, no hard limit, no sizing decision. Closed-loop local synthetic traffic only.";
    public int ValidPairs => Pairs.Count(p => p.Valid);
    public Dictionary<string, CompareDelta> MedianPairDeltas => Pairs.Where(p => p.Valid).SelectMany(p => p.Deltas!)
        .GroupBy(k => k.Key).ToDictionary(g => g.Key, g => new CompareDelta(CompareMath.Median(g.Select(k => k.Value.Absolute)),
            CompareMath.Median(g.Select(k => k.Value.RelativePercent))));
    internal string Markdown()
    {
        var s = new StringBuilder($"# RNF03 comparison: {Status}\n\n{Classification}\n\nPerformance acceptance: {PerformanceAcceptance}\nMemory target: null\nFailure: {Failure ?? "null"}\n\n{Configuration}\n\n{Measurement}\n\nCompiled revision: {Environment.CompiledRevision}\nCompiled harness SHA256: {Environment.CompiledHarnessSha256}\nCompiled gateway SHA256: {Environment.CompiledGatewaySha256}\nCompiled API SHA256: {Environment.CompiledApiSha256}\nBuild: {Environment.BuildConfiguration}; runtime: {Environment.Runtime}; OS: {Environment.OS}; architecture: {Environment.Architecture}; processors: {Environment.ProcessorCount}; server GC: {Environment.ServerGC}\nRequests per arm: {RequestsPerArm}; pairs: {Repetitions}; concurrency: {Concurrency}\n\n");
        s.AppendLine("|Pair|Arm|Valid|Validated requests|Client ms|p50 ms|p95 ms|p99 ms|RPS|CPU ms|Allocated bytes|Failure|\n|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var p in Pairs) foreach (var a in p.Arms)
            s.AppendLine($"|{p.Repetition}|{a.Name}|{a.Valid}|{a.ValidatedRequests}|{a.ClientElapsedMs}|{a.P50Ms}|{a.P95Ms}|{a.P99Ms}|{a.RequestsPerSecond}|{a.Gateway?.CpuMs}|{a.Gateway?.AllocatedBytes}|{a.Failure}|");
        s.AppendLine("\nPair deltas are mask minus baseline; relative percent is null for zero baseline. Medians below are paired deltas, never pooled percentiles. JSON is canonical for all windows, peaks and partial observations.\n");
        s.AppendLine("|Pair|Metric|Delta mask minus baseline|Relative percent|\n|---:|---|---:|---:|");
        foreach (var p in Pairs.Where(p => p.Valid)) foreach (var d in p.Deltas!)
            s.AppendLine($"|{p.Repetition}|{d.Key}|{d.Value.Absolute?.ToString() ?? "null"}|{d.Value.RelativePercent?.ToString() ?? "null"}|");
        foreach (var d in MedianPairDeltas) s.AppendLine($"- Median pair delta {d.Key}: {d.Value.Absolute?.ToString() ?? "null"}; relative percent: {d.Value.RelativePercent?.ToString() ?? "null"}");
        return s.ToString();
    }
}
internal static class CompareMath
{
    internal static double? Percentile(IEnumerable<double> values, double q)
    {
        var a = values.Order().ToArray();
        return a.Length == 0 || !double.IsFinite(q) || q <= 0 || q > 1 || a.Any(x => !double.IsFinite(x) || x < 0)
            ? null : a[(int)Math.Ceiling(q * a.Length) - 1];
    }
    internal static double? Median(IEnumerable<double?> values)
    {
        var a = values.ToArray();
        if (a.Length == 0 || a.Any(x => x is null || !double.IsFinite(x.Value))) return null;
        var s = a.Select(x => x!.Value).Order().ToArray(); var m = s.Length / 2;
        return s.Length % 2 == 1 ? s[m] : s[m - 1] / 2 + s[m] / 2;
    }
    internal static double? Ratio(double? value, double? denominator) => value is >= 0 && denominator is > 0
        && double.IsFinite(value.Value) && double.IsFinite(denominator.Value) && double.IsFinite(value.Value / denominator.Value) ? value / denominator : null;
    internal static CompareDelta Delta(double? baseline, double? mask)
    {
        if (baseline is null || mask is null || baseline < 0 || mask < 0 || !double.IsFinite(baseline.Value) || !double.IsFinite(mask.Value)) return new(null, null);
        var d = mask.Value - baseline.Value;
        var relative = baseline == 0 ? (double?)null : d / baseline * 100;
        return new(double.IsFinite(d) ? d : null, relative is not null && double.IsFinite(relative.Value) ? relative : null);
    }
}
