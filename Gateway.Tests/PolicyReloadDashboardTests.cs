using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Tests;

public sealed class PolicyReloadDashboardTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Summary_captures_catalog_and_reload_state_together()
    {
        await using var h = await Harness.Create(barriers: true);
        var expected = new Dictionary<string, (string Mode, int Bytes, DateTimeOffset Applied)>
        { ["v1"] = ("Mask", 1048576, Epoch) };
        var previousVersion = "v1";
        for (var i = 1; i <= 24; i++)
        {
            // Rejection and unchanged checks decouple lastChecked from lastApplied.
            h.Clock.Now = Epoch.AddMinutes(-i);
            await h.Replace("{synthetic-invalid-body");
            await h.Service.ReloadAsync(default);
            Check(await h.Summary(), previousVersion, "rejected", "invalid_configuration",
                h.Clock.Now, expected[previousVersion].Applied);
            await h.Replace(Harness.Document(expected[previousVersion].Mode, previousVersion, expected[previousVersion].Bytes));
            h.Clock.Now = Epoch.AddSeconds(-i);
            await h.Service.ReloadAsync(default);
            var oldCheck = h.Clock.Now;
            Check(await h.Summary(), previousVersion, "valid", null, oldCheck, expected[previousVersion].Applied);

            var version = "qa-" + i;
            var mode = i % 2 == 0 ? "Block" : "Mask";
            var bytes = 4096 + i;
            var applied = Epoch.AddHours(i);
            expected.Add(version, (mode, bytes, applied));
            await h.Replace(Harness.Document(mode, version, bytes));
            h.Clock.Now = applied;
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.OpenBarrier = () => { entered.TrySetResult(); Assert.True(release.Wait(Limit)); };
            var reload = Task.Run(() => h.Service.ReloadAsync(default));
            try
            {
                await entered.Task.WaitAsync(Limit);
                // Real HTTP while the actual file open is held, not a delay-based race.
                Check(await h.Summary(), previousVersion, "valid", null, oldCheck, expected[previousVersion].Applied);
                var reads = Enumerable.Range(0, 3).Select(async _ =>
                {
                    for (var n = 0; n < 8; n++)
                    {
                        var summary = await h.Summary();
                        var p = summary["policies"]![0]!;
                        var observed = p["version"]!.GetValue<string>();
                        Assert.Contains(observed, new[] { previousVersion, version });
                        var row = expected[observed];
                        Assert.Equal(row.Mode, p["mode"]!.GetValue<string>());
                        Assert.Equal(row.Bytes, p["maxBodyBytes"]!.GetValue<int>());
                        Check(summary, observed, "valid", null, observed == version ? applied : oldCheck, row.Applied);
                    }
                }).ToArray();
                release.Set();
                await Task.WhenAll(reads.Append(reload)).WaitAsync(Limit);
                Check(await h.Summary(), version, "valid", null, applied, applied);
            }
            finally { release.Set(); await reload.WaitAsync(Limit); h.OpenBarrier = null; }
            previousVersion = version;
        }
    }

    [Fact]
    public async Task Real_reload_rejection_and_recovery_change_status_not_readiness()
    {
        await using var h = await Harness.Create();
        Check(await h.Summary(), "v1", "valid", null, Epoch, Epoch);
        await h.AssertProxy(masked: true);
        h.Clock.Now = Epoch.AddSeconds(1);
        await h.Replace("{private-body-marker 4111111111111111");
        await h.Service.ReloadAsync(default);
        Check(await h.Summary(), "v1", "rejected", "invalid_configuration", Epoch.AddSeconds(1), Epoch);
        await h.AssertProxy(masked: true);
        await h.Replace(Harness.Document());
        h.Clock.Now = Epoch.AddSeconds(2);
        await h.Service.ReloadAsync(default);
        Check(await h.Summary(), "v1", "valid", null, Epoch.AddSeconds(2), Epoch);
        await h.Replace(Harness.Document("Block", "v2"));
        h.Clock.Now = Epoch.AddSeconds(3);
        await h.Service.ReloadAsync(default);
        Check(await h.Summary(), "v2", "valid", null, Epoch.AddSeconds(3), Epoch.AddSeconds(3));
        await h.AssertProxy(masked: false);
        using var ready = await h.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Reload_status_never_contains_sensitive_markers()
    {
        await using var h = await Harness.Create();
        var cases = new[] { "invalid_configuration", "read_failed", "too_large", "incompatible_snapshot" };
        foreach (var reason in cases)
        {
            switch (reason)
            {
                case "read_failed": File.Delete(h.Path); break;
                case "too_large": await h.Replace("private-body-marker" + new string(' ', LocalPolicyFileReader.MaximumBytes)); break;
                case "incompatible_snapshot": await h.Replace(Harness.Document("Block", "v1")); break;
                default: await h.Replace("{private-body-marker 4111111111111111 $.payment.cardNumber exception-marker"); break;
            }
            h.Clock.Now = h.Clock.Now.AddSeconds(1);
            await h.Service.ReloadAsync(default);
            var summary = await h.Summary();
            Check(summary, "v1", "rejected", reason, h.Clock.Now, Epoch);
            var status = summary["policyReload"]!.AsObject();
            Assert.Equal(new[] { "enabled", "lastAppliedAtUtc", "lastCheckedAtUtc", "reason", "status" }, status.Select(p => p.Key).Order());
            Assert.Contains(status["reason"]!.GetValue<string>(), cases);
            var text = status.ToJsonString();
            foreach (var marker in new[] { h.Path, "private-file-marker", "private-body-marker", "4111111111111111", "$.payment.cardNumber", "exception-marker", "private-query-marker" })
                Assert.DoesNotContain(marker, text);
        }
    }

    private static void Check(JsonObject root, string version, string status, string? reason, DateTimeOffset check, DateTimeOffset applied)
    {
        Assert.Equal(1, root["schemaVersion"]!.GetValue<int>());
        Assert.True(root["health"]!["ready"]!.GetValue<bool>());
        Assert.Equal(version, Assert.Single(root["policies"]!.AsArray())!["version"]!.GetValue<string>());
        var r = root["policyReload"]!;
        Assert.True(r["enabled"]!.GetValue<bool>());
        Assert.Equal(status, r["status"]!.GetValue<string>());
        Assert.Equal(reason, r["reason"]?.GetValue<string>());
        Assert.Equal(check, r["lastCheckedAtUtc"]!.GetValue<DateTimeOffset>());
        Assert.Equal(applied, r["lastAppliedAtUtc"]!.GetValue<DateTimeOffset>());
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MVP36-dashboard-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(directory, "private-file-marker.json");
        internal readonly Clock Clock = new();
        internal Action? OpenBarrier;
        private WebApplication upstream = null!, gateway = null!;
        private LocalPolicyReloadService? barrierEvaluator;
        internal HttpClient Client = null!;
        internal LocalPolicyReloadService Service => barrierEvaluator ?? gateway.Services.GetServices<IHostedService>().OfType<LocalPolicyReloadService>().Single();
        internal static JsonObject Document(string mode = "Mask", string version = "v1", int bytes = 1048576)
        {
            var defaults = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "pan-policy-defaults.json")))!;
            var d = new JsonObject { ["PanPolicies"] = defaults["PanPolicies"]!.DeepClone() };
            var p = d["PanPolicies"]!["Policies"]![0]!;
            p["Mode"] = mode; p["Version"] = version; p["MaxBodyBytes"] = bytes;
            return d;
        }
        internal Task Replace(JsonObject d) => Replace(d.ToJsonString());
        internal async Task Replace(string text)
        {
            var next = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(next, text, new UTF8Encoding(false));
            if (File.Exists(Path)) File.Replace(next, Path, null); else File.Move(next, Path);
        }
        internal static async Task<Harness> Create(bool barriers = false)
        {
            var h = new Harness();
            Directory.CreateDirectory(h.directory);
            try
            {
                await h.Replace(Document());
                var b = WebApplication.CreateBuilder(); b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0");
                h.upstream = b.Build();
                h.upstream.Run(async c => { c.Response.ContentType = "application/json"; await c.Response.WriteAsync("{\"payment\":{\"cardNumber\":\"4111111111111111\"}}"); });
                await h.upstream.StartAsync();
                h.gateway = GatewayHost.Build([], Address(h.upstream) + "/", b =>
                {
                    b.Configuration.Sources.Clear();
                    var values = PolicyConfiguration.Defaults(); values["LocalPolicySnapshotFile"] = h.Path;
                    b.Configuration.AddInMemoryCollection(values); b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0");
                    b.Services.AddSingleton<TimeProvider>(h.Clock);
                });
                await h.gateway.StartAsync();
                // Host registration intentionally owns its production reader. The separate evaluator
                // uses that host's store while its polling timer is idle: still exactly one writer.
                if (barriers) h.barrierEvaluator = new LocalPolicyReloadService(new LocalPolicyFileReader(h.Path,
                        [new RouteConfig { RouteId = "protected-payment" }, new RouteConfig { RouteId = "passthrough" }], path =>
                        {
                            h.OpenBarrier?.Invoke();
                            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.Asynchronous);
                        }), h.gateway.Services.GetRequiredService<LocalPolicySnapshots>(), h.Clock,
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalPolicyReloadService>.Instance);
                h.Client = new HttpClient { BaseAddress = new Uri(Address(h.gateway)), Timeout = Limit };
                return h;
            }
            catch { await h.DisposeAsync(); throw; }
        }
        private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        internal async Task<JsonObject> Summary()
        {
            using var response = await Client.GetAsync("/dashboard/api/summary?private-query-marker=synthetic");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }
        internal async Task AssertProxy(bool masked)
        {
            using var response = await Client.GetAsync("/protected/payment?private-query-marker=synthetic");
            Assert.Equal(masked ? HttpStatusCode.OK : HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("4111111111111111", body);
            if (masked) Assert.Equal("************1111", JsonNode.Parse(body)!["payment"]!["cardNumber"]!.GetValue<string>());
        }
        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            barrierEvaluator?.Dispose();
            if (gateway is not null) { await gateway.StopAsync().WaitAsync(Limit); await gateway.DisposeAsync(); }
            if (upstream is not null) { await upstream.StopAsync().WaitAsync(Limit); await upstream.DisposeAsync(); }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = Epoch;
        public override DateTimeOffset GetUtcNow() => Now;
        // Explicit sequential evaluations, not a claim of wall-clock polling coverage.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => dueTime == TimeSpan.FromSeconds(1) ? new IdleTimer() : TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        private sealed class IdleTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
