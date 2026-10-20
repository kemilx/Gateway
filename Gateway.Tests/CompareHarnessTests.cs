using System.Net;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gateway.LoadHarness;
using Xunit;

namespace Gateway.Tests;

[Collection("Isolated harness")]
public sealed class CompareHarnessTests
{
    [Fact]
    public void Percentiles_use_sorted_nearest_rank_and_medians_use_pair_values()
    {
        double[] values = [100, 1, 4, 2, 3];
        Assert.Equal(3, CompareMath.Percentile(values, .5));
        Assert.Equal(100, CompareMath.Percentile(values, .95));
        Assert.Equal(100, CompareMath.Percentile(values, .99));
        Assert.Equal(1, CompareMath.Percentile([1], .99));
        var hundred = Enumerable.Range(1, 100).Select(x => (double)x).Reverse();
        Assert.Equal(50, CompareMath.Percentile(hundred, .5));
        Assert.Equal(95, CompareMath.Percentile(hundred, .95));
        Assert.Equal(99, CompareMath.Percentile(hundred, .99));
        Assert.Equal(5, CompareMath.Median([9, 1]));
        Assert.Equal(-2, CompareMath.Median([-10, -2, 20]));
        Assert.Equal(new CompareDelta(5, 25), CompareMath.Delta(20, 25));
        Assert.Equal(new CompareDelta(-5, -25), CompareMath.Delta(20, 15));
        Assert.Equal(new CompareDelta(5, null), CompareMath.Delta(0, 5));
        Assert.Equal(4, CompareMath.Ratio(20, 5));
        Assert.Equal(0, CompareMath.Ratio(0, 5));
        Assert.Equal(new CompareDelta(0, null), CompareMath.Delta(0, 0));
        var arm = Arm("baseline", 1);
        Assert.Equal(.125, arm.Metrics()["cpuMsPerRequest"]);
        Assert.Equal(1.25, arm.Metrics()["allocatedBytesPerRequest"]);
    }

    [Fact]
    public void Missing_nonfinite_negative_measurements_never_become_zero_metrics()
    {
        Assert.Null(CompareMath.Percentile([], .5));
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
        {
            Assert.Null(CompareMath.Percentile([1, value], .5));
            Assert.Null(CompareMath.Ratio(value, 1));
            Assert.Null(CompareMath.Ratio(1, value));
            Assert.Equal(new CompareDelta(null, null), CompareMath.Delta(value, 1));
            Assert.Equal(new CompareDelta(null, null), CompareMath.Delta(1, value));
        }
        foreach (var q in new[] { 0d, -1d, 1.01, double.NaN, double.PositiveInfinity }) Assert.Null(CompareMath.Percentile([1], q));
        Assert.Null(CompareMath.Median([])); Assert.Null(CompareMath.Median([1, null]));
        Assert.Null(CompareMath.Median([1, double.NaN]));
        Assert.Null(CompareMath.Median([double.PositiveInfinity]));
        Assert.Null(CompareMath.Ratio(null, 1)); Assert.Null(CompareMath.Ratio(1, 0));
        Assert.Null(CompareMath.Ratio(1, null)); Assert.Null(CompareMath.Ratio(double.MaxValue, double.Epsilon));
        Assert.Equal(new CompareDelta(null, null), CompareMath.Delta(null, 1));
        Assert.Equal(new CompareDelta(null, null), CompareMath.Delta(1, null));
        Assert.Null(CompareMath.Delta(double.Epsilon, double.MaxValue).RelativePercent);
    }

    [Fact]
    public void Pairs_alternate_AB_BA_and_aggregate_only_valid_paired_deltas()
    {
        Assert.Equal(new[] { "baseline", "mask", "mask", "baseline", "baseline", "mask", "mask", "baseline" },
            Enumerable.Range(1, 4).SelectMany(CompareHarness.Order));
        var first = Pair(1, 1, 9); var second = Pair(2, 100, 101); var invalid = Pair(3, 1, 999);
        invalid.Arms[1].Valid = false;
        var report = new CompareReport("failed", CompareEnvironment.Current(), 8, 3, 1, [first, second, invalid], "body_invalid");
        Assert.Equal(2, report.ValidPairs);
        Assert.Equal(4.5, report.MedianPairDeltas["p99Ms"].Absolute); // median(8,1), not difference of pooled tails
        Assert.Equal(400.5, report.MedianPairDeltas["p99Ms"].RelativePercent);
        Assert.Equal(800, first.Deltas!["p99Ms"].RelativePercent);
        Assert.Null(invalid.Deltas);
        var missing = new ComparePair(4, CompareHarness.Order(4), [new("baseline") { Valid = true }, new("mask") { Valid = true }]);
        Assert.False(missing.Valid); Assert.Null(missing.Deltas);
        Assert.False(new ComparePair(1, CompareHarness.Order(1), [first.Arms[0]]).Valid);
        Assert.False(new ComparePair(1, CompareHarness.Order(1), [first.Arms[0], first.Arms[0]]).Valid);
    }

    [Fact]
    public async Task Arguments_and_output_are_validated_before_processes_or_files()
    {
        using var root = new Workspace();
        var output = root.NewOutput();
        var defaults = CompareHarness.Parse(new Dictionary<string, string> { ["--output"] = output });
        Assert.Equal((256, 2, 6), (defaults.Requests, defaults.Repetitions, defaults.Concurrency));
        foreach (var key in new[] { "--requests-per-arm", "--repetitions", "--concurrency" })
        {
            var min = key == "--requests-per-arm" ? 8 : 1; var max = key == "--requests-per-arm" ? 4096 : key == "--repetitions" ? 4 : 6;
            foreach (var n in new[] { min, max })
                CompareHarness.Parse(new Dictionary<string, string> { ["--output"] = output, [key] = n.ToString() });
            foreach (var n in new[] { (min - 1).ToString(), (max + 1).ToString(), "+1", " 8", "8 ", "1.0", "99999999999999999" })
            {
                var bad = new Dictionary<string, string> { ["--output"] = output, [key] = n };
                Assert.ThrowsAny<Exception>(() => CompareHarness.Parse(bad));
            }
        }
        Assert.Throws<InvalidOperationException>(() => CompareHarness.Parse(new Dictionary<string, string>()));
        Assert.Throws<InvalidOperationException>(() => CompareHarness.Parse(new Dictionary<string, string> { ["--output"] = output, ["--unknown"] = "1" }));
        foreach (var invalid in new[] { "", root.Root, Path.Combine(root.Root, "outside"), Path.Combine(root.Root, "artifacts", "mvp05"), Path.Combine(root.Root, "artifacts", "mvp05", "..", "escape") })
        {
            var pending = CompareHarness.Run(new(invalid, 8, 1, 1), CancellationToken.None);
            Assert.True(pending.IsFaulted); await Assert.ThrowsAnyAsync<Exception>(() => pending);
        }
        Assert.False(Directory.Exists(Path.Combine(root.Root, "artifacts")));
        var run = CompareHarness.Run(new(output, 7, 1, 1), CancellationToken.None);
        Assert.True(run.IsFaulted); await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.False(Directory.Exists(output));
        Directory.CreateDirectory(output); var marker = Path.Combine(output, "compare.json"); await File.WriteAllTextAsync(marker, "preserve");
        Assert.Throws<InvalidOperationException>(() => CompareHarness.Parse(new Dictionary<string, string> { ["--output"] = output }));
        Assert.Equal("preserve", await File.ReadAllTextAsync(marker));
        var file = root.NewOutput(); await File.WriteAllTextAsync(file, "preserve");
        Assert.Throws<InvalidOperationException>(() => CompareHarness.Parse(new Dictionary<string, string> { ["--output"] = file }));
    }

    [Fact]
    public async Task Protocol_checks_version_order_expected_counts_and_EOF_without_advancing()
    {
        foreach (var line in new[] { "", "{}", "not-json", CompareProtocol.Command("end", 8),
            "{\"version\":\"other\",\"kind\":\"begin\",\"expected\":0}", CompareProtocol.Command("begin", 1),
            CompareProtocol.Command("begin", 3), CompareProtocol.Command("begin", 14) })
        {
            var called = false;
            await Assert.ThrowsAsync<CompareFailure>(() => CompareProtocol.Session(new StringReader(line), _ => Task.CompletedTask,
                _ => { called = true; return Task.FromResult((0L, 0L)); }, _ => Task.FromResult(Window()), () => Task.CompletedTask, CancellationToken.None));
            Assert.False(called);
        }
        foreach (var badEnd in new[] { CompareProtocol.Command("begin", 0), CompareProtocol.Command("end", 7), CompareProtocol.Command("end", 4097), "" })
        {
            var endCalled = false;
            await Assert.ThrowsAsync<CompareFailure>(() => CompareProtocol.Session(new StringReader(CompareProtocol.Command("begin", 0) + "\n" + badEnd),
                _ => Task.CompletedTask, _ => Task.FromResult((0L, 0L)), _ => { endCalled = true; return Task.FromResult(Window()); }, () => Task.CompletedTask, CancellationToken.None));
            Assert.False(endCalled);
        }
        var sends = new List<string>(); var stopped = false;
        await CompareProtocol.Session(new StringReader(string.Join('\n', CompareProtocol.Command("begin", 12), CompareProtocol.Command("end", 8), CompareProtocol.Command("stop"))),
            o => { sends.Add(JsonSerializer.Serialize(o, Common.Json)); return Task.CompletedTask; },
            n => { Assert.Equal(12, n); return Task.FromResult((12L, 12L)); }, n => { Assert.Equal(8, n); return Task.FromResult(Window()); },
            () => { stopped = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(stopped); Assert.Equal(3, sends.Count);
        for (var i = 0; i < sends.Count; i++)
        {
            using var json = JsonDocument.Parse(sends[i]); CompareProtocol.Validate(json.RootElement, new[] { "begin", "end", "stopped" }[i]);
        }
        foreach (var badStop in new[] { "", CompareProtocol.Command("begin", 0), "{\"version\":\"other\",\"kind\":\"stop\"}" })
        {
            var stopCalled = false; var emitted = 0;
            var input = string.Join('\n', CompareProtocol.Command("begin", 0), CompareProtocol.Command("end", 8), badStop);
            await Assert.ThrowsAsync<CompareFailure>(() => CompareProtocol.Session(new StringReader(input), _ => { emitted++; return Task.CompletedTask; },
                _ => Task.FromResult((0L, 0L)), _ => Task.FromResult(Window()), () => { stopCalled = true; return Task.CompletedTask; }, CancellationToken.None));
            Assert.False(stopCalled); Assert.Equal(2, emitted);
        }
        foreach (var n in new[] { 8, 4096 })
            Assert.Equal(n, await CompareProtocol.Read(new StringReader(CompareProtocol.Command("end", n)), "end", CancellationToken.None));
    }

    [Fact]
    public async Task Protocol_EOF_timeout_and_worker_termination_fail_bounded()
    {
        var eof = await Assert.ThrowsAsync<CompareFailure>(() => CompareProtocol.Read(new StringReader(""), "begin", CancellationToken.None));
        Assert.Equal("protocol_eof", eof.Code);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompareProtocol.Read(new WaitingReader(), "begin", timeout.Token));
        await using var child = new IsolatedHarness.Child("compare-gateway-worker", "--upstream", "invalid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() => child.Ready(deadline.Token));
        await child.DisposeAsync(); // exited child: pipe EOF, bounded drain and disposal; never a success report
    }

    [Fact]
    public async Task Client_requires_exact_full_body_and_retains_partial_observations_on_failure()
    {
        var expected = CompareHarness.Expected(false); var altered = (byte[])expected.Clone(); altered[^3] ^= 1;
        foreach (var body in new[] { altered, expected[..^1], expected.Concat(new byte[] { 1 }).ToArray(), CompareHarness.Expected(true) })
        {
            using var client = new HttpClient(new ResponseHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }))) { BaseAddress = new Uri("http://127.0.0.1/") };
            var error = await Assert.ThrowsAsync<CompareFailure>(() => CompareHarness.Request(client, "/", expected, CancellationToken.None));
            Assert.Equal("body_invalid", error.Code);
        }
        var calls = 0;
        using var partial = new HttpClient(new ResponseHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(++calls == 1 ? expected : altered) })))
        { BaseAddress = new Uri("http://127.0.0.1/") };
        var arm = new CompareArm("baseline");
        await Assert.ThrowsAsync<CompareFailure>(() => CompareHarness.Load(partial, "/", expected, 8, 1, arm, CancellationToken.None));
        Assert.Equal(1, arm.ValidatedRequests); Assert.True(arm.ClientElapsedMs > 0); Assert.False(arm.Valid); Assert.Null(arm.Gateway);
        using var slow = new HttpClient(new ResponseHandler(async token => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }))
        { BaseAddress = new Uri("http://127.0.0.1/") };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompareHarness.Request(slow, "/", expected, timeout.Token));
    }

    [Fact]
    public void Telemetry_requires_baseline_zero_mask_exact_counts_and_released_nonnegative_metrics()
    {
        var options = new CompareOptions("unused", 8, 1, 1);
        var baseline = Arm("baseline", 1); CompareHarness.ValidateWindow(baseline, options);
        var mask = Arm("mask", 2); mask.Gateway = Window() with { Outcomes = 8, Durations = 8 }; CompareHarness.ValidateWindow(mask, options);
        var valid = mask.Gateway;
        foreach (var invalid in new[] { valid with { Outcomes = 7 }, valid with { Durations = 9 }, valid with { CpuMs = -1 },
            valid with { CpuMs = double.NaN }, valid with { AllocatedBytes = -1 }, valid with { ElapsedMs = 0 },
            valid with { Released = false }, valid with { GcEnd = [-1, 0, 0] }, valid with { Peak = new ComparePeak() } })
        {
            mask.Gateway = invalid;
            Assert.Equal("metrics_invalid", Assert.Throws<CompareFailure>(() => CompareHarness.ValidateWindow(mask, options)).Code);
        }
        baseline.Gateway = valid;
        Assert.Throws<CompareFailure>(() => CompareHarness.ValidateWindow(baseline, options));
    }

    [Fact]
    public async Task Warmup_completes_two_full_waves_and_preserves_validated_partial_counts()
    {
        var expected = new byte[] { 1, 2, 3 }; var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new HttpClient(new ResponseHandler(async token =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 2) started.SetResult();
            if (n <= 2) await release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(expected) };
        }))
        { BaseAddress = new Uri("http://127.0.0.1/") };
        var arm = new CompareArm("baseline");
        var warmup = CompareHarness.Warmup(client, "/", expected, 2, arm, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        Assert.Equal(2, Volatile.Read(ref calls)); Assert.False(warmup.IsCompleted);
        release.SetResult(); await warmup;
        Assert.Equal(4, calls); Assert.Equal(4, arm.Warmups);
        var count = 0;
        using var bad = new HttpClient(new ResponseHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(++count == 1 ? expected : new byte[] { 9 }) })))
        { BaseAddress = new Uri("http://127.0.0.1/") };
        await Assert.ThrowsAsync<CompareFailure>(() => CompareHarness.Warmup(bad, "/", expected, 1, arm, timeout.Token));
        Assert.Equal(1, arm.Warmups); Assert.Equal(2, count);
    }

    [Fact]
    public async Task Cancelled_execution_emits_failed_report_without_successful_partial_pair()
    {
        using var workspace = new Workspace(); var output = workspace.NewOutput();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.False(await CompareHarness.Run(new(output, 8, 1, 1), cancelled.Token));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "compare.json")));
        var r = document.RootElement; Assert.Equal("failed", r.GetProperty("status").GetString());
        Assert.Equal("timeout", r.GetProperty("failure").GetString()); Assert.Equal(0, r.GetProperty("validPairs").GetInt32());
        Assert.Empty(r.GetProperty("medianPairDeltas").EnumerateObject());
        var arms = r.GetProperty("pairs")[0].GetProperty("arms"); Assert.Equal(2, arms.GetArrayLength());
        Assert.Equal("timeout", arms[0].GetProperty("failure").GetString());
        Assert.Equal("not_run", arms[1].GetProperty("failure").GetString());
        Assert.Equal(JsonValueKind.Null, arms[0].GetProperty("gateway").ValueKind);
        Assert.Equal(JsonValueKind.Null, arms[1].GetProperty("validatedRequests").ValueKind);
        AssertSafe(r.GetRawText());
    }

    [Fact]
    public async Task Real_minimum_compare_validates_both_arms_and_reports_only_safe_observations()
    {
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = RepositoryRoot();
        var output = Path.Combine(RepositoryRoot(), "artifacts", "mvp05", "compare-test-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            Assert.True(await CompareHarness.Run(new(output, 8, 1, 1), timeout.Token));
            var json = await File.ReadAllTextAsync(Path.Combine(output, "compare.json"));
            var markdown = await File.ReadAllTextAsync(Path.Combine(output, "compare.md"));
            using var document = JsonDocument.Parse(json); var r = document.RootElement;
            Assert.Equal("gateway.rnf03.compare.v1", r.GetProperty("schema").GetString());
            Assert.Equal("LOCAL_OBSERVATIONAL_COMPARISON", r.GetProperty("classification").GetString());
            Assert.Equal("completed", r.GetProperty("status").GetString());
            Assert.Equal("not_evaluated", r.GetProperty("performanceAcceptance").GetString());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("memoryTargetBytes").ValueKind); Assert.Equal(1, r.GetProperty("validPairs").GetInt32());
            var arms = r.GetProperty("pairs")[0].GetProperty("arms");
            for (var i = 0; i < 2; i++)
            {
                var arm = arms[i]; var w = arm.GetProperty("gateway"); var n = i == 0 ? 0 : 8;
                Assert.True(arm.GetProperty("valid").GetBoolean()); Assert.Equal(8, arm.GetProperty("validatedRequests").GetInt32());
                Assert.Equal(2, arm.GetProperty("warmups").GetInt32()); Assert.Equal(i == 0 ? 0 : 2, arm.GetProperty("warmupOutcomes").GetInt64());
                Assert.Equal(i == 0 ? 0 : 2, arm.GetProperty("warmupDurations").GetInt64());
                Assert.Equal(n, w.GetProperty("outcomes").GetInt64()); Assert.Equal(n, w.GetProperty("durations").GetInt64());
                Assert.True(w.GetProperty("released").GetBoolean()); Assert.True(w.GetProperty("peak").GetProperty("samples").GetInt32() > 0);
                Assert.True(w.GetProperty("allocatedBytes").GetInt64() > 0); Assert.True(w.GetProperty("cpuMs").GetDouble() >= 0);
                Assert.True(w.GetProperty("elapsedMs").GetDouble() > 0); Assert.True(arm.GetProperty("requestsPerSecond").GetDouble() > 0);
                Assert.Equal(3, w.GetProperty("gcStart").GetArrayLength()); Assert.Equal(3, w.GetProperty("gcEnd").GetArrayLength());
            }
            foreach (var key in new[] { "compiledHarnessSha256", "compiledGatewaySha256", "compiledApiSha256" })
                Assert.Matches("^[A-F0-9]{64}$", r.GetProperty("environment").GetProperty(key).GetString()!);
            AssertSafe(json); AssertSafe(markdown);
            var existing = CompareHarness.Run(new(output, 8, 1, 1), timeout.Token);
            Assert.True(existing.IsFaulted); await Assert.ThrowsAsync<InvalidOperationException>(() => existing);
            Assert.Equal(json, await File.ReadAllTextAsync(Path.Combine(output, "compare.json")));
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    [Fact]
    public void Failed_report_preserves_null_missing_metrics_without_sensitive_serialization()
    {
        var report = new CompareReport("failed", CompareEnvironment.Current(), 8, 1, 1,
            [new(1, CompareHarness.Order(1), [new("baseline") { Failure = "timeout" }])], "timeout");
        var json = JsonSerializer.Serialize(report, Common.Json); AssertSafe(json); AssertSafe(report.Markdown());
        using var document = JsonDocument.Parse(json); var arm = document.RootElement.GetProperty("pairs")[0].GetProperty("arms")[0];
        foreach (var key in new[] { "gateway", "validatedRequests", "clientElapsedMs", "p50Ms", "p95Ms", "p99Ms", "requestsPerSecond", "warmups" })
            Assert.Equal(JsonValueKind.Null, arm.GetProperty(key).ValueKind);
        Assert.Empty(report.MedianPairDeltas); Assert.Equal(0, report.ValidPairs);
    }

    [Fact]
    public void Worker_window_requires_every_field_and_correct_types_without_defaulting_to_zero()
    {
        var valid = JsonSerializer.SerializeToNode(Window(), Common.Json)!.AsObject();
        var options = new CompareOptions("unused", 8, 1, 1);
        var arm = Arm("baseline", 1);
        CompareHarness.AcceptWindow(JsonSerializer.SerializeToElement(valid), arm, options);
        Assert.NotNull(arm.Gateway);
        foreach (var nested in new[] { false, true })
        {
            var fields = (nested ? valid["peak"]!.AsObject() : valid).Select(p => p.Key).ToArray();
            foreach (var field in fields)
                foreach (var mutation in new[] { "missing", "wrong_type", "null" })
                {
                    var changed = valid.DeepClone().AsObject(); var target = nested ? changed["peak"]!.AsObject() : changed;
                    if (mutation == "missing") target.Remove(field);
                    else target[field] = mutation == "null" ? null : JsonValue.Create("invalid");
                    arm = Arm("baseline", 1);
                    var error = Assert.Throws<CompareFailure>(() => CompareHarness.AcceptWindow(JsonSerializer.SerializeToElement(changed), arm, options));
                    Assert.Equal("metrics_invalid", error.Code); Assert.Null(arm.Gateway); Assert.False(arm.Valid);
                    Assert.Null(new ComparePair(1, CompareHarness.Order(1), [arm, Arm("mask", 2)]).Deltas);
                }
        }
    }

    [Fact]
    public async Task Stop_ack_requires_real_exit_zero_nonzero_or_hang_invalidates_execution_and_pair()
    {
        foreach (var scenario in new[] { "zero", "nonzero", "hang" })
        {
            using var workspace = new Workspace(); var output = workspace.NewOutput(); var pid = 0;
            using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var completed = await CompareHarness.Run(new(output, 8, 1, 1), watchdog.Token, async (gateway, arm, token) =>
            {
                await CompareHarness.FinishGateway(gateway, arm, token);
                var process = StartStopFixture(scenario); pid = process.Id;
                await using var fixture = new IsolatedHarness.Child(process);
                await CompareHarness.FinishGateway(fixture, arm, token);
                Assert.True(process.HasExited); Assert.Equal(0, process.ExitCode);
            });
            Assert.Equal(scenario == "zero", completed);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "compare.json")));
            var report = document.RootElement;
            Assert.Equal(scenario == "zero" ? "completed" : "failed", report.GetProperty("status").GetString());
            Assert.Equal(scenario == "zero" ? 1 : 0, report.GetProperty("validPairs").GetInt32());
            if (scenario != "zero")
            {
                var arm = report.GetProperty("pairs")[0].GetProperty("arms")[0];
                Assert.False(arm.GetProperty("valid").GetBoolean()); Assert.Equal("cleanup_failed", arm.GetProperty("failure").GetString());
                Assert.Equal(JsonValueKind.Null, report.GetProperty("pairs")[0].GetProperty("deltas").ValueKind);
                Assert.Empty(report.GetProperty("medianPairDeltas").EnumerateObject());
            }
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
    }

    [Fact]
    public async Task Real_worker_open_stdin_without_newline_times_out_and_overlong_line_fails_bounded()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var api = new IsolatedHarness.Child("api-worker"); var upstream = await api.Ready(watchdog.Token);
        foreach (var overlong in new[] { false, true })
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(typeof(CompareHarness).Assembly.Location);
            foreach (var arg in new[] { "compare-gateway-worker", "--upstream", upstream }) start.ArgumentList.Add(arg);
            using var worker = Process.Start(start)!;
            var drain = worker.StandardError.ReadToEndAsync(watchdog.Token);
            try
            {
                var ready = await worker.StandardOutput.ReadLineAsync(watchdog.Token); Assert.NotNull(ready);
                using var json = JsonDocument.Parse(ready); CompareProtocol.Validate(json.RootElement, "ready");
                var elapsed = Stopwatch.StartNew();
                // Keep the redirected pipe OPEN. No newline and no EOF can release the native read.
                await worker.StandardInput.WriteAsync(overlong ? new string('x', 16385) : "{");
                await worker.StandardInput.FlushAsync(watchdog.Token);
                await worker.WaitForExitAsync(watchdog.Token); await drain;
                Assert.NotEqual(0, worker.ExitCode);
                if (!overlong) Assert.InRange(elapsed.Elapsed.TotalSeconds, 25, 110);
            }
            finally
            {
                if (!worker.HasExited) worker.Kill(true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await worker.WaitForExitAsync(cleanup.Token); await drain.WaitAsync(cleanup.Token);
            }
        }
        await api.Stop(watchdog.Token);
    }

    private static Process StartStopFixture(string scenario)
    {
        // Test-owned fixed scripts only: never exposed as an application command or user option.
        var ack = JsonSerializer.Serialize(new { version = CompareProtocol.Version, kind = "stopped" }, IsolatedWorkers.Wire);
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[Console]::ReadLine() | Out-Null; [Console]::Out.WriteLine('" + ack + "'); "
                + (scenario == "hang" ? "[Threading.Thread]::Sleep(60000); exit 1" : "exit " + (scenario == "zero" ? "0" : "1")));
        }
        else
        {
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("read ignored; printf '%s\\n' '" + ack + "'; "
                + (scenario == "hang" ? "sleep 60; exit 1" : "exit " + (scenario == "zero" ? "0" : "1")));
        }
        return Process.Start(start) ?? throw new InvalidOperationException("Test process did not start.");
    }

    private static CompareWindow Window() => new(1, 1, 10, 0, 0, [0, 0, 0], [0, 0, 0], new() { Samples = 1 }, true);
    private static CompareArm Arm(string name, double metric) => new(name)
    { Valid = true, ValidatedRequests = 8, ClientElapsedMs = 1, RequestsPerSecond = 8, P50Ms = metric, P95Ms = metric, P99Ms = metric, Gateway = Window() };
    private static ComparePair Pair(int n, double baseline, double mask) => new(n, CompareHarness.Order(n), [Arm("baseline", baseline), Arm("mask", mask)]);
    private static void AssertSafe(string text)
    {
        foreach (var denied in new[] { TestApi.SyntheticApi.Pan, "************1111", "$.payment.cardNumber", "http://", "https://", Environment.MachineName, "Exception", "--output", "requestArgs" })
            Assert.False(text.Contains(denied, StringComparison.OrdinalIgnoreCase), "Report privacy invariant failed.");
        Assert.False(Regex.IsMatch(text, @"[A-Za-z]:[\\/]|\\\\|/(?:tmp|home|Users)/|(?i)\b(pid|processId|hostname|headers|selector)\b"), "Report identifier/path invariant failed.");
    }
    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ProyectoSeguridad.slnx"))) return d.FullName;
        throw new InvalidOperationException("Repository root unavailable.");
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string previous = Environment.CurrentDirectory;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "opencode", "MVP18-compare", "args-" + Guid.NewGuid().ToString("N"));
        internal Workspace() { Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "ProyectoSeguridad.slnx"), ""); Environment.CurrentDirectory = Root; }
        internal string NewOutput() => Path.Combine(Root, "artifacts", "mvp05", Guid.NewGuid().ToString("N"));
        public void Dispose() { Environment.CurrentDirectory = previous; Directory.Delete(Root, true); }
    }
    private sealed class ResponseHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(cancellationToken);
    }
    private sealed class WaitingReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return null; }
    }
}
