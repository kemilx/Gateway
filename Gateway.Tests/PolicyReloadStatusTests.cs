using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway;
using Microsoft.Extensions.Logging;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Tests;

public sealed class PolicyReloadStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Disabled_reload_status_has_no_operational_details()
    {
        await using var h = await Harness.Create(start: false);
        var view = h.Store.Capture();
        Assert.Same(h.Store.Active, view.Snapshot);
        Assert.Equal(new DashboardPolicyReload(false, "disabled", null, null, null), view.Reload);
        var dto = new DashboardSnapshot(1, T0, new(true, true), [], null!);
        Assert.Equal(view.Reload, dto.PolicyReload);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var status = json.RootElement.GetProperty("policyReload");
        Assert.Equal(new[] { "enabled", "lastAppliedAtUtc", "lastCheckedAtUtc", "reason", "status" },
            status.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("disabled", status.GetProperty("status").GetString());
        foreach (var name in new[] { "reason", "lastCheckedAtUtc", "lastAppliedAtUtc" })
            Assert.Equal(JsonValueKind.Null, status.GetProperty(name).ValueKind);
    }

    [Fact]
    public async Task Applied_unchanged_rejected_and_recovered_states_have_expected_timestamps()
    {
        await using var h = await Harness.Create();
        var initial = h.Store.Capture();
        Check(initial, "valid", null, T0, T0, "v1");
        h.Clock.Now = T0.AddSeconds(1);
        await h.Service.ReloadAsync(default);
        var unchanged = h.Store.Capture();
        Assert.Same(initial.Snapshot, unchanged.Snapshot);
        Check(unchanged, "valid", null, T0.AddSeconds(1), T0, "v1");

        await h.Write("Block", "v2");
        h.Clock.Now = T0.AddSeconds(2);
        await h.Service.ReloadAsync(default);
        var applied = h.Store.Capture();
        Assert.NotSame(initial.Snapshot, applied.Snapshot);
        Check(applied, "valid", null, T0.AddSeconds(2), T0.AddSeconds(2), "v2");

        await File.WriteAllTextAsync(h.Path, "{private-rejected-payload");
        h.Clock.Now = T0.AddSeconds(3);
        await h.Service.ReloadAsync(default);
        var rejected = h.Store.Capture();
        Assert.Same(applied.Snapshot, rejected.Snapshot);
        Check(rejected, "rejected", "invalid_configuration", T0.AddSeconds(3), T0.AddSeconds(2), "v2");

        await h.Write("Block", "v2");
        h.Clock.Now = T0.AddSeconds(4);
        await h.Service.ReloadAsync(default);
        var recoveredUnchanged = h.Store.Capture();
        Assert.Same(applied.Snapshot, recoveredUnchanged.Snapshot);
        Check(recoveredUnchanged, "valid", null, T0.AddSeconds(4), T0.AddSeconds(2), "v2");
        await h.Write("Mask", "v3");
        h.Clock.Now = T0.AddSeconds(5);
        await h.Service.ReloadAsync(default);
        Check(h.Store.Capture(), "valid", null, T0.AddSeconds(5), T0.AddSeconds(5), "v3");
        Check(initial, "valid", null, T0, T0, "v1"); // Captured views remain immutable.
        Check(rejected, "rejected", "invalid_configuration", T0.AddSeconds(3), T0.AddSeconds(2), "v2");
    }

    [Fact]
    public async Task Repeated_rejections_update_check_time_even_when_logs_are_suppressed()
    {
        await using var h = await Harness.Create();
        var initial = h.Store.Active;
        await File.WriteAllTextAsync(h.Path, "{");
        h.Clock.Now = T0.AddSeconds(1);
        await h.Service.ReloadAsync(default);
        Check(h.Store.Capture(), "rejected", "invalid_configuration", T0.AddSeconds(1), T0, "v1");
        Assert.Equal(2, h.Logger.Count);
        h.Clock.Now = T0.AddSeconds(2);
        await h.Service.ReloadAsync(default);
        Check(h.Store.Capture(), "rejected", "invalid_configuration", T0.AddSeconds(2), T0, "v1");
        Assert.Same(initial, h.Store.Active);
        Assert.Equal(2, h.Logger.Count);
        await h.Write();
        h.Clock.Now = T0.AddSeconds(3);
        await h.Service.ReloadAsync(default);
        Check(h.Store.Capture(), "valid", null, T0.AddSeconds(3), T0, "v1");
        await File.WriteAllTextAsync(h.Path, "{");
        h.Clock.Now = T0.AddSeconds(4);
        await h.Service.ReloadAsync(default);
        Check(h.Store.Capture(), "rejected", "invalid_configuration", T0.AddSeconds(4), T0, "v1");
        Assert.Equal(3, h.Logger.Count);
    }

    [Fact]
    public async Task Cancellation_and_logger_failure_preserve_reload_status_contract()
    {
        await using var h = await Harness.Create(start: false);
        LocalPolicyView? notified = null;
        h.Logger.OnLog = () =>
        {
            notified = h.Store.Capture();
            throw new InvalidOperationException("synthetic logger failure");
        };
        await h.Service.StartAsync(default);
        Assert.NotNull(notified);
        Check(notified, "valid", null, T0, T0, "v1");
        var before = h.Store.Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        h.Clock.Now = T0.AddSeconds(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.ReloadAsync(cancellation.Token));
        Assert.Same(before, h.Store.Capture());
        await h.Write("Block", "v2");
        h.Logger.OnLog = () =>
        {
            notified = h.Store.Capture();
            throw new InvalidOperationException("synthetic logger failure");
        };
        await h.Service.ReloadAsync(default);
        Check(notified, "valid", null, T0.AddSeconds(1), T0.AddSeconds(1), "v2");
        Check(h.Store.Capture(), "valid", null, T0.AddSeconds(1), T0.AddSeconds(1), "v2");
        await File.WriteAllTextAsync(h.Path, "{");
        h.Clock.Now = T0.AddSeconds(2);
        h.Logger.OnLog = () =>
        {
            notified = h.Store.Capture();
            throw new InvalidOperationException("synthetic logger failure");
        };
        await h.Service.ReloadAsync(default);
        Check(notified, "rejected", "invalid_configuration", T0.AddSeconds(2), T0.AddSeconds(1), "v2");
        Check(h.Store.Capture(), "rejected", "invalid_configuration", T0.AddSeconds(2), T0.AddSeconds(1), "v2");
    }

    [Fact]
    public async Task Clock_rollback_is_observational_not_a_reload_failure()
    {
        await using var h = await Harness.Create();
        h.Clock.Now = T0.AddHours(-1);
        await h.Service.ReloadAsync(default);
        Check(h.Store.Capture(), "valid", null, T0.AddHours(-1), T0, "v1");
        await File.WriteAllTextAsync(h.Path, "{");
        h.Clock.Now = T0.AddHours(-2);
        await h.Service.ReloadAsync(default);
        Check(h.Store.Capture(), "rejected", "invalid_configuration", T0.AddHours(-2), T0, "v1");
        await h.Write("Block", "v2");
        h.Clock.Now = T0.AddHours(-3);
        await h.Service.ReloadAsync(default);
        Check(h.Store.Capture(), "valid", null, T0.AddHours(-3), T0.AddHours(-3), "v2");
    }

    [Theory]
    [InlineData("read_failed")]
    [InlineData("too_large")]
    [InlineData("invalid_configuration")]
    [InlineData("incompatible_snapshot")]
    public async Task Rejections_have_closed_reasons_and_retain_last_activation(string reason)
    {
        await using var h = await Harness.Create();
        var before = h.Store.Active;
        switch (reason)
        {
            case "read_failed": File.Delete(h.Path); break;
            case "too_large": await File.WriteAllTextAsync(h.Path, new string(' ', LocalPolicyFileReader.MaximumBytes + 1)); break;
            case "invalid_configuration": await File.WriteAllTextAsync(h.Path, "{"); break;
            case "incompatible_snapshot": await h.Write("Block", "v1"); break;
        }
        h.Clock.Now = T0.AddSeconds(1);
        await h.Service.ReloadAsync(default);
        Assert.Same(before, h.Store.Active);
        Check(h.Store.Capture(), "rejected", reason, T0.AddSeconds(1), T0, "v1");
    }

    [Theory]
    [InlineData("private-untrusted-reason")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Unknown_rejection_reason_cannot_enter_the_view(string? reason)
    {
        await using var h = await Harness.Create();
        var before = h.Store.Capture();
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Store.RecordRejection(reason!, T0.AddSeconds(1)));
        Assert.Same(before, h.Store.Capture());
    }

    private static void Check(LocalPolicyView view, string status, string? reason,
        DateTimeOffset check, DateTimeOffset applied, string version)
    {
        Assert.Equal(new DashboardPolicyReload(true, status, reason, check, applied), view.Reload);
        Assert.Equal(TimeSpan.Zero, view.Reload.LastCheckedAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, view.Reload.LastAppliedAtUtc!.Value.Offset);
        Assert.Equal(version, view.Snapshot.Catalog.Find("protected-payment")!.Version);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MVP36-status-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(directory, "private-policy-marker.json");
        internal Clock Clock { get; } = new();
        internal Logger Logger { get; } = new();
        internal LocalPolicySnapshots Store { get; private set; } = null!;
        internal LocalPolicyReloadService Service { get; private set; } = null!;
        internal static async Task<Harness> Create(bool start = true)
        {
            var h = new Harness();
            Directory.CreateDirectory(h.directory);
            try
            {
                await h.Write();
                var reader = new LocalPolicyFileReader(h.Path, [new RouteConfig { RouteId = "protected-payment" }]);
                h.Store = new LocalPolicySnapshots(await reader.ReadAsync(default));
                h.Service = new(reader, h.Store, h.Clock, h.Logger);
                if (start) await h.Service.StartAsync(default);
                return h;
            }
            catch { await h.DisposeAsync(); throw; }
        }
        internal Task Write(string mode = "Mask", string version = "v1")
        {
            var defaults = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "pan-policy-defaults.json")))!;
            var document = new JsonObject { ["PanPolicies"] = defaults["PanPolicies"]!.DeepClone() };
            var policy = document["PanPolicies"]!["Policies"]![0]!;
            policy["Mode"] = mode;
            policy["Version"] = version;
            return File.WriteAllTextAsync(Path, document.ToJsonString());
        }
        public async ValueTask DisposeAsync()
        {
            if (Service is not null) { await Service.StopAsync(default); Service.Dispose(); }
            Directory.Delete(directory, true);
        }
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = T0;
        public override DateTimeOffset GetUtcNow() => Now;
        // Only explicit ReloadAsync calls evaluate; no real-time polling in these tests.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new IdleTimer();
        private sealed class IdleTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Logger : ILogger<LocalPolicyReloadService>
    {
        internal int Count;
        internal Action? OnLog;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Count++; OnLog?.Invoke(); }
    }
}
