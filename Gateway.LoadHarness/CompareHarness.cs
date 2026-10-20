using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Gateway.LoadHarness;

internal sealed record CompareOptions(string Output, int Requests, int Repetitions, int Concurrency);
internal static class CompareHarness
{
    internal static CompareOptions Parse(IReadOnlyDictionary<string, string> options)
    {
        Common.Require(options.Keys.All(k => k is "--output" or "--requests-per-arm" or "--repetitions" or "--concurrency"));
        Common.Require(options.TryGetValue("--output", out var output) && !string.IsNullOrWhiteSpace(output));
        int Number(string key, int fallback, int min, int max)
        {
            var n = options.TryGetValue(key, out var value) ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : fallback;
            Common.Require(n >= min && n <= max); return n;
        }
        var requests = Number("--requests-per-arm", 256, 8, 4096);
        var repetitions = Number("--repetitions", 2, 1, 4); var concurrency = Number("--concurrency", 6, 1, 6);
        var directory = Common.OutputDirectory(output!);
        Common.Require(Path.GetRelativePath(Path.Combine(Environment.CurrentDirectory, "artifacts", "mvp05"), directory) != ".");
        Common.Require(!Directory.Exists(directory) && !File.Exists(directory));
        return new(directory, requests, repetitions, concurrency);
    }
    internal static string[] Order(int repetition) => repetition % 2 == 1 ? ["baseline", "mask"] : ["mask", "baseline"];
    internal static byte[] Expected(bool mask)
    {
        var payload = Common.Payload();
        return mask ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace(TestApi.SyntheticApi.Pan, "************1111", StringComparison.Ordinal)) : payload;
    }
    internal static async Task Request(HttpClient client, string path, byte[] expected, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        // Bound a faulty upstream without retaining arbitrary bodies or logging their contents.
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > Common.BodySize)
            throw new CompareFailure("body_invalid");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[expected.Length + 1]; var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), ct); if (count == 0) break; length += count;
        }
        if (length != expected.Length || !buffer.AsSpan(0, length).SequenceEqual(expected)) throw new CompareFailure("body_invalid");
    }
    internal static async Task Load(HttpClient client, string path, byte[] expected, int requests, int concurrency, CompareArm arm, CancellationToken ct)
    {
        var times = new ConcurrentBag<double>(); var next = -1; var watch = Stopwatch.StartNew();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
            {
                while (Interlocked.Increment(ref next) < requests)
                {
                    var start = Stopwatch.GetTimestamp(); await Request(client, path, expected, ct);
                    times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
            }));
        }
        finally
        {
            watch.Stop(); arm.ValidatedRequests = times.Count; arm.ClientElapsedMs = watch.Elapsed.TotalMilliseconds;
            arm.RequestsPerSecond = CompareMath.Ratio(times.Count * 1000d, arm.ClientElapsedMs);
            arm.P50Ms = CompareMath.Percentile(times, .5); arm.P95Ms = CompareMath.Percentile(times, .95); arm.P99Ms = CompareMath.Percentile(times, .99);
        }
    }
    internal static async Task Warmup(HttpClient client, string path, byte[] expected, int concurrency, CompareArm arm, CancellationToken ct)
    {
        var completed = 0;
        try
        {
            for (var wave = 0; wave < 2; wave++)
                await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
                {
                    await Request(client, path, expected, ct); Interlocked.Increment(ref completed);
                }));
        }
        finally { arm.Warmups = completed; }
    }
    internal static async Task<bool> Run(CompareOptions input, CancellationToken ct,
        Func<IsolatedHarness.Child, CompareArm, CancellationToken, Task>? finishGateway = null)
    {
        // Revalidate also for internal callers: no alternate path around the output guard.
        var options = Parse(new Dictionary<string, string>
        {
            ["--output"] = input.Output,
            ["--requests-per-arm"] = input.Requests.ToString(CultureInfo.InvariantCulture),
            ["--repetitions"] = input.Repetitions.ToString(CultureInfo.InvariantCulture),
            ["--concurrency"] = input.Concurrency.ToString(CultureInfo.InvariantCulture)
        });
        Directory.CreateDirectory(options.Output);
        // CreateNew reports below never overwrite evidence, even if another writer races.
        var environment = CompareEnvironment.Current(); var pairs = new List<ComparePair>(); string? failure = null;
        foreach (var repetition in Enumerable.Range(1, options.Repetitions))
        {
            var order = Order(repetition);
            var pair = new ComparePair(repetition, order, order.Select(name => new CompareArm(name) { Failure = "not_run" }).ToList()); pairs.Add(pair);
            foreach (var arm in pair.Arms)
            {
                var name = arm.Name; arm.Failure = null; var stage = "worker_start_failed";
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2)); var token = timeout.Token;
                    await using var api = new IsolatedHarness.Child("api-worker"); var upstream = await api.Ready(token);
                    await using var gateway = new IsolatedHarness.Child("compare-gateway-worker", "--upstream", upstream);
                    var ready = await gateway.Command(CompareProtocol.Command("ready"), "ready", token);
                    CompareProtocol.Validate(ready, "ready");
                    var address = IsolatedWorkers.Origin(ready.GetProperty("address").GetString()!).AbsoluteUri;
                    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None })
                    { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
                    var mask = name == "mask"; var expected = Expected(mask); var path = mask ? "/protected/load" : "/passthrough/load";
                    stage = "warmup_failed";
                    await Warmup(client, path, expected, options.Concurrency, arm, token);
                    stage = "protocol_invalid";
                    Common.Require(arm.Warmups == 2 * options.Concurrency);
                    var warmups = mask ? 2 * options.Concurrency : 0;
                    var begin = await gateway.Command(CompareProtocol.Command("begin", warmups), "begin", token);
                    CompareProtocol.Validate(begin, "begin");
                    arm.WarmupOutcomes = begin.GetProperty("outcomes").GetInt64(); arm.WarmupDurations = begin.GetProperty("durations").GetInt64();
                    Common.Require(arm.WarmupOutcomes == warmups && arm.WarmupDurations == warmups);
                    stage = "load_failed"; await Load(client, path, expected, options.Requests, options.Concurrency, arm, token);
                    stage = "protocol_invalid";
                    var end = await gateway.Command(CompareProtocol.Command("end", options.Requests), "end", token); CompareProtocol.Validate(end, "end");
                    AcceptWindow(end.GetProperty("window"), arm, options);
                    stage = "cleanup_failed";
                    await (finishGateway ?? FinishGateway)(gateway, arm, token);
                    await gateway.DisposeAsync(); await api.Stop(token); await api.DisposeAsync();
                    arm.Valid = true;
                }
                catch (CompareFailure e) { arm.Failure = e.Code; }
                catch (OperationCanceledException) { arm.Failure = "timeout"; }
                catch { arm.Failure = stage; }
                if (!arm.Valid) { failure = arm.Failure; break; }
            }
            if (failure is not null) break;
        }
        var completed = failure is null && pairs.Count == options.Repetitions && pairs.All(p => p.Valid);
        var report = new CompareReport(completed ? "completed" : "failed", environment, options.Requests, options.Repetitions, options.Concurrency, pairs, failure);
        await SaveNew(options.Output, "compare.json", JsonSerializer.Serialize(report, Common.Json));
        await SaveNew(options.Output, "compare.md", report.Markdown());
        return completed;
    }
    internal static async Task FinishGateway(IsolatedHarness.Child gateway, CompareArm arm, CancellationToken ct)
    {
        try
        {
            var stop = await gateway.Command(CompareProtocol.Command("stop"), "stopped", ct); CompareProtocol.Validate(stop, "stopped");
            await gateway.FinishStop(ct);
        }
        catch
        {
            arm.Valid = false; arm.Failure = "cleanup_failed";
            throw new CompareFailure("cleanup_failed");
        }
    }
    internal static void AcceptWindow(JsonElement json, CompareArm arm, CompareOptions options)
    {
        arm.Gateway = null;
        try
        {
            arm.Gateway = json.Deserialize<CompareWindow>(Common.Json) ?? throw new CompareFailure("metrics_invalid");
            ValidateWindow(arm, options);
        }
        catch
        {
            arm.Gateway = null; arm.Valid = false; arm.Failure = "metrics_invalid";
            throw new CompareFailure("metrics_invalid");
        }
    }
    internal static void ValidateWindow(CompareArm arm, CompareOptions options)
    {
        var w = arm.Gateway; var expected = arm.Name == "mask" ? options.Requests : 0;
        if (w is null || !w.Released || w.Outcomes != expected || w.Durations != expected || arm.ValidatedRequests != options.Requests
            || !double.IsFinite(w.ElapsedMs) || w.ElapsedMs <= 0 || !double.IsFinite(w.CpuMs) || w.CpuMs < 0 || w.AllocatedBytes < 0
            || w.Peak is null || w.Peak.Samples <= 0 || w.Peak.Active < 0 || w.Peak.Active > options.Concurrency
            || w.Peak.Admission < 0 || w.Peak.Admission > options.Concurrency || w.Peak.Reserved < 0 || w.Peak.Reserved > options.Concurrency * (long)Common.BodySize
            || w.Peak.ManagedBytes < 0 || w.Peak.PrivateBytes < 0 || w.Peak.WorkingSetBytes < 0
            || (arm.Name == "baseline" && (w.Peak.Active != 0 || w.Peak.Reserved != 0))
            || w.GcStart is not { Length: 3 } || w.GcEnd is not { Length: 3 } || w.GcStart.Where((v, i) => v < 0 || w.GcEnd[i] < v).Any()
            || w.Peak.GcCounts is not { Length: 3 } || w.Peak.GcCounts.Any(v => v < 0)
            || arm.Metrics().Values.Any(v => v is null || !double.IsFinite(v.Value) || v.Value < 0))
            throw new CompareFailure("metrics_invalid");
    }
    private static async Task SaveNew(string directory, string name, string content)
    {
        await using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(file, new UTF8Encoding(false)); await writer.WriteAsync(content);
    }
}
