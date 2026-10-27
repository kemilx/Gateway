using System.Text;
using System.Text.Json.Nodes;
using Gateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Xunit;

namespace Gateway.Tests;

public sealed class LocalPolicySnapshotTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "policy-snapshot-unit-" + Guid.NewGuid().ToString("N"));
    private static readonly RouteConfig[] Routes = [new() { RouteId = "protected-payment" }, new() { RouteId = "passthrough" }];
    private string FilePath => Path.Combine(directory, "policy.json");

    public LocalPolicySnapshotTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);
    private static JsonObject Document()
    {
        var defaults = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pan-policy-defaults.json")))!;
        return new JsonObject { ["PanPolicies"] = defaults["PanPolicies"]!.DeepClone() };
    }
    private static JsonObject Policy(JsonObject document) => document["PanPolicies"]!["Policies"]![0]!.AsObject();
    private LocalPolicyFileReader Reader() => new(FilePath, Routes);
    private async Task<LocalPolicySnapshot> Load(JsonObject? document = null)
    {
        await File.WriteAllTextAsync(FilePath, (document ?? Document()).ToJsonString());
        return await Reader().ReadAsync(default);
    }

    [Fact]
    public async Task Disabled_option_registers_no_reader_or_reload_service_and_preserves_default_catalog()
    {
        await using var app = GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        Assert.Null(app.Services.GetService<LocalPolicyFileReader>());
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), service => service is LocalPolicyReloadService);
        var policy = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        Assert.Equal("Mask", policy.Mode);
        Assert.Equal("v1", policy.Version);
    }

    [Fact]
    public async Task Isolated_complete_file_compiles_normalized_defaults_and_copies_options()
    {
        var document = Document();
        var snapshot = await Load(document);
        Policy(document)["Mode"] = "Block";
        Assert.Equal("Mask", snapshot.Catalog.Find("protected-payment")!.Mode);
        Assert.Equal(new[] { "payment", "cardNumber" }, snapshot.Catalog.Find("protected-payment")!.Segments);
        Assert.Null(snapshot.Catalog.Find("passthrough"));
    }

    [Theory]
    [InlineData("root-extra")]
    [InlineData("unknown")]
    [InlineData("missing-budget")]
    [InlineData("missing-coverage")]
    [InlineData("missing-policy")]
    [InlineData("bad-rule-shape")]
    [InlineData("bad-bool")]
    [InlineData("invalid-mode")]
    public async Task Invalid_file_is_rejected_without_merging_defaults(string mutation)
    {
        var document = Document();
        switch (mutation)
        {
            case "root-extra": document["Upstream"] = "secret-invalid"; break;
            case "unknown": Policy(document)["Unknown"] = true; break;
            case "missing-budget": document["PanPolicies"]!.AsObject().Remove("InspectionBudget"); break;
            case "missing-coverage": document["PanPolicies"]!.AsObject().Remove("CoverageContract"); break;
            case "missing-policy": document["PanPolicies"]!["Policies"] = new JsonArray(); break;
            case "bad-rule-shape": Policy(document)["AdditionalRules"] = new JsonObject { ["Selector"] = "$.email" }; break;
            case "bad-bool": Policy(document)["SecondaryPanDetection"] = " true "; break;
            case "invalid-mode": Policy(document)["Mode"] = "mask"; break;
        }
        var failure = await Assert.ThrowsAsync<LocalPolicyReadException>(() => Load(document));
        Assert.Equal("invalid_configuration", failure.Reason);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"PanPolicies\":null}")]
    [InlineData("{\"PanPolicies\":{},\"PanPolicies\":{}}")]
    public async Task Invalid_json_or_root_has_bounded_reason(string text)
    {
        await File.WriteAllTextAsync(FilePath, text);
        Assert.Equal("invalid_configuration", (await Assert.ThrowsAsync<LocalPolicyReadException>(() => Reader().ReadAsync(default))).Reason);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Actual_utf8_bytes_enforce_one_mebibyte_boundary(int delta)
    {
        var json = Document().ToJsonString();
        var suffix = new string(' ', LocalPolicyFileReader.MaximumBytes + delta - Encoding.UTF8.GetByteCount(json));
        await File.WriteAllTextAsync(FilePath, json + suffix, new UTF8Encoding(false));
        if (delta <= 0) Assert.NotNull(await Reader().ReadAsync(default));
        else Assert.Equal("too_large", (await Assert.ThrowsAsync<LocalPolicyReadException>(() => Reader().ReadAsync(default))).Reason);
    }

    [Theory]
    [InlineData("relative.json")]
    [InlineData("https://example.invalid/policy.json")]
    [InlineData(@"\\server\share\policy.json")]
    [InlineData(@"\\.\pipe\policy")]
    [InlineData(@"\\?\C:\policy.json")]
    public async Task Unsafe_paths_are_rejected(string path)
    {
        Assert.Equal("read_failed", (await Assert.ThrowsAsync<LocalPolicyReadException>(() => new LocalPolicyFileReader(path, Routes).ReadAsync(default))).Reason);
    }

    [Fact]
    public async Task Directory_and_missing_file_are_rejected_and_stream_is_disposed()
    {
        Assert.Equal("read_failed", (await Assert.ThrowsAsync<LocalPolicyReadException>(() => new LocalPolicyFileReader(directory, Routes).ReadAsync(default))).Reason);
        Assert.Equal("read_failed", (await Assert.ThrowsAsync<LocalPolicyReadException>(() => Reader().ReadAsync(default))).Reason);
        await Load();
        using var exclusive = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task Publication_is_atomic_and_old_catalog_and_capture_remain_immutable()
    {
        var a = await Load();
        var store = new LocalPolicySnapshots(a);
        var capture = new LocalPolicyRequestCapture(store.Active, store.Active.Catalog.Find("protected-payment")!);
        var document = Document();
        Policy(document)["Mode"] = "Block";
        Policy(document)["Version"] = "v2";
        var b = await Load(document);
        Assert.True(store.Publish(b, default));
        Assert.Same(b, store.Active);
        Assert.Equal("Block", store.Active.Catalog.Find("protected-payment")!.Mode);
        Assert.Same(a, capture.Snapshot);
        Assert.Equal("Mask", capture.Policy.Mode);
        Assert.Equal("v1", a.Catalog.Find("protected-payment")!.Version);
    }

    [Fact]
    public async Task Normalized_identical_does_not_publish_version_only_does_and_no_history_is_kept()
    {
        var a = await Load();
        var store = new LocalPolicySnapshots(a);
        var document = Document();
        Policy(document)["Transformation"] = "PAN";
        Policy(document)["AdditionalRules"] = new JsonArray();
        Policy(document)["SecondaryPanDetection"] = false;
        Assert.False(store.Publish(await Load(document), default));
        Assert.Same(a, store.Active);
        Policy(document)["Version"] = "v2";
        Assert.True(store.Publish(await Load(document), default));
        Assert.True(store.Publish(a, default));
        Assert.Same(a, store.Active);
    }

    [Theory]
    [InlineData("Mode")]
    [InlineData("Transformation")]
    [InlineData("SecondaryPanDetection")]
    [InlineData("MaxBodyBytes")]
    [InlineData("AdditionalRules")]
    [InlineData("Selector")]
    public async Task Changed_behavior_requires_distinct_active_version(string field)
    {
        var a = await Load();
        var store = new LocalPolicySnapshots(a);
        var document = Document();
        Policy(document)[field] = field switch
        {
            "Mode" => JsonValue.Create("Block"),
            "Transformation" => JsonValue.Create("Email"),
            "SecondaryPanDetection" => JsonValue.Create(true),
            "MaxBodyBytes" => JsonValue.Create(512),
            "Selector" => JsonValue.Create("$.email"),
            _ => new JsonArray(new JsonObject { ["Selector"] = "$.email", ["Transformation"] = "Email" })
        };
        if (field == "Selector") Policy(document)["AdditionalRules"] = new JsonArray(new JsonObject { ["Selector"] = "$.payment.cardNumber" });
        var candidate = await Load(document);
        Assert.Equal("incompatible_snapshot", Assert.Throws<LocalPolicyReadException>(() => store.Publish(candidate, default)).Reason);
        Assert.Same(a, store.Active);
        Policy(document)["Version"] = "v2";
        Assert.True(store.Publish(await Load(document), default));
    }

    [Theory]
    [InlineData("policy-id")]
    [InlineData("route")]
    [InlineData("coverage-version")]
    [InlineData("coverage-required")]
    [InlineData("timeout")]
    [InlineData("concurrency")]
    [InlineData("bytes")]
    public async Task Fixed_restrictions_reject_even_with_new_version(string field)
    {
        var a = await Load();
        var store = new LocalPolicySnapshots(a);
        var document = Document();
        Policy(document)["Version"] = "v2";
        switch (field)
        {
            case "policy-id": Policy(document)["PolicyId"] = "other"; break;
            case "route":
                var extra = Policy(document).DeepClone(); extra["RouteId"] = "passthrough";
                document["PanPolicies"]!["Policies"]!.AsArray().Add(extra); break;
            case "coverage-version": document["PanPolicies"]!["CoverageContract"]!["Version"] = "v2"; break;
            case "coverage-required":
                Policy(document)["AdditionalRules"] = new JsonArray(new JsonObject { ["Selector"] = "$.email" });
                document["PanPolicies"]!["CoverageContract"]!["Required"]!.AsArray().Add(new JsonObject { ["RouteId"] = "protected-payment", ["Selector"] = "$.email" }); break;
            default: document["PanPolicies"]!["InspectionBudget"]![field == "timeout" ? "InspectionTimeoutSeconds" : field == "concurrency" ? "MaxConcurrentInspections" : "MaxReservedBodyBytes"] = 7; if (field == "bytes") document["PanPolicies"]!["InspectionBudget"]!["MaxReservedBodyBytes"] = 7000000; break;
        }
        var candidate = await Load(document);
        Assert.Equal("incompatible_snapshot", Assert.Throws<LocalPolicyReadException>(() => store.Publish(candidate, default)).Reason);
        Assert.Same(a, store.Active);
        Assert.False(store.IsProtected("passthrough"));
    }

    [Fact]
    public async Task Cancellation_before_read_or_swap_retains_active()
    {
        var a = await Load();
        var store = new LocalPolicySnapshots(a);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader().ReadAsync(cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => store.Publish(a, cancellation.Token, initial: true));
        Assert.Same(a, store.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notifications_are_bounded_suppressed_reset_and_cannot_rollback(bool throwLogger)
    {
        var a = await Load();
        var store = new LocalPolicySnapshots(a);
        var logger = new CaptureLogger { Throw = throwLogger };
        using var service = new LocalPolicyReloadService(Reader(), store, TimeProvider.System, logger);
        await File.WriteAllTextAsync(FilePath, "invalid");
        await service.ReloadAsync(default);
        await service.ReloadAsync(default);
        Assert.Equal(1, logger.Attempts);
        Assert.Same(a, store.Active);
        await Load();
        await service.ReloadAsync(default);
        Assert.Equal(1, logger.Attempts);
        await File.WriteAllTextAsync(FilePath, "invalid");
        await service.ReloadAsync(default);
        Assert.Equal(2, logger.Attempts);
        File.Delete(FilePath);
        await service.ReloadAsync(default);
        Assert.Equal(3, logger.Attempts);
        var document = Document(); Policy(document)["Version"] = "v2";
        await Load(document);
        await service.ReloadAsync(default);
        Assert.Equal(4, logger.Attempts);
        Assert.Equal("v2", store.Active.Catalog.Find("protected-payment")!.Version);
        Assert.Equal(new[] { "rejected/invalid_configuration", "rejected/invalid_configuration", "rejected/read_failed", "applied/none" }, logger.States);
    }

    [Fact]
    public async Task Invalid_initial_file_fails_generically_without_fallback()
    {
        var store = new LocalPolicySnapshots(await Load());
        await File.WriteAllTextAsync(FilePath, "not-json-secret");
        using var service = new LocalPolicyReloadService(Reader(), store, TimeProvider.System, new CaptureLogger());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));
        Assert.Equal("Local policy snapshot initialization failed.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Initial_file_must_match_fixed_baseline_before_polling_starts()
    {
        var store = new LocalPolicySnapshots(await Load());
        var document = Document();
        Policy(document)["PolicyId"] = "other";
        await Load(document);
        var clock = new ManualTimeProvider();
        using var service = new LocalPolicyReloadService(Reader(), store, clock, new CaptureLogger());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));
        Assert.Equal("Local policy snapshot initialization failed.", exception.Message);
        Assert.Equal(0, clock.TimerCount);
        Assert.Equal("PAN", store.Active.Catalog.Find("protected-payment")!.PolicyId);
    }

    [Fact]
    public async Task Polling_waits_one_second_after_each_attempt_and_stop_cancels_pending_timer()
    {
        var store = new LocalPolicySnapshots(await Load());
        var document = Document();
        Policy(document)["Mode"] = "Block";
        Policy(document)["Version"] = "v2";
        await Load(document);
        var logger = new CaptureLogger();
        var clock = new ManualTimeProvider();
        using var service = new LocalPolicyReloadService(Reader(), store, clock, logger);
        await service.StartAsync(default);
        var first = await clock.NextTimer();
        Assert.Equal("Block", store.Active.Catalog.Find("protected-payment")!.Mode);
        Assert.Equal(new[] { "applied/none" }, logger.States);
        var initial = store.Active;
        Policy(document)["Mode"] = "Observe";
        Policy(document)["Version"] = "v3";
        await Load(document);
        Assert.Same(initial, store.Active);
        first.Fire();
        var second = await clock.NextTimer();
        Assert.Equal("Observe", store.Active.Catalog.Find("protected-payment")!.Mode);
        Assert.Equal(2, logger.Attempts);
        await File.WriteAllTextAsync(FilePath, "invalid");
        second.Fire();
        var third = await clock.NextTimer();
        Assert.Equal("Observe", store.Active.Catalog.Find("protected-payment")!.Mode);
        Assert.Equal("rejected/invalid_configuration", logger.States.Last());
        await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(third.Disposed);
        var stopped = store.Active;
        await Load();
        third.Fire();
        Assert.Same(stopped, store.Active);
        Assert.Equal(3, clock.TimerCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_or_throwing_IsEnabled_does_not_retry_rejected_state(bool throwEnabled)
    {
        var store = new LocalPolicySnapshots(await Load());
        var logger = new CaptureLogger { Enabled = false, ThrowEnabled = throwEnabled };
        using var service = new LocalPolicyReloadService(Reader(), store, TimeProvider.System, logger);
        await File.WriteAllTextAsync(FilePath, "invalid");
        await service.ReloadAsync(default);
        await service.ReloadAsync(default);
        Assert.Equal(1, logger.EnabledChecks);
        Assert.Equal(0, logger.Attempts);
        await Load();
        await service.ReloadAsync(default);
        await File.WriteAllTextAsync(FilePath, "invalid");
        await service.ReloadAsync(default);
        Assert.Equal(2, logger.EnabledChecks);
        Assert.Equal(0, logger.Attempts);
        var document = Document();
        Policy(document)["Mode"] = "Block";
        Policy(document)["Version"] = "v2";
        await Load(document);
        await service.ReloadAsync(default);
        Assert.Equal("Block", store.Active.Catalog.Find("protected-payment")!.Mode);
        Assert.Equal(3, logger.EnabledChecks);
        Assert.Equal(0, logger.Attempts);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly global::System.Threading.Channels.Channel<ManualTimer> timers = global::System.Threading.Channels.Channel.CreateUnbounded<ManualTimer>();
        internal int TimerCount;
        internal async Task<ManualTimer> NextTimer() => await timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ManualTimer(callback, state);
            Interlocked.Increment(ref TimerCount);
            timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        internal bool Disposed;
        internal void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class CaptureLogger : ILogger<LocalPolicyReloadService>
    {
        internal int Attempts;
        internal bool Throw;
        internal bool Enabled = true;
        internal bool ThrowEnabled;
        internal int EnabledChecks;
        internal List<string> States { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level)
        {
            EnabledChecks++;
            if (ThrowEnabled) throw new InvalidOperationException("synthetic enabled");
            return Enabled;
        }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Attempts++;
            Assert.Null(exception);
            Assert.Equal(7, id.Id);
            var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(new[] { "Status", "Reason", "{OriginalFormat}" }, fields.Keys);
            States.Add(fields["Status"] + "/" + fields["Reason"]);
            if (Throw) throw new InvalidOperationException("synthetic logger");
        }
    }
}
