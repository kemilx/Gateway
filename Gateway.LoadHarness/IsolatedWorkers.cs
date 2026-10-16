using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
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

internal static class IsolatedWorkers
{
    internal static readonly JsonSerializerOptions Wire = new(Common.Json) { WriteIndented = false };
    internal static Uri Origin(string value)
    {
        Common.Require(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "http"
            && IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip)
            && uri.Port > 0 && uri.UserInfo == "" && uri.AbsolutePath == "/" && uri.Query == "" && uri.Fragment == "");
        return uri!;
    }
    private static void Configure(WebApplicationBuilder b, int maxConcurrentInspections)
    {
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        b.Configuration.Sources.Clear();
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PanPolicies:InspectionBudget:MaxConcurrentInspections"] = maxConcurrentInspections.ToString(CultureInfo.InvariantCulture),
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
    }
    private static Task Send(object value) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, Wire));
    internal static async Task Run(string? upstream, int maxConcurrentInspections, CancellationToken ct)
    {
        if (upstream is not null)
        {
            Common.Require(maxConcurrentInspections is >= 4 and <= 8);
            Origin(upstream);
        }
        var payload = upstream is null ? Common.Payload() : null;
        await using var app = upstream is null
            ? SyntheticApi.Build([], b =>
            {
                b.Logging.ClearProviders();
                b.Configuration.Sources.Clear();
                b.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            }, a => a.MapGet("/load", async context =>
            {
                context.Response.ContentType = "application/json"; context.Response.ContentLength = payload!.Length;
                await context.Response.Body.WriteAsync(payload, context.RequestAborted);
            }))
            : GatewayHost.Build([], upstream, b => Configure(b, maxConcurrentInspections));
        long outcomes = 0, durations = 0, baselineOutcomes = 0, baselineDurations = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == PrivacyTelemetry.MeterName && i.Name is "gateway.privacy.inspection.outcomes" or "gateway.privacy.inspection.duration") l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((_, v, _, _) => Interlocked.Add(ref outcomes, v));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref durations));
        listener.Start();
        var budget = upstream is null ? null : app.Services.GetRequiredService<InspectionBudget>();
        if (budget is not null)
        {
            var leases = new List<IDisposable>(maxConcurrentInspections);
            IDisposable? overflow = null;
            try
            {
                for (var i = 0; i < maxConcurrentInspections; i++)
                    leases.Add(budget.TryAcquire(1) ?? throw new InvalidOperationException("Harness invariant failed."));
                overflow = budget.TryAcquire(1);
                Common.Require(overflow is null);
            }
            finally
            {
                overflow?.Dispose();
                foreach (var lease in leases) lease.Dispose();
            }
            Common.Require(budget.Snapshot == (0, 0L));
        }
        var gate = new object(); var peak = new Peak(0, 0, 0, 0, 0);
        void Sample()
        {
            if (budget is null) return;
            lock (gate)
            {
                var s = Common.Measure("isolated", false, budget);
                peak.ManagedBytes = Math.Max(peak.ManagedBytes, s.ManagedBytes);
                peak.WorkingSetBytes = Math.Max(peak.WorkingSetBytes, s.WorkingSetBytes);
                peak.PrivateBytes = Math.Max(peak.PrivateBytes, s.PrivateBytes);
                peak.Active = Math.Max(peak.Active, s.Active); peak.Reserved = Math.Max(peak.Reserved, s.Reserved); peak.Samples++;
            }
        }
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task SampleLoop()
        {
            try { while (true) { Sample(); await Task.Delay(10, sampling.Token); } }
            catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        }
        var sampler = SampleLoop();
        try
        {
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Origin(address);
            await Send(new { kind = "ready", address });
            var reset = false;
            while (true)
            {
                var line = await Console.In.ReadLineAsync(ct);
                if (line == "stop") { await Send(new { kind = "stopped" }); break; }
                Common.Require(budget is not null && line is not null);
                if (line == "reset")
                {
                    Common.Require(!reset);
                    await Settle(2, 2);
                    Common.Require(Interlocked.Read(ref outcomes) == 2 && Interlocked.Read(ref durations) == 2);
                    baselineOutcomes = Interlocked.Read(ref outcomes); baselineDurations = Interlocked.Read(ref durations);
                    lock (gate) { peak = new Peak(0, 0, 0, 0, 0); Sample(); }
                    reset = true; await Send(new { kind = "reset" });
                }
                else
                {
                    Common.Require(reset && line!.StartsWith("report ", StringComparison.Ordinal));
                    var expected = int.Parse(line![7..], NumberStyles.None, CultureInfo.InvariantCulture);
                    Common.Require(expected is >= 8 and <= 100000);
                    await Settle(baselineOutcomes + expected, baselineDurations + expected);
                    Sample();
                    var o = Interlocked.Read(ref outcomes) - baselineOutcomes; var d = Interlocked.Read(ref durations) - baselineDurations;
                    Common.Require(o == expected && d == expected);
                    sampling.Cancel(); await sampler;
                    await Send(new WorkerMetrics("report", peak, o, d, budget!.Snapshot == (0, 0L)));
                    Common.Require(await Console.In.ReadLineAsync(ct) == "stop");
                    await Send(new { kind = "stopped" }); break;
                }
            }
        }
        finally
        {
            sampling.Cancel(); await sampler;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await app.StopAsync(stop.Token);
        }
        async Task Settle(long o, long d)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (budget!.Snapshot != (0, 0L) || Interlocked.Read(ref outcomes) < o || Interlocked.Read(ref durations) < d)
                await Task.Delay(1, deadline.Token);
        }
    }
}
internal sealed record WorkerMetrics(string Kind, Peak Peak, long Outcomes, long Durations, bool BudgetReleased);
