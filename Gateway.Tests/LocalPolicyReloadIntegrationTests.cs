using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Tests;

public sealed class LocalPolicyReloadIntegrationTests : IDisposable
{
    private const string Payload = "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}";
    private const string SummaryPath = "/dashboard/api/summary";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MVP35-integration-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "private-policy-marker.json");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static readonly RouteConfig[] Routes = [new() { RouteId = "protected-payment" }, new() { RouteId = "passthrough" }];
    public LocalPolicyReloadIntegrationTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private static JsonObject Document(string mode = "Mask", string version = "v1")
    {
        var source = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pan-policy-defaults.json")))!;
        var document = new JsonObject { ["PanPolicies"] = source["PanPolicies"]!.DeepClone() };
        Policy(document)["Mode"] = mode;
        Policy(document)["Version"] = version;
        return document;
    }
    private static JsonObject Policy(JsonObject d) => d["PanPolicies"]!["Policies"]![0]!.AsObject();
    private async Task Replace(string contents)
    {
        var next = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(next, contents, new UTF8Encoding(false));
        if (File.Exists(FilePath)) File.Replace(next, FilePath, null);
        else File.Move(next, FilePath);
    }
    private Task Replace(JsonObject document) => Replace(document.ToJsonString());
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(Address(app)), Timeout = Limit };
    private WebApplication GatewayApp(string upstream, Logs logs, TimeProvider? clock = null, bool enabled = true, bool removeCapture = false, string? listen = null)
        => GatewayHost.Build([], upstream, b =>
        {
            b.Configuration.Sources.Clear();
            var values = PolicyConfiguration.Defaults();
            if (enabled) values["LocalPolicySnapshotFile"] = FilePath;
            b.Configuration.AddInMemoryCollection(values);
            b.WebHost.UseUrls(listen ?? "http://127.0.0.1:0");
            b.Logging.ClearProviders();
            b.Logging.AddProvider(logs); // Keep the production category filter intact.
            if (clock is not null) b.Services.AddSingleton(clock);
            if (removeCapture) b.Services.AddSingleton<IStartupFilter, RemoveCaptureFilter>();
        });
    private static WebApplication Upstream(Func<HttpContext, Task> handle)
    {
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();
        app.Run(context => handle(context));
        return app;
    }
    private static async Task<JsonObject> Summary(HttpClient client, string mode, string version)
    {
        using var response = await client.GetAsync(SummaryPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("4111111111111111", text);
        Assert.DoesNotContain("private-policy-marker", text);
        var root = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(new[] { "health", "metrics", "policies", "policyReload", "schemaVersion", "startedAtUtc" }, root.Select(p => p.Key).Order());
        Assert.True(root["health"]!["ready"]!.GetValue<bool>());
        var p = Assert.Single(root["policies"]!.AsArray())!;
        Assert.Equal(mode, p["mode"]!.GetValue<string>());
        Assert.Equal(version, p["version"]!.GetValue<string>());
        Assert.Equal("PAN", p["transformation"]!.GetValue<string>());
        return root;
    }
    private static async Task Masked(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("4111111111111111", text);
        Assert.Equal("************1111", JsonNode.Parse(text)!["payment"]!["cardNumber"]!.GetValue<string>());
    }

    [Fact]
    public async Task Polling_atomic_replace_preserves_inflight_capture_and_recovers_with_consistent_summary_events()
    {
        await Replace(Document());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = Upstream(async context =>
        {
            if (context.Request.Path == "/held")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
            }
            if (context.Request.Path == "/text")
            { context.Response.ContentType = "text/plain"; await context.Response.WriteAsync("synthetic"); return; }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(Payload);
        });
        await upstream.StartAsync();
        var logs = new Logs();
        await using var gateway = GatewayApp(Address(upstream) + "/", logs);
        await gateway.StartAsync();
        using var client = Client(gateway);
        var store = gateway.Services.GetRequiredService<LocalPolicySnapshots>();
        var a = store.Active;
        var initial = await Summary(client, "Mask", "v1");
        var held = client.GetAsync("/protected/held?request-private-marker=secret");
        await entered.Task.WaitAsync(Limit); // YARP has sent the request, but no upstream headers exist.
        try
        {
            await Replace(Document("Block", "v2"));
            await logs.Wait(l => l.Count(e => e.Id == 7 && e.Field("Status") == "applied") == 2);
            Assert.NotSame(a, store.Active);
            using (var blocked = await client.GetAsync("/protected/now"))
            {
                Assert.Equal(HttpStatusCode.BadGateway, blocked.StatusCode);
                Assert.DoesNotContain("4111111111111111", await blocked.Content.ReadAsStringAsync());
            }
            release.TrySetResult();
            using (var old = await held.WaitAsync(Limit)) await Masked(old);
            await logs.Wait(l => l.Count(e => e.Id == 6) == 2);
            foreach (var (version, outcome) in new[] { ("v1", "inspected"), ("v2", "policy_blocked") })
            {
                var terminal = Assert.Single(logs.All, e => e.Id == 5 && e.Field("PolicyVersion") == version);
                var rule = Assert.Single(logs.All, e => e.Id == 6 && e.Field("PolicyVersion") == version);
                Assert.Equal(outcome, terminal.Field("Outcome"));
                Assert.Equal(outcome, rule.Field("Outcome"));
                Assert.Equal(terminal.Field("CorrelationId"), rule.Field("CorrelationId"));
                Assert.Equal(32, terminal.Field("CorrelationId").Length);
                Assert.Equal("primary", rule.Field("RuleId"));
            }
            var b = store.Active;
            await Replace("{rejected-json-private-marker");
            await logs.Wait(l => l.Any(e => e.Id == 7 && e.Field("Reason") == "invalid_configuration"));
            Assert.Same(b, store.Active);
            var rejected = await Summary(client, "Block", "v2");
            Assert.True(rejected["policyReload"]!["enabled"]!.GetValue<bool>());
            Assert.Equal("rejected", rejected["policyReload"]!["status"]!.GetValue<string>());
            Assert.Equal("invalid_configuration", rejected["policyReload"]!["reason"]!.GetValue<string>());
            Assert.NotNull(rejected["policyReload"]!["lastCheckedAtUtc"]);
            Assert.NotNull(rejected["policyReload"]!["lastAppliedAtUtc"]);
            Assert.Equal(initial["startedAtUtc"]!.ToJsonString(), rejected["startedAtUtc"]!.ToJsonString());
            Assert.Equal(1, rejected["metrics"]!["outcomes"]!["inspected"]!.GetValue<int>());
            Assert.Equal(1, rejected["metrics"]!["outcomes"]!["policy_blocked"]!.GetValue<int>());
            using (var stillBlocked = await client.GetAsync("/protected/still")) Assert.Equal(HttpStatusCode.BadGateway, stillBlocked.StatusCode);
            await Replace(Document("Mask", "v3"));
            await logs.Wait(l => l.Count(e => e.Id == 7 && e.Field("Status") == "applied") == 3);
            using (var recovered = await client.GetAsync("/protected/recovered")) await Masked(recovered);
            using (var excluded = await client.GetAsync("/protected/text")) Assert.Equal("synthetic", await excluded.Content.ReadAsStringAsync());
            await logs.Wait(l => l.Any(e => e.Id == 4));
            var exclusion = Assert.Single(logs.All, e => e.Id == 4);
            Assert.Equal("v3", exclusion.Field("PolicyVersion"));
            Assert.Equal("excluded_media_type", exclusion.Field("Reason"));
            Assert.Equal("not_evaluated", exclusion.Field("RuleEvaluation"));
            var final = await Summary(client, "Mask", "v3");
            Assert.Equal("valid", final["policyReload"]!["status"]!.GetValue<string>());
            Assert.Null(final["policyReload"]!["reason"]);
            Assert.Equal(2, final["metrics"]!["outcomes"]!["inspected"]!.GetValue<int>());
            Assert.Equal(2, final["metrics"]!["outcomes"]!["policy_blocked"]!.GetValue<int>());
            Assert.Equal(2, final["metrics"]!["validatedMasks"]!.GetValue<int>());
            using var guarded = new HttpRequestMessage(HttpMethod.Get, SummaryPath);
            guarded.Headers.Add("Sec-Fetch-Site", "cross-site");
            using var denied = await client.SendAsync(guarded);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var post = await client.PostAsync(SummaryPath, new StringContent("synthetic"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            Assert.All(logs.All, e => Assert.Contains(e.Category, new[] { "Gateway.PanResponseTransform", "Gateway.LocalPolicyReloadService" }));
            Assert.All(logs.All.Where(e => e.Id == 7), CheckNotification);
            var collected = string.Join("\n", logs.All.Select(e => e.Text));
            foreach (var secret in new[] { FilePath, "private-policy-marker", "rejected-json-private-marker", "request-private-marker", "4111111111111111" })
                Assert.DoesNotContain(secret, collected);
        }
        finally { release.TrySetResult(); await held.WaitAsync(Limit); await gateway.StopAsync(); await upstream.StopAsync(); }
    }

    [Fact]
    public async Task Invalid_initial_file_never_listens_and_reports_generic_failure()
    {
        await Replace("{private-init-payload");
        var logs = new Logs();
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await using var gateway = GatewayApp("http://127.0.0.1:1/", logs, listen: $"http://127.0.0.1:{port}");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.StartAsync());
        Assert.Equal("Local policy snapshot initialization failed.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.False(gateway.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Empty(gateway.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient { Timeout = Limit };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/health/ready"));
        Assert.DoesNotContain("private-init-payload", string.Join("\n", logs.All.Select(e => e.Text)));
        Assert.Empty(logs.All);
    }

    [Theory]
    [InlineData("unknown", "invalid_configuration")]
    [InlineData("duplicate", "invalid_configuration")]
    [InlineData("depth", "invalid_configuration")]
    [InlineData("config", "invalid_configuration")]
    [InlineData("coverage", "invalid_configuration")]
    [InlineData("remove", "invalid_configuration")]
    [InlineData("add", "incompatible_snapshot")]
    [InlineData("reassign", "incompatible_snapshot")]
    [InlineData("budget", "incompatible_snapshot")]
    [InlineData("unversioned", "incompatible_snapshot")]
    [InlineData("missing", "read_failed")]
    [InlineData("oversize", "too_large")]
    public async Task Rejected_real_files_retain_reference_and_recover(string caseId, string reason)
    {
        await Replace(Document());
        var reader = new LocalPolicyFileReader(FilePath, Routes);
        var store = new LocalPolicySnapshots(await reader.ReadAsync(default));
        var before = store.Active;
        var logs = new Logs();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        using var service = new LocalPolicyReloadService(reader, store, TimeProvider.System, factory.CreateLogger<LocalPolicyReloadService>());
        var d = Document("Mask", "v2");
        switch (caseId)
        {
            case "unknown": Policy(d)["private-unknown"] = true; break;
            case "config": Policy(d)["Mode"] = "invalid-private"; break;
            case "coverage": d["PanPolicies"]!["CoverageContract"]!.AsObject().Remove("Required"); break;
            case "remove": d["PanPolicies"]!["Policies"] = new JsonArray(); break;
            case "add":
                var extra = Policy(d).DeepClone(); extra["RouteId"] = "passthrough"; extra["PolicyId"] = "second";
                d["PanPolicies"]!["Policies"]!.AsArray().Add(extra); break;
            case "reassign": Policy(d)["PolicyId"] = "other"; break;
            case "budget": d["PanPolicies"]!["InspectionBudget"]!["MaxConcurrentInspections"] = 5; break;
            case "unversioned": Policy(d)["Mode"] = "Block"; Policy(d)["Version"] = "v1"; break;
        }
        var text = caseId switch
        {
            "duplicate" => "{\"PanPolicies\":{},\"PanPolicies\":{}}",
            "depth" => "{\"PanPolicies\":" + new string('[', 65) + "0" + new string(']', 65) + "}",
            "oversize" => d.ToJsonString() + new string(' ', LocalPolicyFileReader.MaximumBytes),
            _ => d.ToJsonString()
        };
        if (caseId == "missing") File.Delete(FilePath); else await Replace(text);
        await service.ReloadAsync(default);
        await service.ReloadAsync(default);
        Assert.Same(before, store.Active);
        var notification = Assert.Single(logs.All);
        CheckNotification(notification);
        Assert.Equal(reason, notification.Field("Reason"));
        Assert.Equal("rejected", notification.Field("Status"));
        await Replace(Document()); // Unchanged valid resets rejection suppression without notification.
        await service.ReloadAsync(default);
        Assert.Same(before, store.Active);
        Assert.Single(logs.All);
        if (caseId == "missing") File.Delete(FilePath); else await Replace(text);
        await service.ReloadAsync(default);
        Assert.Equal(2, logs.All.Count);
        await Replace(Document("Block", "v2"));
        await service.ReloadAsync(default);
        Assert.NotSame(before, store.Active);
        Assert.Equal("Block", store.Active.Catalog.Find("protected-payment")!.Mode);
        Assert.Equal("v2", store.Active.Catalog.Find("protected-payment")!.Version);
        Assert.Equal("applied", logs.All.Last().Field("Status"));
        using var exclusive = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite); // Every completed read closed its handle.
    }

    [Fact]
    public async Task Junction_ancestor_and_leaf_are_rejected()
    {
        await Replace(Document());
        var target = Path.Combine(directory, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "policy.json"), Document().ToJsonString());
        var link = Path.Combine(directory, "junction");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await process.WaitForExitAsync().WaitAsync(Limit);
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            var failure = await Assert.ThrowsAsync<LocalPolicyReadException>(() => new LocalPolicyFileReader(Path.Combine(link, "policy.json"), Routes).ReadAsync(default));
            Assert.Equal("read_failed", failure.Reason);
            // A junction used as the leaf is also a real reparse entry, not a mocked attribute.
            Assert.Equal("read_failed", (await Assert.ThrowsAsync<LocalPolicyReadException>(() => new LocalPolicyFileReader(link, Routes).ReadAsync(default))).Reason);
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("ads")]
    [InlineData("device")]
    [InlineData("network")]
    public async Task Unsafe_paths_are_rejected_before_open(string caseId)
    {
        await Replace(Document());
        var path = caseId switch { "ads" => FilePath + ":alternate", "device" => @"\\.\pipe\MVP35-never-open", _ => @"\\MVP35.invalid\share\policy.json" };
        var failure = await Assert.ThrowsAsync<LocalPolicyReadException>(() => new LocalPolicyFileReader(path, Routes).ReadAsync(default));
        Assert.Equal("read_failed", failure.Reason);
    }

    [Fact]
    public async Task Disabled_host_creates_no_reader_or_timer_and_keeps_baseline()
    {
        await Replace(Document("Block", "v2"));
        var clock = new Clock();
        await using var gateway = GatewayApp("http://127.0.0.1:1/", new Logs(), clock, enabled: false);
        await gateway.StartAsync();
        using var client = Client(gateway);
        var before = gateway.Services.GetRequiredService<LocalPolicySnapshots>().Active;
        await Summary(client, "Mask", "v1");
        await Replace(Document("Observe", "v3"));
        await Summary(client, "Mask", "v1");
        Assert.Same(before, gateway.Services.GetRequiredService<LocalPolicySnapshots>().Active);
        Assert.Null(gateway.Services.GetService<LocalPolicyFileReader>());
        Assert.DoesNotContain(gateway.Services.GetServices<IHostedService>(), s => s is LocalPolicyReloadService);
        Assert.Equal(0, clock.Count);
        await gateway.StopAsync();
    }

    [Fact]
    public async Task Missing_capture_fails_before_upstream_but_passthrough_remains_untouched()
    {
        await Replace(Document());
        var calls = 0;
        await using var upstream = Upstream(async c => { Interlocked.Increment(ref calls); c.Response.ContentType = "application/json"; await c.Response.WriteAsync(Payload); });
        await upstream.StartAsync();
        await using var gateway = GatewayApp(Address(upstream) + "/", new Logs(), removeCapture: true);
        await gateway.StartAsync();
        using var client = Client(gateway);
        using var protectedResponse = await client.GetAsync("/protected/missing");
        Assert.Contains(protectedResponse.StatusCode, new[] { HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway });
        Assert.Equal(0, Volatile.Read(ref calls));
        var body = await protectedResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("4111111111111111", body);
        Assert.DoesNotContain("Protected route policy", body);
        using var passthrough = await client.GetAsync("/passthrough/original");
        Assert.Equal(HttpStatusCode.OK, passthrough.StatusCode);
        Assert.Equal(Payload, await passthrough.Content.ReadAsStringAsync());
        Assert.Equal(1, Volatile.Read(ref calls));
        await gateway.StopAsync(); await upstream.StopAsync();
    }

    [Fact]
    public async Task Polling_is_sequential_when_notification_blocks_and_logger_failure_cannot_rollback()
    {
        await Replace(Document());
        var clock = new Clock();
        var logs = new Logs();
        await using var gateway = GatewayApp("http://127.0.0.1:1/", logs, clock);
        await gateway.StartAsync();
        var first = await clock.Next();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unblock = new ManualResetEventSlim();
        logs.OnEntry = e =>
        {
            if (e.Id != 7) return;
            entered.TrySetResult();
            if (!unblock.Wait(Limit)) throw new TimeoutException("Synthetic notification barrier timed out.");
        };
        try
        {
            await Replace(Document("Block", "v2"));
            first.Fire();
            await entered.Task.WaitAsync(Limit);
            var store = gateway.Services.GetRequiredService<LocalPolicySnapshots>();
            var published = store.Active;
            Assert.Equal("v2", published.Catalog.Find("protected-payment")!.Version);
            Assert.Equal(1, clock.Count); // No second timer while the first iteration is still blocked.
            await Replace(Document("Mask", "v3"));
            Assert.Same(published, store.Active);
            logs.OnEntry = e => { if (e.Id == 7) throw new InvalidOperationException("Synthetic logger failure after publication."); };
            unblock.Set();
            var second = await clock.Next();
            Assert.Same(published, store.Active);
            second.Fire();
            await clock.Next();
            Assert.Equal("v3", store.Active.Catalog.Find("protected-payment")!.Version);
            Assert.Equal("Mask", store.Active.Catalog.Find("protected-payment")!.Mode);
            Assert.Equal(3, logs.All.Count(e => e.Id == 7 && e.Field("Status") == "applied"));
        }
        finally { unblock.Set(); await gateway.StopAsync().WaitAsync(Limit); }
    }

    [Fact]
    public async Task Stop_during_pending_cooperative_read_cancels_disposes_and_joins_without_publication()
    {
        await Replace(Document());
        var store = new LocalPolicySnapshots(await new LocalPolicyFileReader(FilePath, Routes).ReadAsync(default));
        var clock = new Clock();
        var logs = new Logs();
        var paths = new ConcurrentQueue<string>();
        var pending = new CooperativeReadStream();
        var opens = 0;
        var reader = new LocalPolicyFileReader(FilePath, Routes, path =>
        {
            paths.Enqueue(path);
            return Interlocked.Increment(ref opens) == 1
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    1, FileOptions.Asynchronous | FileOptions.SequentialScan)
                : pending;
        });
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        using var service = new LocalPolicyReloadService(reader, store, clock, factory.CreateLogger<LocalPolicyReloadService>());
        try
        {
            await service.StartAsync(default); // Initial snapshot actually reads the real validated file.
            var timer = await clock.Next();
            var before = store.Active;
            var beforeView = store.Capture();
            await Replace(Document("Block", "v2"));
            timer.Fire();
            var readToken = await pending.Entered.Task.WaitAsync(Limit);
            Assert.True(readToken.CanBeCanceled);
            Assert.False(readToken.IsCancellationRequested);
            Assert.False(service.ExecuteTask!.IsCompleted);
            Assert.Equal(1, pending.ActiveReads);
            Assert.Equal(1, pending.ReadCalls);
            Assert.True(timer.Disposed);
            for (var i = 0; i < 100; i++) timer.Fire(); // Exhausted one-shot ticks cannot queue more reads.
            Assert.Equal(1, clock.Count); // No next delay exists while ReadAsync is pending.
            Assert.Equal(2, Volatile.Read(ref opens));
            Assert.Same(before, store.Active);

            await service.StopAsync(default).WaitAsync(Limit);
            await pending.Canceled.Task.WaitAsync(Limit);
            await pending.Completed.Task.WaitAsync(Limit);
            Assert.True(readToken.IsCancellationRequested);
            Assert.Equal(new[] { "read_entered", "token_canceled", "read_completed", "disposed" }, pending.Events.ToArray());
            Assert.Equal(0, pending.ActiveReads);
            Assert.Equal(1, pending.ReadCalls);
            Assert.True(pending.WasDisposed);
            Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
            Assert.Same(beforeView, store.Capture());
            Assert.Same(before, store.Active);
            Assert.Single(logs.All, e => e.Id == 7 && e.Field("Status") == "applied");
            Assert.DoesNotContain(logs.All, e => e.Field("Status") == "rejected");

            await Replace(Document("Mask", "v3"));
            for (var i = 0; i < 100; i++) timer.Fire();
            Assert.Equal(1, clock.Count);
            Assert.Equal(2, Volatile.Read(ref opens));
            Assert.Equal(new[] { FilePath, FilePath }, paths.ToArray());
            Assert.Same(before, store.Active);
            Assert.Single(logs.All);
            using var exclusive = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            pending.ReleaseForCleanup(); // Only to prevent orphan work if a preceding assertion failed.
            await service.StopAsync(default).WaitAsync(Limit);
            await pending.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stream_opener_runs_only_after_real_path_validation()
    {
        await Replace(Document());
        var calls = 0;
        string? openedPath = null;
        Stream Open(string path)
        {
            Interlocked.Increment(ref calls);
            openedPath = path;
            return new MemoryStream(Encoding.UTF8.GetBytes(Document().ToJsonString()), writable: false);
        }
        var valid = await new LocalPolicyFileReader(FilePath, Routes, Open).ReadAsync(default);
        Assert.Equal("Mask", valid.Catalog.Find("protected-payment")!.Mode);
        Assert.Equal(1, calls);
        Assert.Equal(FilePath, openedPath);
        calls = 0;

        var target = Path.Combine(directory, "opener-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "policy.json"), Document().ToJsonString());
        var junction = Path.Combine(directory, "opener-junction");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await process.WaitForExitAsync().WaitAsync(Limit);
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            Assert.True(File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint));
            foreach (var path in new[] { FilePath + ":alternate", @"\\.\pipe\MVP35-never-open",
                @"\\MVP35.invalid\share\policy.json", "relative.json", Path.Combine(directory, "absent.json"),
                directory, junction, Path.Combine(junction, "policy.json") })
            {
                var error = await Assert.ThrowsAsync<LocalPolicyReadException>(() => new LocalPolicyFileReader(path, Routes, Open).ReadAsync(default));
                Assert.Equal("read_failed", error.Reason);
                Assert.Equal(0, calls); // A valid injected stream cannot bypass location validation.
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalPolicyFileReader(FilePath, Routes, Open).ReadAsync(cancellation.Token));
            Assert.Equal(0, calls);
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public async Task Stop_cancels_pending_timer_without_later_publication()
    {
        await Replace(Document());
        var clock = new Clock();
        var logs = new Logs();
        await using var gateway = GatewayApp("http://127.0.0.1:1/", logs, clock);
        await gateway.StartAsync();
        var timer = await clock.Next();
        var store = gateway.Services.GetRequiredService<LocalPolicySnapshots>();
        var before = store.Active;
        await gateway.StopAsync().WaitAsync(Limit);
        Assert.True(timer.Disposed);
        await Replace(Document("Block", "v2"));
        timer.Fire();
        Assert.Same(before, store.Active);
        Assert.Equal(1, clock.Count);
        Assert.Single(logs.All, e => e.Id == 7);
        using var exclusive = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Concurrent_summaries_and_requests_never_mix_policy_versions()
    {
        var initialTime = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        await Replace(Document());
        var entered = Channel.CreateUnbounded<bool>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = Upstream(async c =>
        {
            entered.Writer.TryWrite(true);
            await release.Task.WaitAsync(c.RequestAborted);
            c.Response.ContentType = "application/json";
            await c.Response.WriteAsync(Payload);
        });
        await upstream.StartAsync();
        var clock = new Clock { Now = initialTime };
        var logs = new Logs();
        await using var gateway = GatewayApp(Address(upstream) + "/", logs, clock);
        await gateway.StartAsync();
        using var client = Client(gateway);
        var timer = await clock.Next();
        var old = Enumerable.Range(0, 3).Select(i => client.GetAsync("/protected/old-" + i)).ToArray();
        try
        {
            for (var i = 0; i < 3; i++) await entered.Reader.ReadAsync().AsTask().WaitAsync(Limit);
            await Replace(Document("Block", "v2"));
            clock.Now = initialTime.AddSeconds(1);
            timer.Fire();
            await clock.Next();
            // 100 simultaneous readers; dashboard has a deliberate four-request admission cap.
            var readers = Enumerable.Range(0, 100).Select(async _ =>
            {
                using var response = await client.GetAsync(SummaryPath);
                Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable });
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return false;
                var d = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                var p = Assert.Single(d["policies"]!.AsArray())!;
                Assert.Equal("Block", p["mode"]!.GetValue<string>());
                Assert.Equal("v2", p["version"]!.GetValue<string>());
                Assert.Equal("valid", d["policyReload"]!["status"]!.GetValue<string>());
                Assert.Equal(initialTime.AddSeconds(1), d["policyReload"]!["lastAppliedAtUtc"]!.GetValue<DateTimeOffset>());
                return true;
            }).ToArray();
            Assert.Contains(true, await Task.WhenAll(readers));
            var fresh = client.GetAsync("/protected/new");
            await entered.Reader.ReadAsync().AsTask().WaitAsync(Limit);
            release.TrySetResult();
            foreach (var task in old) { using var response = await task; await Masked(response); }
            using (var response = await fresh) Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            await logs.Wait(l => l.Count(e => e.Id == 5) == 4);
            Assert.Equal(3, logs.All.Count(e => e.Id == 5 && e.Field("PolicyVersion") == "v1" && e.Field("Outcome") == "inspected"));
            Assert.Equal(1, logs.All.Count(e => e.Id == 5 && e.Field("PolicyVersion") == "v2" && e.Field("Outcome") == "policy_blocked"));
            // Overlap 100 genuine file reads/publications with HTTP DTO reads. Each version has
            // independently specified mode/limit metadata, so mixed projections cannot pass.
            var service = gateway.Services.GetServices<IHostedService>().OfType<LocalPolicyReloadService>().Single();
            var expected = new Dictionary<string, (string Mode, int Bytes)> { ["v2"] = ("Block", 1048576) };
            var appliedTimes = new Dictionary<string, DateTimeOffset> { ["v2"] = initialTime.AddSeconds(1) };
            for (var i = 0; i < 100; i++)
            {
                var version = "stress-" + i;
                var mode = i % 2 == 0 ? "Mask" : "Block";
                var bytes = 2048 + i;
                expected.Add(version, (mode, bytes));
                clock.Now = initialTime.AddSeconds(i + 2);
                appliedTimes.Add(version, clock.Now.Value);
                var d = Document(mode, version);
                Policy(d)["MaxBodyBytes"] = bytes;
                await Replace(d);
                var read = client.GetStringAsync(SummaryPath);
                await Task.WhenAll(read, service.ReloadAsync(default));
                var summary = JsonNode.Parse(await read)!;
                var policy = summary["policies"]![0]!;
                var tuple = expected[policy["version"]!.GetValue<string>()];
                Assert.Equal(tuple.Mode, policy["mode"]!.GetValue<string>());
                Assert.Equal(tuple.Bytes, policy["maxBodyBytes"]!.GetValue<int>());
                var reload = summary["policyReload"]!;
                Assert.True(reload["enabled"]!.GetValue<bool>());
                Assert.Equal("valid", reload["status"]!.GetValue<string>());
                Assert.Null(reload["reason"]);
                var appliedAt = appliedTimes[policy["version"]!.GetValue<string>()];
                Assert.Equal(appliedAt, reload["lastAppliedAtUtc"]!.GetValue<DateTimeOffset>());
                Assert.Equal(appliedAt, reload["lastCheckedAtUtc"]!.GetValue<DateTimeOffset>());
            }
            await Summary(client, "Block", "stress-99");
        }
        finally { release.TrySetResult(); await Task.WhenAll(old).WaitAsync(Limit); await gateway.StopAsync(); await upstream.StopAsync(); }
    }

    private static void CheckNotification(Entry e)
    {
        Assert.Equal("Gateway.LocalPolicyReloadService", e.Category);
        Assert.Equal(7, e.Id);
        Assert.Null(e.Exception);
        Assert.Equal(new[] { "Reason", "Status", "{OriginalFormat}" }.Order(StringComparer.Ordinal), e.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(e.Field("Status"), new[] { "applied", "rejected" });
        Assert.Contains(e.Field("Reason"), new[] { "none", "invalid_configuration", "read_failed", "too_large", "incompatible_snapshot" });
    }
    private sealed record Entry(string Category, int Id, Dictionary<string, object?> Fields, Exception? Exception, string Text)
    {
        internal string Field(string name) => Fields.TryGetValue(name, out var value) ? value?.ToString() ?? "" : "";
    }
    private sealed class Logs : ILoggerProvider
    {
        private readonly object gate = new();
        private readonly List<Entry> entries = [];
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<Entry>? OnEntry;
        internal IReadOnlyList<Entry> All { get { lock (gate) return entries.ToArray(); } }
        internal async Task Wait(Func<IReadOnlyList<Entry>, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(Limit);
            while (true)
            {
                Task next;
                lock (gate) { if (predicate(entries)) return; next = changed.Task; }
                await next.WaitAsync(timeout.Token);
            }
        }
        public ILogger CreateLogger(string category) => new Logger(this, category);
        public void Dispose() { }
        private sealed class Logger(Logs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value) : [];
                var entry = new Entry(category, id.Id, fields, exception, formatter(state, exception));
                lock (owner.gate)
                {
                    owner.entries.Add(entry);
                    var previous = owner.changed;
                    owner.changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    previous.TrySetResult();
                }
                owner.OnEntry?.Invoke(entry);
            }
        }
    }
    // This injected stream tests cooperative cancellation, not a stalled OS driver.
    private sealed class CooperativeReadStream : MemoryStream
    {
        private readonly TaskCompletionSource<int> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeReads, readCalls, disposed;
        internal readonly TaskCompletionSource<CancellationToken> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<string> Events = new();
        internal int ActiveReads => Volatile.Read(ref activeReads);
        internal int ReadCalls => Volatile.Read(ref readCalls);
        internal bool WasDisposed => Volatile.Read(ref disposed) != 0;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref activeReads);
            Interlocked.Increment(ref readCalls);
            Events.Enqueue("read_entered");
            Entered.TrySetResult(cancellationToken);
            try
            {
                using var registration = cancellationToken.Register(() =>
                {
                    Events.Enqueue("token_canceled");
                    Canceled.TrySetResult();
                    pending.TrySetCanceled(cancellationToken);
                });
                return await pending.Task;
            }
            finally
            {
                Interlocked.Decrement(ref activeReads);
                Events.Enqueue("read_completed");
                Completed.TrySetResult();
            }
        }
        internal void ReleaseForCleanup() => pending.TrySetResult(0);
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Events.Enqueue("disposed");
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset? Now;
        public override DateTimeOffset GetUtcNow() => Now ?? base.GetUtcNow();
        private readonly Channel<Timer> timers = Channel.CreateUnbounded<Timer>();
        internal int Count;
        internal async Task<Timer> Next() => await timers.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Inspection deadlines share DI TimeProvider; only control the reload timer.
            if (dueTime != TimeSpan.FromSeconds(1)) return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
            Assert.Equal(TimeSpan.FromSeconds(1), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            Interlocked.Increment(ref Count);
            var timer = new Timer(callback, state);
            timers.Writer.TryWrite(timer);
            return timer;
        }
    }
    private sealed class Timer(TimerCallback callback, object? state) : ITimer
    {
        internal bool Disposed;
        internal void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class RemoveCaptureFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                var original = context.Features;
                _ = context.RequestServices; // Materialize the factory-owned request scope before decorating.
                // IHttpContextFactory creates DefaultHttpContext; reinitialize with a feature decorator.
                var testContext = new DefaultHttpContext(new WithoutCapture(original));
                await continuation(testContext);
            });
            next(app);
        };
    }
    private sealed class WithoutCapture(IFeatureCollection inner) : IFeatureCollection
    {
        public bool IsReadOnly => false;
        public int Revision => inner.Revision;
        public object? this[Type key] { get => key == typeof(LocalPolicyRequestCapture) ? null : inner[key]; set { if (key != typeof(LocalPolicyRequestCapture)) inner[key] = value; } }
        public TFeature? Get<TFeature>() => (TFeature?)this[typeof(TFeature)];
        public void Set<TFeature>(TFeature? instance) => this[typeof(TFeature)] = instance;
        public IEnumerator<KeyValuePair<Type, object>> GetEnumerator() => inner.Where(p => p.Key != typeof(LocalPolicyRequestCapture)).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
