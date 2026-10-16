using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.LoadHarness;
using Xunit;

namespace Gateway.Tests;

[CollectionDefinition("Isolated harness", DisableParallelization = true)]
public sealed class IsolatedHarnessCollection;

[Collection("Isolated harness")]
public sealed class IsolatedHarnessTests
{
    [Fact]
    public void Origin_accepts_only_canonical_loopback_http_roots()
    {
        foreach (var port in new[] { 1, 8080, 65535 })
        {
            var origin = IsolatedWorkers.Origin($"http://127.0.0.1:{port}/");
            Assert.Equal("127.0.0.1", origin.Host);
            Assert.Equal(port, origin.Port);
            Assert.Equal("/", origin.AbsolutePath);
            Assert.Equal("http", origin.Scheme);
        }

        // Keep input data out of runner-generated case names and failure messages.
        string[] rejected =
        [
            "http://192.0.2.1:8080/", "http://localhost:8080/",
            "https://127.0.0.1:8080/", "http://127.0.0.1:8080/path",
            "http://127.0.0.1:8080/?q=1", "http://127.0.0.1:8080/#fragment",
            "http://user@127.0.0.1:8080/", "http://127.0.0.1:0/",
            "http://127.0.0.1:65536/", "http://127.0.0.1:-1/",
            "http://127.0.0.1:invalid/"
        ];
        foreach (var value in rejected)
            Assert.Throws<InvalidOperationException>(() => IsolatedWorkers.Origin(value));
    }

    [Fact]
    public async Task Isolated_run_separates_processes_ignores_inherited_endpoints_and_records_exact_invariants()
    {
        const string key = "Kestrel__Endpoints__Injected__Url";
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "https://0.0.0.0:0");
        try
        {
            await VerifyRun(limitMiB: 1024, headroom: 1, allowed: 1063004405, candidate: 8,
                expectedSchedule: [1, 2, 4, 8], expectedPass: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Fact]
    public Task Isolated_run_fails_sizing_when_selected_limit_is_too_small() =>
        VerifyRun(limitMiB: 64, headroom: 90, allowed: 6710886, candidate: 8,
            expectedSchedule: [1, 2, 4, 8], expectedPass: false);

    [Fact]
    public Task Isolated_run_with_candidate_seven_uses_1_2_4_7_and_preserves_budget_invariants() =>
        VerifyRun(limitMiB: 1024, headroom: 1, allowed: 1063004405, candidate: 7,
            expectedSchedule: [1, 2, 4, 7], expectedPass: true);

    [Fact]
    public Task Isolated_run_with_candidate_four_deduplicates_schedule_and_preserves_budget_invariants() =>
        VerifyRun(limitMiB: 1024, headroom: 1, allowed: 1063004405, candidate: 4,
            expectedSchedule: [1, 2, 4], expectedPass: true);

    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    public async Task Isolated_run_rejects_out_of_range_candidate_before_workers_and_without_report(int candidate)
    {
        var output = Path.Combine(Path.GetTempPath(), "gateway-isolated-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var run = IsolatedHarness.Run(output, 8, 1, 512, 30, candidate, CancellationToken.None);
            // Validation must fault before the first asynchronous worker startup/readiness wait.
            Assert.True(run.IsFaulted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => run);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static async Task VerifyRun(int limitMiB, int headroom, long allowed, int candidate,
        int[] expectedSchedule, bool expectedPass)
    {
        var output = Path.Combine(Path.GetTempPath(), "gateway-isolated-tests", Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        long parentMeasurements = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PrivacyTelemetry.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref parentMeasurements));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref parentMeasurements));
        listener.Start();
        try
        {
            var passed = await IsolatedHarness.Run(output, 8, 1, limitMiB, headroom, candidate, deadline.Token);
            Assert.Equal(expectedPass, passed);
            Assert.Equal(0L, Interlocked.Read(ref parentMeasurements));
            var json = await File.ReadAllTextAsync(Path.Combine(output, "isolated.json"), deadline.Token);
            var markdown = await File.ReadAllTextAsync(Path.Combine(output, "isolated.md"), deadline.Token);
            AssertReportSafe(json);
            AssertReportSafe(markdown);
            using var document = JsonDocument.Parse(json);
            var report = document.RootElement;
            var classification = expectedPass ? "SIZED_LOCAL_CANDIDATE" : "SIZING_FAILED";
            Assert.Equal("gateway.mvp06.isolated.v1", report.GetProperty("schema").GetString());
            Assert.Equal(expectedPass ? "passed" : "failed", report.GetProperty("status").GetString());
            Assert.Equal(classification, report.GetProperty("classification").GetString());
            Assert.Contains(classification, markdown);
            Assert.Equal(allowed, report.GetProperty("allowedBytes").GetInt64());
            Assert.Equal(limitMiB * 1048576L, report.GetProperty("targetBytes").GetInt64());
            Assert.Equal(headroom, report.GetProperty("headroomPercent").GetInt32());
            Assert.Equal("privateBytes", report.GetProperty("sizingMetric").GetString());
            Assert.Equal(8, report.GetProperty("requestsPerConcurrency").GetInt32());
            Assert.Equal(1, report.GetProperty("repetitions").GetInt32());
            Assert.Equal(candidate, report.GetProperty("maxConcurrentInspections").GetInt32());
            Assert.Equal(expectedSchedule, report.GetProperty("concurrencies").EnumerateArray().Select(c => c.GetInt32()));
            var decision = report.GetProperty("decision").GetString()!;
            Assert.Contains("Gateway/appsettings defaults remain unchanged", decision);
            Assert.Contains("does not authorize production or changes to defaults", decision);
            Assert.Contains(decision, markdown);
            Assert.Equal(1048576, report.GetProperty("bodyBytes").GetInt32());
            var runs = report.GetProperty("runs").EnumerateArray().ToArray();
            Assert.Equal(expectedSchedule, runs.Select(r => r.GetProperty("concurrency").GetInt32()));
            var expectedTotal = 8 * expectedSchedule.Length;
            Assert.Equal(expectedTotal, runs.Sum(r => r.GetProperty("total").GetInt32()));
            Assert.Equal(expectedTotal, runs.Sum(r => r.GetProperty("outcomes").GetInt64()));
            Assert.Equal(expectedTotal, runs.Sum(r => r.GetProperty("durations").GetInt64()));
            foreach (var run in runs)
            {
                var concurrency = run.GetProperty("concurrency").GetInt32();
                Assert.Equal(1, run.GetProperty("repetition").GetInt32());
                Assert.Equal(8, run.GetProperty("total").GetInt32());
                var status = Assert.Single(run.GetProperty("statusCounts").EnumerateObject());
                Assert.Equal("200", status.Name);
                Assert.Equal(8, status.Value.GetInt32());
                Assert.Equal(8L, run.GetProperty("outcomes").GetInt64());
                Assert.Equal(8L, run.GetProperty("durations").GetInt64());
                Assert.True(run.GetProperty("budgetReleased").GetBoolean());
                Assert.True(run.GetProperty("canary").GetBoolean());
                Assert.Equal(expectedPass, run.GetProperty("withinMemoryLimit").GetBoolean());
                Assert.Equal(expectedPass, run.GetProperty("passed").GetBoolean());
                var peak = run.GetProperty("peak");
                Assert.True(peak.GetProperty("samples").GetInt32() > 0);
                Assert.True(peak.GetProperty("managedBytes").GetInt64() > 0);
                Assert.True(peak.GetProperty("workingSetBytes").GetInt64() > 0);
                var privateBytes = peak.GetProperty("privateBytes").GetInt64();
                Assert.True(privateBytes > 0);
                Assert.Equal(expectedPass, privateBytes <= allowed);
                // Sampling may miss active requests; zero is a valid observation.
                Assert.InRange(peak.GetProperty("active").GetInt32(), 0, concurrency);
                Assert.InRange(peak.GetProperty("reserved").GetInt64(), 0L, concurrency * 1048576L);
            }
            var aggregates = report.GetProperty("aggregates").EnumerateArray().ToArray();
            Assert.Equal(expectedSchedule, aggregates.Select(a => a.GetProperty("concurrency").GetInt32()));
            foreach (var aggregate in aggregates)
            {
                Assert.Equal(1, aggregate.GetProperty("runs").GetInt32());
                Assert.Equal(expectedPass, aggregate.GetProperty("allPassed").GetBoolean());
            }
            Assert.Equal(2, Directory.GetFiles(output).Length);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static void AssertReportSafe(string report)
    {
        // Boolean assertions deliberately avoid echoing a potentially unsafe report.
        foreach (var forbidden in new[] { "PAN", "cardNumber", "http://", "https://", "127.0.0.1", "4111111111111111", "************1111" })
            Assert.False(report.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "Report contains a forbidden marker.");
        Assert.False(Regex.IsMatch(report, @"(?<![\w.])\d{13,19}(?![\w.])"), "Report contains a sensitive numeric pattern.");
        Assert.False(report.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase),
            "Report contains a personal path.");
    }
}
