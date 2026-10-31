using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

[Collection("Secondary PAN telemetry isolation")]
public sealed class PrivacyInspectionEventTests
{
    private const string Template = "Privacy inspection completed; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} validated masks {ValidatedMasks} validated explicit sensitive matches {ValidatedExplicitSensitiveMatches} validated secondary PAN matches {ValidatedSecondaryPanMatches}.";
    private const string Body = "{\"selected\":\"4111111111111111\"}";
    private static CompiledPanPolicy Policy(string mode = "Mask") => new("EventPolicy", "v1", "protected-payment", ["selected"], 4096) { Mode = mode };
    private static InspectionBudget Budget() => new(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = 4096, InspectionTimeoutSeconds = 1 } }));

    [Theory]
    [InlineData(PrivacyInspectionOutcome.Inspected, "inspected")]
    [InlineData(PrivacyInspectionOutcome.Blocked, "blocked")]
    [InlineData(PrivacyInspectionOutcome.CapacityExceeded, "capacity_exceeded")]
    [InlineData(PrivacyInspectionOutcome.Canceled, "canceled")]
    [InlineData(PrivacyInspectionOutcome.WriteError, "write_error")]
    [InlineData(PrivacyInspectionOutcome.Faulted, "faulted")]
    [InlineData(PrivacyInspectionOutcome.TimedOut, "timed_out")]
    [InlineData(PrivacyInspectionOutcome.PolicyBlocked, "policy_blocked")]
    public void Eight_outcomes_have_exact_schema_via_terminal_seam(PrivacyInspectionOutcome outcome, string name)
    {
        using var metrics = new Metrics();
        var budget = Budget(); using var telemetry = new PrivacyTelemetry(budget); var logs = new Logs();
        var transform = new PanResponseTransform(logs, telemetry); var policy = Policy();
        using var held = outcome == PrivacyInspectionOutcome.CapacityExceeded ? budget.TryAcquire(4096) : null;
        var callbacks = 0;
        using var measurement = telemetry.TryStartInspection(policy, TimeProvider.System, summary =>
        {
            Interlocked.Increment(ref callbacks);
            Assert.Equal(1, metrics.Terminals);
            Assert.Equal(outcome == PrivacyInspectionOutcome.CapacityExceeded ? (1, 4096L) : (0, 0L), budget.Snapshot);
            transform.RecordInspectionCompleted(policy, "synthetic-api", summary);
        });
        if (measurement is not null)
        {
            measurement.ValidatedMasks = 2;
            measurement.ValidatedExplicitSensitiveMatches = 9007199254740993L;
            measurement.ValidatedSecondaryPanMatches = 7;
            measurement.Complete(outcome); measurement.Dispose(); measurement.Complete(PrivacyInspectionOutcome.Canceled);
        }
        else Assert.Equal(PrivacyInspectionOutcome.CapacityExceeded, outcome);
        Assert.Equal(1, callbacks);
        AssertEvent(Assert.Single(logs.Events), name, outcome == PrivacyInspectionOutcome.CapacityExceeded ? 0 : 2,
            outcome == PrivacyInspectionOutcome.CapacityExceeded ? 0 : 9007199254740993L, outcome == PrivacyInspectionOutcome.CapacityExceeded ? 0 : 7, "synthetic-api");
    }

    [Fact]
    public void Concurrent_complete_and_dispose_notify_once()
    {
        var budget = Budget(); using var telemetry = new PrivacyTelemetry(budget); var callbacks = new ConcurrentQueue<PrivacyTelemetry.InspectionSummary>();
        using var m = telemetry.TryStartInspection(Policy(), TimeProvider.System, s => { callbacks.Enqueue(s); throw new OperationCanceledException(); })!;
        m.ValidatedMasks = 2; m.ValidatedExplicitSensitiveMatches = 3; m.ValidatedSecondaryPanMatches = 7;
        Parallel.For(0, 64, i => { if (i % 2 == 0) m.Complete(PrivacyInspectionOutcome.Inspected); else m.Dispose(); });
        m.Dispose(); m.Complete(PrivacyInspectionOutcome.Canceled);
        var summary = Assert.Single(callbacks);
        Assert.Contains(summary.Outcome, new[] { PrivacyInspectionOutcome.Inspected, PrivacyInspectionOutcome.Faulted });
        Assert.Equal((2L, 3L, 7L), (summary.ValidatedMasks, summary.ValidatedExplicitSensitiveMatches, summary.ValidatedSecondaryPanMatches));
        Assert.Equal(1, telemetry.DashboardMetrics.Duration.Samples); Assert.Equal((0, 0L), budget.Snapshot);
        using var next = telemetry.TryStartInspection(Policy()); Assert.NotNull(next);
        m.Dispose(); Assert.Equal((1, 4096L), budget.Snapshot);
    }

    [Theory]
    [InlineData("Mask", "PAN")] [InlineData("Block", "PAN")] [InlineData("Observe", "PAN")]
    [InlineData("Mask", "Email")] [InlineData("Block", "Email")] [InlineData("Observe", "Email")]
    [InlineData("Mask", "Phone")] [InlineData("Block", "Phone")] [InlineData("Observe", "Phone")]
    public async Task Transform_modes_partition_matches_and_masks(string mode, string transformation)
    {
        const string body = "{\"selected\":[\"\",null,\"4111111111111111\"],\"selected\":[\"incompatible\"],\"additional\":\"4111111111111111\",\"other\":\"4111111111111111\"}";
        using var f = new Fixture(body, Policy(mode) with { Segments = ["selected", "[*]"], Transformation = transformation,
            AdditionalRules = [new(["additional"], transformation)], SecondaryPanDetection = true });
        await f.Run();
        AssertEvent(Assert.Single(f.Logs.Events), mode == "Block" ? "policy_blocked" : "inspected", mode == "Mask" ? 5 : 0, 4, 1);
        Assert.Equal(mode == "Block" ? 502 : 200, f.Context.HttpContext.Response.StatusCode);
        if (mode == "Observe") Assert.Equal(body, f.Text);
        if (mode == "Block") Assert.Equal("{\"code\":\"privacy_response_blocked\"}", f.Text);
        if (mode == "Mask") { Assert.DoesNotContain("4111111111111111", f.Text); Assert.Contains("null", f.Text); }
        Assert.Equal((0, 0L), f.Budget.Snapshot);
        Assert.Equal(new[] { "primary", "additional-0", "secondary-pan" }, f.Logs.Rules.Select(e => e.State["RuleId"]));
        Assert.Equal(new long[] { 3, 1, 1 }, f.Logs.Rules.Select(e => Assert.IsType<long>(e.State["ValidatedMatches"])));
        Assert.All(f.Logs.Rules, e => Assert.Equal(mode == "Mask" ? (long)e.State["ValidatedMatches"]! : 0L, e.State["ValidatedMasks"]));
        Assert.All(f.Logs.Rules, e => Assert.Equal(f.Logs.Events.Single().State["CorrelationId"], e.State["CorrelationId"]));
    }

    [Theory]
    [InlineData("null")] [InlineData("absent")] [InlineData("intermediate")]
    public async Task No_matches_still_emits_zero_counts(string scenario)
    {
        var body = scenario == "null" ? "{\"selected\":null}" : scenario == "absent" ? "{}" : "{\"selected\":42}";
        using var f = new Fixture(body, scenario == "intermediate" ? Policy() with { Segments = ["selected", "nested"] } : Policy());
        await f.Run(); Assert.Equal(body, f.Text); AssertEvent(Assert.Single(f.Logs.Events), "inspected", 0, 0, 0);
        Assert.Empty(f.Logs.Rules);
    }

    [Theory]
    [InlineData("Mask", false)] [InlineData("Block", false)] [InlineData("Observe", false)]
    [InlineData("Mask", true)] [InlineData("Block", true)] [InlineData("Observe", true)]
    public async Task Late_validation_failure_discards_counts(string mode, bool multiple)
    {
        using var f = new Fixture("{\"selected\":\"4111111111111111\",\"selected\":42}", Policy(mode) with { AdditionalRules = multiple ? [new(["other"], "Email")] : [] });
        await f.Run(); Assert.Equal(502, f.Context.HttpContext.Response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", f.Text);
        AssertEvent(Assert.Single(f.Logs.Events), "blocked", 0, 0, 0);
        Assert.Empty(f.Logs.Rules);
    }

    [Theory]
    [InlineData("Mask", "cancel")] [InlineData("Mask", "timeout")] [InlineData("Mask", "fault")] [InlineData("Mask", "write")]
    [InlineData("Block", "cancel")] [InlineData("Block", "timeout")] [InlineData("Block", "fault")] [InlineData("Block", "write")]
    [InlineData("Observe", "cancel")] [InlineData("Observe", "timeout")] [InlineData("Observe", "fault")] [InlineData("Observe", "write")]
    public async Task Postcommit_failures_retain_counts(string mode, string failure)
    {
        using var f = new Fixture("{\"selected\":\"4111111111111111\",\"other\":\"4111111111111111\"}", Policy(mode) with { SecondaryPanDetection = true });
        f.Checkpoint = _ =>
        {
            Assert.Empty(f.Logs.Events);
            if (failure == "cancel") f.Caller.Cancel();
            if (failure == "timeout") f.Clock.Advance();
            if (failure == "fault") throw new InvalidOperationException("checkpoint_marker");
            return ValueTask.CompletedTask;
        };
        if (failure == "write") f.Context.HttpContext.Response.Body = new FailedWrite();
        if (failure is "cancel" or "timeout") await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run);
        else if (failure == "fault") await Assert.ThrowsAsync<InvalidOperationException>(f.Run);
        else await Assert.ThrowsAsync<IOException>(f.Run);
        AssertEvent(Assert.Single(f.Logs.Events), failure == "cancel" ? "canceled" : failure == "timeout" ? "timed_out" : failure == "write" ? "write_error" : "faulted", mode == "Mask" ? 2 : 0, 1, 1);
        Assert.Equal(2, f.Logs.Rules.Count);
        Assert.All(f.Logs.Rules, e => { Assert.Equal(f.Logs.Events.Single().State["Outcome"], e.State["Outcome"]); Assert.Equal(1L, e.State["ValidatedMatches"]); Assert.Equal(mode == "Mask" ? 1L : 0L, e.State["ValidatedMasks"]); });
        Assert.Equal((0, 0L), f.Budget.Snapshot);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Precommit_cancel_or_timeout_discards_counts(bool cancel, bool afterWriter)
    {
        using var f = new Fixture(Body, Policy()); var traversed = false; var checks = 0; var triggered = false;
        // Same deterministic boundary observation as MVP28/30; not proof of timer delivery.
        f.Clock.OnTimestamp = () =>
        {
            var inside = new StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == "WriteSelected");
            if (inside) { traversed = true; checks++; }
            if (triggered || (afterWriter ? !traversed || inside : !inside || checks != 4)) return;
            triggered = true; f.Clock.OnTimestamp = null;
            if (cancel) f.Caller.Cancel(); else f.Clock.Advance();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run);
        Assert.True(triggered); Assert.Empty(f.Text);
        AssertEvent(Assert.Single(f.Logs.Events), cancel ? "canceled" : "timed_out", 0, 0, 0);
        Assert.Empty(f.Logs.Rules);
    }

    [Fact]
    public async Task Blocking_event_logger_runs_after_metrics_and_lease_release()
    {
        using var metrics = new Metrics(); using var f = new Fixture(Body, Policy());
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Logs.OnEvent = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); };
        var pending = Task.Run(f.Run);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted); Assert.Equal((0, 0L), f.Budget.Snapshot);
            Assert.Equal(1, metrics.Terminals); Assert.Equal(1, f.Telemetry.DashboardMetrics.Duration.Samples);
            Assert.Equal(new[] { "gateway.privacy.inspection.outcomes", "gateway.privacy.inspection.duration", "gateway.privacy.inspection.input_body_bytes", "gateway.privacy.inspection.output_body_bytes", "gateway.privacy.inspection.masks", "gateway.privacy.inspection.explicit_sensitive_matches" }, metrics.Publications.ToArray());
            Assert.Equal(1, f.Telemetry.DashboardMetrics.Outcomes.Inspected);
            using var next = f.Budget.TryAcquire(4096); Assert.NotNull(next);
            Assert.Equal("{\"selected\":\"************1111\"}", f.Text);
        }
        finally { release.Set(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        AssertEvent(Assert.Single(f.Logs.Events), "inspected", 1, 1, 0);
    }

    [Theory]
    [InlineData("disabled", false)] [InlineData("enabled-error", false)] [InlineData("enabled-cancel", false)]
    [InlineData("log-error", false)] [InlineData("log-cancel", false)] [InlineData("normal", true)]
    [InlineData("log-error", true)] [InlineData("enabled-error", true)]
    public async Task Consumer_failures_do_not_change_http(string behavior, bool metricThrows)
    {
        using var metrics = new Metrics(metricThrows); using var f = new Fixture(Body, Policy()); f.Logs.Behavior = behavior;
        await f.Run();
        Assert.Equal(200, f.Context.HttpContext.Response.StatusCode); Assert.Equal("{\"selected\":\"************1111\"}", f.Text);
        Assert.Equal("no-store", f.Context.HttpContext.Response.Headers.CacheControl);
        Assert.Equal(1, metrics.Terminals); Assert.Equal(1, f.Telemetry.DashboardMetrics.Duration.Samples); Assert.Equal((0, 0L), f.Budget.Snapshot);
        Assert.Equal(2, f.Logs.EnabledCalls); Assert.Equal(behavior.StartsWith("log-") || behavior == "normal" ? 1 : 0, f.Logs.EventAttempts);
        if (behavior == "normal") AssertEvent(Assert.Single(f.Logs.Events), "inspected", 1, 1, 0); else Assert.Empty(f.Logs.Events);
        Assert.Contains(1, f.Logs.LegacyIds);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task Legacy_logger_error_semantics_are_unchanged(int id)
    {
        using var f = new Fixture(id == 2 ? "invalid" : Body, Policy()); f.Logs.ThrowLegacy = id;
        using var held = id == 3 ? f.Budget.TryAcquire(4096) : null;
        if (id == 1)
        {
            await f.Run(); Assert.Equal(502, f.Context.HttpContext.Response.StatusCode);
            AssertEvent(Assert.Single(f.Logs.Events), "blocked", 1, 1, 0);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(f.Run);
            AssertEvent(Assert.Single(f.Logs.Events), id == 2 ? "faulted" : "capacity_exceeded", 0, 0, 0);
        }
    }

    public static IEnumerable<object[]> InvalidIds()
    {
        foreach (var field in new[] { "route", "policy", "version" })
        foreach (var scenario in new[] { "null", "empty", "long", "newline", "trailing", "control", "unicode", "first", "space", "slash" }) yield return [field, scenario];
    }

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public async Task Invalid_identifiers_omit_only_event5(string field, string scenario)
    {
        string? invalid = scenario switch { "null" => null, "empty" => "", "long" => new string('a', 65), "newline" => "a\nb", "trailing" => "safe\n", "control" => "a\0b", "unicode" => "á", "first" => "_first", "space" => "a b", _ => "a/b" };
        using var metrics = new Metrics();
        using var f = new Fixture(Body, Policy() with { RouteId = field == "route" ? invalid! : "protected-payment", PolicyId = field == "policy" ? invalid! : "EventPolicy", Version = field == "version" ? invalid! : "v1" });
        await f.Run(); Assert.Empty(f.Logs.Events); Assert.Equal(0, f.Logs.EventAttempts);
        Assert.Empty(f.Logs.Rules);
        Assert.Equal(1, metrics.Terminals); Assert.Equal(200, f.Context.HttpContext.Response.StatusCode);
        Assert.Equal("{\"selected\":\"************1111\"}", f.Text); Assert.Contains(1, f.Logs.LegacyIds);
    }

    [Theory]
    [InlineData("one")] [InlineData("max")] [InlineData("grammar")] [InlineData("null")] [InlineData("empty")] [InlineData("url")] [InlineData("long")] [InlineData("unicode")] [InlineData("first")]
    public async Task Service_metadata_and_public_fallback(string scenario)
    {
        var value = scenario switch { "one" => "0", "max" => new string('Z', 64), "grammar" => "aZ09._-", "null" => null, "empty" => "", "url" => "http://address_marker/", "long" => new string('a', 65), "unicode" => "á", _ => "-first" };
        var valid = scenario is "one" or "max" or "grammar";
        var policy = valid ? Policy() with { RouteId = value!, PolicyId = value!, Version = value! } : Policy();
        using var f = new Fixture(Body, policy); await f.Transform.ApplyAsync(f.Context, policy, value);
        AssertEvent(Assert.Single(f.Logs.Events), "inspected", 1, 1, 0, valid ? value! : "unknown", policy);
        Assert.Equal(valid ? value! : "unknown", Assert.Single(f.Logs.Rules).State["ServiceId"]);
    }

    [Theory]
    [InlineData("head")] [InlineData("204")] [InlineData("304")] [InlineData("media")] [InlineData("null")] [InlineData("precancel")]
    public async Task Noninspection_paths_emit_no_event5(string scenario)
    {
        using var f = new Fixture(Body, Policy(), noUpstream: scenario == "null");
        if (scenario == "head") f.Context.HttpContext.Request.Method = "HEAD";
        if (scenario == "204") f.Context.ProxyResponse!.StatusCode = HttpStatusCode.NoContent;
        if (scenario == "304") f.Context.ProxyResponse!.StatusCode = HttpStatusCode.NotModified;
        if (scenario == "media") f.Context.ProxyResponse!.Content.Headers.ContentType = new("text/plain");
        if (scenario == "precancel") { f.Caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run); }
        else await f.Run();
        if (scenario == "304")
        {
            var terminal = Assert.Single(f.Logs.Events);
            Assert.Equal("blocked", terminal.State["Outcome"]);
            Assert.Equal(1, f.Logs.EventAttempts);
            Assert.Equal(502, f.Context.HttpContext.Response.StatusCode);
        }
        else { Assert.Empty(f.Logs.Events); Assert.Equal(0, f.Logs.EventAttempts); }
        Assert.Equal((0, 0L), f.Budget.Snapshot);
        Assert.Empty(f.Logs.Rules);
        Assert.Equal(0, f.Telemetry.DashboardMetrics.Duration.Samples);
        Assert.Equal(scenario is "head" or "204" or "media" ? 1 : 0, f.Logs.Exclusions);
    }

    [Fact]
    public async Task Capacity_denial_publishes_zero_event_after_metric_without_acquisition()
    {
        using var metrics = new Metrics(); using var f = new Fixture(Body, Policy()); using var held = f.Budget.TryAcquire(4096);
        f.Logs.OnEvent = () => { Assert.Equal(1, metrics.Terminals); Assert.Equal((1, 4096L), f.Budget.Snapshot); };
        await f.Run(); AssertEvent(Assert.Single(f.Logs.Events), "capacity_exceeded", 0, 0, 0);
        Assert.Equal(503, f.Context.HttpContext.Response.StatusCode); Assert.Equal("{\"code\":\"privacy_capacity_exceeded\"}", f.Text);
        Assert.Equal(0, f.Telemetry.DashboardMetrics.Duration.Samples); Assert.Contains(3, f.Logs.LegacyIds);
        Assert.Empty(f.Logs.Rules);
    }

    [Fact]
    public void Public_telemetry_calls_have_no_notifier()
    {
        using var f = new Fixture(Body, Policy());
        using (var m = f.Telemetry.TryStartInspection(f.Policy)) m!.Complete(PrivacyInspectionOutcome.Inspected);
        using (var m = f.Telemetry.TryStartInspection(f.Policy, TimeProvider.System)) m!.Dispose();
        using (var held = f.Budget.TryAcquire(4096)) Assert.Null(f.Telemetry.TryStartInspection(f.Policy));
        Assert.Empty(f.Logs.Events); Assert.Equal(0, f.Logs.EnabledCalls);
        Assert.Equal(2, f.Telemetry.DashboardMetrics.Duration.Samples);
        Assert.Single(typeof(PrivacyTelemetry).GetMethods(), m => m.IsPublic && m.Name == "TryStartInspection");
        Assert.Equal(2, typeof(PanResponseTransform).GetConstructors().Length);
    }

    [Fact]
    public async Task Sensitive_markers_are_absent()
    {
        using var f = new Fixture("{\"selector_marker\":\"body_marker\"}", Policy() with { Segments = ["selector_marker"] });
        using var activity = new Activity("activity_marker").Start(); activity.AddBaggage("baggage", "baggage_marker"); activity.AddTag("tag", "tag_marker");
        f.Context.HttpContext.Request.QueryString = new("?secret=query_marker");
        f.Context.HttpContext.Request.Headers["X-Correlation-Id"] = "incoming_marker";
        f.Context.HttpContext.Request.Headers["X-Secret"] = "header_marker";
        f.Context.HttpContext.TraceIdentifier = "trace_marker";
        f.Context.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("request_body_marker"));
        f.Context.ProxyResponse!.Headers.TryAddWithoutValidation("X-Secret", "upstream_marker");
        await f.Run(); var item = Assert.Single(f.Logs.Events); AssertEvent(item, "inspected", 1, 1, 0);
        var all = item.Rendered + string.Join("|", item.State.Select(x => x.Key + "=" + x.Value));
        foreach (var marker in new[] { "selector_marker", "body_marker", "activity_marker", "baggage_marker", "tag_marker", "query_marker", "incoming_marker", "header_marker", "trace_marker", "upstream_marker", activity.Id!, activity.TraceId.ToString() }) Assert.DoesNotContain(marker, all);
        Assert.False(f.Context.HttpContext.Response.Headers.ContainsKey("X-Correlation-Id"));
    }

    [Fact]
    public async Task Concurrent_invocations_do_not_mix_counts_or_ids()
    {
        using var telemetry = new PrivacyTelemetry(new(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 8 } })));
        var logs = new Logs(); var arrived = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transform = new PanResponseTransform(logs, telemetry, token => { if (Interlocked.Increment(ref arrived) == 2) release.TrySetResult(); return new(release.Task.WaitAsync(TimeSpan.FromSeconds(5), token)); });
        using var a = new Fixture("{\"selected\":[\"\"]}", Policy() with { PolicyId = "first", Segments = ["selected", "[*]"] });
        using var b = new Fixture("{\"selected\":[\"\",\"\",\"\"]}", Policy() with { PolicyId = "second", Segments = ["selected", "[*]"] });
        await Task.WhenAll(transform.ApplyAsync(a.Context, a.Policy, "service-first").AsTask(), transform.ApplyAsync(b.Context, b.Policy, "service-second").AsTask());
        Assert.Equal(2, logs.Events.Count);
        AssertEvent(Assert.Single(logs.Events, e => Equals(e.State["PolicyId"], "first")), "inspected", 1, 1, 0, "service-first", a.Policy);
        AssertEvent(Assert.Single(logs.Events, e => Equals(e.State["PolicyId"], "second")), "inspected", 3, 3, 0, "service-second", b.Policy);
        Assert.Equal(2, logs.Events.Select(e => e.State["CorrelationId"]).Distinct().Count());
        Assert.Equal(2, logs.Rules.Count);
        foreach (var rule in logs.Rules)
        {
            var terminal = Assert.Single(logs.Events, e => Equals(e.State["PolicyId"], rule.State["PolicyId"]));
            Assert.Equal(terminal.State["CorrelationId"], rule.State["CorrelationId"]);
            Assert.Equal(terminal.State["ValidatedExplicitSensitiveMatches"], rule.State["ValidatedMatches"]);
        }
    }

    [Theory]
    [InlineData("Mask")] [InlineData("Block")] [InlineData("Observe")]
    public async Task Real_yarp_host_service_local_correlations_and_no_propagation(string mode)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        const string body = "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}";
        api.MapGet("/terminal", async c => { c.Response.ContentType = "application/json"; await c.Response.WriteAsync(body); });
        await api.StartAsync(); var logs = new Logs(); var config = PolicyConfiguration.Defaults(); config["PanPolicies:Policies:0:Mode"] = mode;
        await using var gateway = GatewayHost.Build([], Address(api) + "/", b => { b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(config); b.WebHost.UseUrls("http://127.0.0.1:0"); b.Logging.ClearProviders(); b.Logging.AddProvider(logs); });
        await gateway.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(gateway)), Timeout = TimeSpan.FromSeconds(10) };
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/protected/terminal?secret=query_marker"); request.Headers.Add("X-Correlation-Id", "incoming_marker");
            using var response = await client.SendAsync(request); var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(mode == "Block" ? HttpStatusCode.BadGateway : HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(mode == "Observe" ? body : mode == "Block" ? "{\"code\":\"privacy_response_blocked\"}" : "{\"payment\":{\"cardNumber\":\"************1111\"}}", text);
            Assert.True(response.Headers.CacheControl!.NoStore); Assert.False(response.Headers.Contains("X-Correlation-Id"));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (logs.Events.Count < 2 || logs.Rules.Count < 2) await Task.Delay(10, timeout.Token);
        var policy = gateway.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        foreach (var item in logs.Events) { AssertEvent(item, mode == "Block" ? "policy_blocked" : "inspected", mode == "Mask" ? 1 : 0, 1, 0, "synthetic-api", policy); Assert.DoesNotContain("marker", item.Rendered); Assert.DoesNotContain(Address(api), item.Rendered); }
        Assert.Equal(2, logs.Events.Select(e => e.State["CorrelationId"]).Distinct().Count());
        Assert.Equal(2, logs.Rules.Count);
        foreach (var rule in logs.Rules)
        {
            var terminal = Assert.Single(logs.Events, e => Equals(e.State["CorrelationId"], rule.State["CorrelationId"]));
            Assert.Equal("primary", rule.State["RuleId"]); Assert.Equal("PAN", rule.State["Transformation"]);
            Assert.Equal(1L, rule.State["ValidatedMatches"]); Assert.Equal(mode == "Mask" ? 1L : 0L, rule.State["ValidatedMasks"]);
            Assert.Equal(terminal.State["Outcome"], rule.State["Outcome"]); Assert.Equal("synthetic-api", rule.State["ServiceId"]);
            Assert.DoesNotContain("marker", rule.Rendered); Assert.DoesNotContain(Address(api), rule.Rendered);
        }
        using var raw = await client.GetAsync("/passthrough/terminal"); Assert.Equal(body, await raw.Content.ReadAsStringAsync()); Assert.Equal(2, logs.Events.Count);
        Assert.Equal(2, logs.Rules.Count);
        Assert.Equal((0, 0L), gateway.Services.GetRequiredService<InspectionBudget>().Snapshot);
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static void AssertEvent(Event item, string outcome, long masks, long explicitMatches, long secondary, string service = "unknown", CompiledPanPolicy? policy = null)
    {
        policy ??= Policy();
        Assert.Equal(LogLevel.Information, item.Level); Assert.Equal(new EventId(5, "PrivacyInspectionCompleted"), item.Id); Assert.Null(item.Exception);
        Assert.Equal(new[] { "SchemaVersion", "RouteId", "ServiceId", "PolicyId", "PolicyVersion", "CorrelationId", "Outcome", "ValidatedMasks", "ValidatedExplicitSensitiveMatches", "ValidatedSecondaryPanMatches", "{OriginalFormat}" }.Order(), item.State.Keys.Order());
        Assert.Equal(1, Assert.IsType<int>(item.State["SchemaVersion"])); Assert.Equal(policy.RouteId, item.State["RouteId"]); Assert.Equal(service, item.State["ServiceId"]);
        Assert.Equal(policy.PolicyId, item.State["PolicyId"]); Assert.Equal(policy.Version, item.State["PolicyVersion"]); Assert.Equal(outcome, item.State["Outcome"]);
        Assert.Equal(masks, Assert.IsType<long>(item.State["ValidatedMasks"])); Assert.Equal(explicitMatches, Assert.IsType<long>(item.State["ValidatedExplicitSensitiveMatches"])); Assert.Equal(secondary, Assert.IsType<long>(item.State["ValidatedSecondaryPanMatches"]));
        Assert.Equal(Template, item.State["{OriginalFormat}"]); var correlation = Assert.IsType<string>(item.State["CorrelationId"]);
        Assert.Matches("\\A[0-9a-f]{32}\\z", correlation); Assert.NotEqual(Guid.Empty, Guid.ParseExact(correlation, "N"));
        Assert.Equal($"Privacy inspection completed; schema 1 route {policy.RouteId} service {service} policy {policy.PolicyId} version {policy.Version} correlation {correlation} outcome {outcome} validated masks {masks} validated explicit sensitive matches {explicitMatches} validated secondary PAN matches {secondary}.", item.Rendered);
    }

    internal sealed record Event(LogLevel Level, EventId Id, Dictionary<string, object?> State, Exception? Exception, string Rendered);
    internal sealed class Logs : ILogger<PanResponseTransform>, ILoggerProvider
    {
        public string Behavior = "normal"; public int EnabledCalls, EventAttempts, Exclusions; public int ThrowLegacy; public Action? OnEvent;
        public readonly ConcurrentQueue<Event> Events = new(); public readonly ConcurrentQueue<int> LegacyIds = new();
        public readonly ConcurrentQueue<Event> Rules = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel)
        {
            Interlocked.Increment(ref EnabledCalls);
            if (Behavior == "enabled-error") throw new InvalidOperationException("logger_marker");
            if (Behavior == "enabled-cancel") throw new OperationCanceledException("logger_marker");
            return Behavior != "disabled";
        }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (id.Id is >= 1 and <= 3) { LegacyIds.Enqueue(id.Id); if (ThrowLegacy == id.Id) throw new InvalidOperationException("legacy_marker"); }
            if (id.Id == 4) Interlocked.Increment(ref Exclusions);
            if (id.Id == 6)
            {
                if (Behavior is "log-error" or "log-cancel") throw new InvalidOperationException("logger_marker");
                Rules.Enqueue(new(level, id, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(x => x.Key, x => x.Value), exception, formatter(state, exception)));
            }
            if (id.Id != 5) return;
            Interlocked.Increment(ref EventAttempts);
            if (Behavior == "log-error") throw new InvalidOperationException("logger_marker");
            if (Behavior == "log-cancel") throw new OperationCanceledException("logger_marker");
            OnEvent?.Invoke();
            Events.Enqueue(new(level, id, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(x => x.Key, x => x.Value), exception, formatter(state, exception)));
        }
        public void Dispose() { }
    }
    internal sealed class Fixture : IDisposable
    {
        public readonly InspectionBudget Budget = PrivacyInspectionEventTests.Budget(); public readonly PrivacyTelemetry Telemetry;
        public readonly Logs Logs = new(); public readonly ManualClock Clock = new(); public readonly CancellationTokenSource Caller = new();
        public readonly ResponseTransformContext Context; public readonly CompiledPanPolicy Policy; public Func<CancellationToken, ValueTask>? Checkpoint;
        private readonly MemoryStream output = new();
        public Fixture(string body, CompiledPanPolicy policy, bool noUpstream = false)
        {
            Policy = policy; Telemetry = new(Budget); var http = new DefaultHttpContext(); http.Request.Method = "GET"; http.Response.Body = output;
            Context = new() { HttpContext = http, ProxyResponse = noUpstream ? null : new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, CancellationToken = Caller.Token };
        }
        public PanResponseTransform Transform => new(Logs, Telemetry, token => Checkpoint?.Invoke(token) ?? ValueTask.CompletedTask, Clock);
        public Task Run() => Transform.ApplyAsync(Context, Policy).AsTask();
        public string Text => Encoding.UTF8.GetString(output.ToArray());
        public void Dispose() { Context.ProxyResponse?.Dispose(); Telemetry.Dispose(); Caller.Dispose(); output.Dispose(); }
    }
    internal sealed class ManualClock : TimeProvider
    {
        private long ticks; public Action? OnTimestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { OnTimestamp?.Invoke(); return Interlocked.Read(ref ticks); }
        public void Advance() => Interlocked.Add(ref ticks, TimeSpan.TicksPerSecond);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class FailedWrite : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("write_marker"));
    }
    private sealed class Metrics : IDisposable
    {
        private readonly MeterListener listener = new(); public int Terminals; public readonly ConcurrentQueue<string> Publications = new();
        public Metrics(bool throws = false)
        {
            listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == PrivacyTelemetry.MeterName) l.EnableMeasurementEvents(i); };
            listener.SetMeasurementEventCallback<long>((i, v, tags, _) => { Publications.Enqueue(i.Name); if (i.Name == "gateway.privacy.inspection.outcomes") Interlocked.Increment(ref Terminals); if (throws) throw new OperationCanceledException("metric_marker"); });
            listener.SetMeasurementEventCallback<double>((i, v, tags, _) => { Publications.Enqueue(i.Name); if (throws) throw new InvalidOperationException("metric_marker"); }); listener.Start();
        }
        public void Dispose() => listener.Dispose();
    }
}
