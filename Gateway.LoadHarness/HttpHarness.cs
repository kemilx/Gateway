using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestApi;

namespace Gateway.LoadHarness;

internal static class HttpHarness
{
    internal static async Task Run(string output, int requests, CancellationToken ct)
    {
        var payload = Common.Payload();
        await using var api = SyntheticApi.Build([], b => b.WebHost.UseUrls("http://127.0.0.1:0"), app => app.MapGet("/load", async context =>
        {
            context.Response.ContentType = "application/json"; context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload, context.RequestAborted);
        }));
        WebApplication? gateway = null;
        try
        {
            await api.StartAsync(ct);
            gateway = GatewayHost.Build([], Address(api) + "/", b =>
            {
                b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0");
                b.Configuration.Sources.Clear();
                b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PanPolicies:InspectionBudget:MaxConcurrentInspections"] = "8",
                    ["PanPolicies:InspectionBudget:MaxReservedBodyBytes"] = "8388608",
                    ["PanPolicies:CoverageContract:Version"] = "v1",
                    ["PanPolicies:CoverageContract:Required:0:RouteId"] = "protected-payment",
                    ["PanPolicies:CoverageContract:Required:0:Selector"] = "$.payment.cardNumber",
                    ["PanPolicies:Policies:0:PolicyId"] = "PAN",
                    ["PanPolicies:Policies:0:Version"] = "v1",
                    ["PanPolicies:Policies:0:RouteId"] = "protected-payment",
                    ["PanPolicies:Policies:0:Mode"] = "Mask",
                    ["PanPolicies:Policies:0:Selector"] = "$.payment.cardNumber",
                    ["PanPolicies:Policies:0:MaxBodyBytes"] = "1048576"
                });
            });
            await gateway.StartAsync(ct);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(30) };
            var budget = gateway.Services.GetRequiredService<InspectionBudget>();
            await Request(client, "/protected/payment", null, ct);
            await Request(client, "/protected/load", payload.Length, ct);
            long outcomes = 0;
            long durations = 0;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == PrivacyTelemetry.MeterName
                    && instrument.Name is "gateway.privacy.inspection.outcomes" or "gateway.privacy.inspection.duration")
                    currentListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref outcomes, value));
            listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref durations));
            listener.Start();
            var scenarios = new List<HttpScenario>(Common.Concurrencies.Length);
            foreach (var n in Common.Concurrencies)
            {
                var outcomesBefore = Interlocked.Read(ref outcomes);
                var durationsBefore = Interlocked.Read(ref durations);
                var before = Common.Measure("before", false, budget);
                var peak = new Peak(before.ManagedBytes, before.WorkingSetBytes, before.PrivateBytes,
                    before.Active, before.Reserved);
                using var sampling = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var sampler = SampleLoop(budget, peak, sampling.Token);
                var times = new ConcurrentBag<double>(); var counts = new ConcurrentDictionary<int, int>(); var next = -1;
                var watch = Stopwatch.StartNew();
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, n).Select(async _ =>
                    {
                        while (Interlocked.Increment(ref next) < requests)
                        {
                            var started = Stopwatch.GetTimestamp();
                            var status = await Request(client, "/protected/load", payload.Length, ct);
                            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds); counts.AddOrUpdate(status, 1, (_, count) => count + 1);
                        }
                    }));
                }
                finally { watch.Stop(); sampling.Cancel(); await sampler; }
                await WaitForCompletion(budget, () => Interlocked.Read(ref outcomes) - outcomesBefore,
                    () => Interlocked.Read(ref durations) - durationsBefore, requests, ct);
                Common.Require(budget.Snapshot == (0, 0L));
                var outcomeCount = Interlocked.Read(ref outcomes) - outcomesBefore;
                var durationCount = Interlocked.Read(ref durations) - durationsBefore;
                Common.Require(outcomeCount == requests && durationCount == requests
                    && peak.Active <= n && peak.Reserved <= n * (long)Common.BodySize);
                var sorted = times.Order().ToArray();
                scenarios.Add(new(n, sorted.Length, counts.OrderBy(x => x.Key).ToDictionary(), watch.Elapsed.TotalMilliseconds,
                    sorted.Length / watch.Elapsed.TotalSeconds, Percentile(sorted, .5), Percentile(sorted, .95), sorted[^1],
                    outcomeCount, durationCount, before, peak, true));
            }
            var report = new HttpReport("gateway.mvp05.http.v1", "passed", "MEASURED_NOT_SIZED", Common.Revision,
                "token-dense-v1", payload.Length,
                "API + Gateway + client share one process. Memory is observational, not isolated; peaks are per-metric sampled maxima at 10ms and can miss transients. Latencies include full body consumption and canary validation; nearest-rank percentiles.", scenarios);
            await Common.Save(output, "http", report, Markdown);
        }
        finally
        {
            try { if (gateway is not null) { try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await gateway.StopAsync(stop.Token); } finally { await gateway.DisposeAsync(); } } }
            finally { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await api.StopAsync(stop.Token); }
        }
    }
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static async Task<int> Request(HttpClient client, string endpoint, int? length, CancellationToken ct)
    {
        using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        Common.Require((int)response.StatusCode == 200 && Common.Canary(bytes, length ?? bytes.Length));
        return (int)response.StatusCode;
    }
    private static async Task SampleLoop(InspectionBudget budget, Peak peak, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var s = Common.Measure("observed", false, budget); peak.ManagedBytes = Math.Max(peak.ManagedBytes, s.ManagedBytes);
                peak.WorkingSetBytes = Math.Max(peak.WorkingSetBytes, s.WorkingSetBytes); peak.PrivateBytes = Math.Max(peak.PrivateBytes, s.PrivateBytes);
                peak.Active = Math.Max(peak.Active, s.Active); peak.Reserved = Math.Max(peak.Reserved, s.Reserved);
                peak.Samples++; await Task.Delay(10, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private static async Task WaitForCompletion(InspectionBudget budget, Func<long> outcomes, Func<long> durations,
        int expected, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (budget.Snapshot != (0, 0L) || outcomes() < expected || durations() < expected)
            await Task.Delay(1, timeout.Token);
    }
    private static double Percentile(double[] values, double p) => values[(int)Math.Ceiling(values.Length * p) - 1];
    private static string Markdown(HttpReport r)
    {
        var text = new StringBuilder($"# HTTP: {r.Status}\n\n{r.Classification}. {r.Notes}\n\n|Concurrency|Total|Requests/sec|p50 ms|p95 ms|max ms|Peak managed bytes|Peak working set bytes|Peak private bytes|\n|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (var s in r.Scenarios) text.AppendLine($"|{s.Concurrency}|{s.Total}|{s.RequestsPerSecond:F2}|{s.P50Ms:F2}|{s.P95Ms:F2}|{s.MaxMs:F2}|{s.Peak.ManagedBytes}|{s.Peak.WorkingSetBytes}|{s.Peak.PrivateBytes}|");
        return text.ToString();
    }
}
internal sealed class Peak(long managed, long working, long privateBytes, int active, long reserved)
{
    public long ManagedBytes { get; set; } = managed;
    public long WorkingSetBytes { get; set; } = working;
    public long PrivateBytes { get; set; } = privateBytes;
    public int Active { get; set; } = active;
    public long Reserved { get; set; } = reserved;
    public int Samples { get; set; }
}
internal sealed record HttpScenario(int Concurrency, int Total, Dictionary<int, int> StatusCounts, double ElapsedMs,
    double RequestsPerSecond, double P50Ms, double P95Ms, double MaxMs, long OutcomeCount, long DurationCount,
    Sample Before, Peak Peak, bool BudgetReleased);
internal sealed record HttpReport(string Schema, string Status, string Classification, string BaseRevision,
    string Profile, int BodyBytes, string Notes, List<HttpScenario> Scenarios);
