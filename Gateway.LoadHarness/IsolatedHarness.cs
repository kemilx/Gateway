using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Gateway.LoadHarness;

internal static class IsolatedHarness
{
    internal static async Task<bool> Run(string output, int requests, int repetitions, int limitMiB, int headroom, int maxConcurrentInspections, CancellationToken ct, bool hardLimit = false)
    {
        Common.Require(!hardLimit || OperatingSystem.IsWindows() && Environment.Is64BitProcess);
        Common.Require(maxConcurrentInspections is >= 4 and <= 8);
        var concurrencies = new[] { 1, 2, 4, maxConcurrentInspections }.Distinct().Order().ToArray();
        var target = limitMiB * 1048576L; var allowed = target * (100 - headroom) / 100;
        var runs = new List<IsolatedRun>(repetitions * concurrencies.Length); var complete = false; var stage = "api-start";
        try
        {
            await using var api = new Child("api-worker");
            var upstream = await api.Ready(ct);
            for (var repetition = 1; repetition <= repetitions; repetition++)
                foreach (var concurrency in concurrencies)
                {
                    IsolatedRun run = hardLimit ? new HardLimitRun(target) : new IsolatedRun();
                    run.Repetition = repetition; run.Concurrency = concurrency; runs.Add(run);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
                    var token = deadline.Token;
                    stage = "gateway-start";
                    await using var gateway = new Child((run as HardLimitRun)?.Enforcement, "gateway-worker", "--upstream", upstream,
                        "--max-concurrent-inspections", maxConcurrentInspections.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var address = await gateway.Ready(token);
                    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
                    stage = "warmup";
                    await Request(client, "/protected/payment", null, token);
                    await Request(client, "/protected/load", Common.BodySize, token);
                    stage = "reset"; await gateway.Command("reset", "reset", token);
                    stage = "load";
                    var times = new ConcurrentBag<double>(); var counts = new ConcurrentDictionary<int, int>(); var next = -1;
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
                        {
                            while (Interlocked.Increment(ref next) < requests)
                            {
                                var started = Stopwatch.GetTimestamp();
                                var status = await Request(client, "/protected/load", Common.BodySize, token);
                                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                                counts.AddOrUpdate(status, 1, (_, value) => value + 1);
                            }
                        }));
                    }
                    finally
                    {
                        watch.Stop(); run.Total = times.Count; run.StatusCounts = counts.OrderBy(x => x.Key).ToDictionary();
                        run.ElapsedMs = watch.Elapsed.TotalMilliseconds; run.RequestsPerSecond = run.Total / watch.Elapsed.TotalSeconds;
                        var sorted = times.Order().ToArray();
                        if (sorted.Length > 0) { run.P50Ms = Percentile(sorted, .5); run.P95Ms = Percentile(sorted, .95); run.MaxMs = sorted[^1]; }
                    }
                    stage = "report"; var response = await gateway.Command($"report {requests}", "report", token);
                    var p = response.GetProperty("peak");
                    var metrics = new WorkerMetrics("report", new Peak(p.GetProperty("managedBytes").GetInt64(), p.GetProperty("workingSetBytes").GetInt64(),
                        p.GetProperty("privateBytes").GetInt64(), p.GetProperty("active").GetInt32(), p.GetProperty("reserved").GetInt64())
                    { Samples = p.GetProperty("samples").GetInt32() }, response.GetProperty("outcomes").GetInt64(), response.GetProperty("durations").GetInt64(), response.GetProperty("budgetReleased").GetBoolean());
                    run.Peak = metrics.Peak; run.Outcomes = metrics.Outcomes; run.Durations = metrics.Durations;
                    run.BudgetReleased = metrics.BudgetReleased; run.Canary = run.Total == requests;
                    run.WithinMemoryLimit = run.Peak.PrivateBytes <= allowed;
                    gateway.VerifyAfter();
                    await gateway.Stop(token);
                    run.Passed = run.Canary && run.BudgetReleased && run.WithinMemoryLimit && run.Outcomes == requests && run.Durations == requests
                        && concurrency <= maxConcurrentInspections && run.Peak.Samples > 0 && run.Peak.Active <= concurrency && run.Peak.Reserved <= concurrency * (long)Common.BodySize;
                }
            await api.Stop(ct); complete = true;
        }
        catch
        {
            if (hardLimit) complete = false;
            Console.Error.WriteLine((hardLimit ? "Hard-limit" : "Isolated") + " failed at fixed stage: " + stage);
        }
        if (hardLimit)
            foreach (var run in runs.Cast<HardLimitRun>())
            {
                run.WithinMemoryLimit &= run.Enforcement.PeakProcessCommitBytes is >= 0 && run.Enforcement.PeakProcessCommitBytes <= allowed;
                run.Passed &= run.WithinMemoryLimit && run.Enforcement.Passes(allowed);
            }
        var passed = complete && runs.Count == repetitions * concurrencies.Length && concurrencies.All(c => c <= maxConcurrentInspections) && runs.All(r => r.Passed);
        var aggregates = runs.GroupBy(r => r.Concurrency).Select(g => new IsolatedAggregate(g.Key, g.Count(),
            g.Max(r => r.Peak.PrivateBytes), Median(g.Select(r => (double)r.Peak.PrivateBytes)), g.Max(r => r.Peak.ManagedBytes), g.Max(r => r.Peak.WorkingSetBytes),
            Median(g.Select(r => r.RequestsPerSecond)), Median(g.Select(r => r.P50Ms)), Median(g.Select(r => r.P95Ms)), g.Count() == repetitions && g.All(r => r.Passed))).ToArray();
        if (hardLimit)
        {
            var hardReport = new HardLimitReport("gateway.mvp08.hard-limit.v1", passed ? "passed" : "failed", passed ? "HARD_LIMIT_LOCAL_CANDIDATE" : "HARD_LIMIT_FAILED",
                Common.Revision, "token-dense-v1", Common.BodySize, requests, repetitions, maxConcurrentInspections, concurrencies, target, headroom, allowed,
                true, true, HardLimitReport.Explanation, runs.Cast<HardLimitRun>().ToList(), aggregates);
            await Common.Save(output, "hard-limit", hardReport, HardLimitReport.Markdown); return passed;
        }
        var report = new IsolatedReport("gateway.mvp06.isolated.v1", passed ? "passed" : "failed", passed ? "SIZED_LOCAL_CANDIDATE" : "SIZING_FAILED",
            Common.Revision, "token-dense-v1", Common.BodySize, requests, repetitions, maxConcurrentInspections, concurrencies, target, headroom, allowed, "privateBytes",
            "Separate API, gateway and client processes; fresh gateway per run. Child self-samples each metric every 10ms, including warmup retained memory after reset; no forced GC. No parent sampling. Sampling can miss transients. Closed-loop full-body validation; monotonic nearest-rank p50/p95. Local synthetic evidence only, not a container hard-limit or sustained-load test. Aborted runs may contain partial observations or zero defaults; consult passed. Overall timeout 10 minutes, run 2 minutes, protocol 30 seconds, release 5 seconds.",
            "Candidate-only: maxConcurrentInspections configures synthetic workers only; Gateway/appsettings defaults remain unchanged. Passing means only this local candidate met the selected limit; it does not authorize production or changes to defaults.", runs, aggregates);
        await Common.Save(output, "isolated", report, Markdown); return passed;
    }
    private static async Task<int> Request(HttpClient client, string path, int? length, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        Common.Require((int)response.StatusCode == 200 && Common.Canary(bytes, length ?? bytes.Length)); return 200;
    }
    private static double Percentile(double[] sorted, double percentile) => sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray(); var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
    private static string Markdown(IsolatedReport r)
    {
        var text = new StringBuilder($"# Isolated sizing: {r.Status}\n\n{r.Classification}\n\nCandidato sintético: maxConcurrentInspections={r.MaxConcurrentInspections}; concurrencias: {string.Join("/", r.Concurrencies)}. No cambia Gateway/appsettings.\n\nSource: {r.SourceRevision}\nProfile: {r.Profile}; body: {r.BodyBytes} bytes; requests/run: {r.RequestsPerConcurrency}; repetitions: {r.Repetitions}.\nTarget: {r.TargetBytes} bytes; headroom: {r.HeadroomPercent}%; allowed: {r.AllowedBytes} bytes; metric: {r.SizingMetric}.\n\n{r.Decision}\n\n{r.Notes}\n\n|Rep|Concurrency|Total|Status counts|Elapsed ms|RPS|p50 ms|p95 ms|max ms|Managed|Working set|Private|Active|Reserved|Outcomes|Durations|Released|Canary|Within limit|Passed|\n|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---|---|\n");
        foreach (var s in r.Runs) text.AppendLine($"|{s.Repetition}|{s.Concurrency}|{s.Total}|{string.Join(", ", s.StatusCounts.Select(x => $"{x.Key}:{x.Value}"))}|{s.ElapsedMs:F2}|{s.RequestsPerSecond:F2}|{s.P50Ms:F2}|{s.P95Ms:F2}|{s.MaxMs:F2}|{s.Peak.ManagedBytes}|{s.Peak.WorkingSetBytes}|{s.Peak.PrivateBytes}|{s.Peak.Active}|{s.Peak.Reserved}|{s.Outcomes}|{s.Durations}|{s.BudgetReleased}|{s.Canary}|{s.WithinMemoryLimit}|{s.Passed}|");
        text.AppendLine("\n|Concurrency|Runs|Max private|Median private|Max managed|Max working set|Median RPS|Median p50 ms|Median p95 ms|All passed|\n|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var a in r.Aggregates) text.AppendLine($"|{a.Concurrency}|{a.Runs}|{a.MaxPrivateBytes}|{a.MedianPrivateBytes:F0}|{a.MaxManagedBytes}|{a.MaxWorkingSetBytes}|{a.MedianRps:F2}|{a.MedianP50Ms:F2}|{a.MedianP95Ms:F2}|{a.AllPassed}|");
        return text.ToString();
    }
    internal sealed class Child : IAsyncDisposable
    {
        private readonly Process? process;
        private readonly WindowsJobProcess? jobProcess;
        private readonly Enforcement? enforcement;
        private readonly Task drain;
        private bool stopped;
        private bool finishRequested;
        private bool disposed;
        private StreamReader StandardOutput => jobProcess?.StandardOutput ?? process!.StandardOutput;
        private StreamReader StandardError => jobProcess?.StandardError ?? process!.StandardError;
        private StreamWriter StandardInput => jobProcess?.StandardInput ?? process!.StandardInput;
        private bool HasExited => jobProcess?.HasExited ?? process!.HasExited;
        private int ExitCode => jobProcess?.ExitCode ?? process!.ExitCode;
        private Task WaitForExit(CancellationToken ct) => jobProcess?.WaitForExitAsync(ct) ?? process!.WaitForExitAsync(ct);
        private void KillTree() { if (jobProcess is not null) jobProcess.KillTree(); else process!.Kill(true); }
        internal void VerifyAfter() { if (enforcement is not null) jobProcess!.Snapshot(enforcement, before: false); }
        internal Child(params string[] args) : this(null, args) { }
        // Ownership seam for tests of real process exit/drain behavior; no CLI launcher.
        internal Child(Process ownedProcess)
        {
            process = ownedProcess;
            drain = Drain();
        }
        internal Child(Enforcement? enforcement, params string[] args)
        {
            this.enforcement = enforcement;
            if (enforcement is not null)
            {
                Common.Require(args.FirstOrDefault() == "gateway-worker");
                jobProcess = WindowsJobProcess.Start(enforcement.RequestedBytes, enforcement, args);
            }
            else
            {
                var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                start.ArgumentList.Add(typeof(IsolatedHarness).Assembly.Location);
                foreach (var arg in args) start.ArgumentList.Add(arg);
                process = Process.Start(start) ?? throw new InvalidOperationException();
            }
            drain = Drain();
        }
        private async Task Drain()
        {
            var buffer = new char[1024];
            while (await StandardError.ReadAsync(buffer) != 0) { }
        }
        internal async Task<string> Ready(CancellationToken ct)
        {
            var ready = await Read("ready", ct); var address = ready.GetProperty("address").GetString()!;
            return IsolatedWorkers.Origin(address).AbsoluteUri;
        }
        private async Task<JsonElement> Read(string kind, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            // Anonymous Win32 pipes are synchronous handles. Bound the await as well as
            // passing cancellation: an in-flight native read may only unblock on cleanup.
            var read = StandardOutput.ReadLineAsync(deadline.Token);
            var line = jobProcess is null ? await read : await read.AsTask().WaitAsync(deadline.Token);
            Common.Require(line is not null && line.Length <= 16384);
            using var document = JsonDocument.Parse(line!);
            Common.Require(document.RootElement.GetProperty("kind").GetString() == kind);
            return document.RootElement.Clone();
        }
        internal async Task<JsonElement> Command(string command, string kind, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var write = StandardInput.WriteLineAsync(command.AsMemory(), deadline.Token);
            if (jobProcess is null) await write; else await write.WaitAsync(deadline.Token);
            var flush = StandardInput.FlushAsync(deadline.Token);
            if (jobProcess is null) await flush; else await flush.WaitAsync(deadline.Token);
            return await Read(kind, deadline.Token);
        }
        internal async Task Stop(CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await Command("stop", "stopped", deadline.Token);
            await WaitForCleanExit(deadline.Token);
        }
        internal async Task FinishStop(CancellationToken ct)
        {
            // A versioned stop was already acknowledged: never send a legacy command.
            finishRequested = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await WaitForCleanExit(deadline.Token);
        }
        private async Task WaitForCleanExit(CancellationToken ct)
        {
            await WaitForExit(ct); await drain.WaitAsync(ct);
            Common.Require(ExitCode == 0); stopped = true;
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            if (enforcement is not null) enforcement.Cleanup = false;
            try
            {
                if (enforcement is { VerificationAfter: null })
                {
                    // Preserve post-load observations on aborted runs too, without skipping cleanup.
                    try { VerifyAfter(); }
                    catch { enforcement.VerificationAfter = false; }
                }
                if (!stopped && !HasExited)
                {
                    if (finishRequested) KillTree();
                    else
                    {
                        try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await Stop(deadline.Token); }
                        catch { if (!HasExited) KillTree(); }
                    }
                }
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await WaitForExit(exit.Token); await drain.WaitAsync(exit.Token);
                if (enforcement is not null) jobProcess!.FinalPeak(enforcement);
            }
            finally { process?.Dispose(); jobProcess?.Dispose(); }
            if (enforcement is not null) enforcement.Cleanup = stopped;
        }
    }
}
internal class IsolatedRun
{
    public int Repetition { get; set; }
    public int Concurrency { get; set; }
    public int Total { get; set; }
    public Dictionary<int, int> StatusCounts { get; set; } = [];
    public double ElapsedMs { get; set; }
    public double RequestsPerSecond { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double MaxMs { get; set; }
    public Peak Peak { get; set; } = new(0, 0, 0, 0, 0);
    public long Outcomes { get; set; }
    public long Durations { get; set; }
    public bool BudgetReleased { get; set; }
    public bool Canary { get; set; }
    public bool WithinMemoryLimit { get; set; }
    public bool Passed { get; set; }
}
internal sealed record IsolatedAggregate(int Concurrency, int Runs, long MaxPrivateBytes, double MedianPrivateBytes, long MaxManagedBytes,
    long MaxWorkingSetBytes, double MedianRps, double MedianP50Ms, double MedianP95Ms, bool AllPassed);
internal sealed record IsolatedReport(string Schema, string Status, string Classification, string SourceRevision, string Profile, int BodyBytes,
    int RequestsPerConcurrency, int Repetitions, int MaxConcurrentInspections, int[] Concurrencies, long TargetBytes, int HeadroomPercent, long AllowedBytes, string SizingMetric,
    string Notes, string Decision, List<IsolatedRun> Runs, IsolatedAggregate[] Aggregates);
