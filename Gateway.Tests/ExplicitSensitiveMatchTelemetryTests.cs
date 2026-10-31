using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

[Collection("Secondary PAN telemetry isolation")]
public sealed class ExplicitSensitiveMatchTelemetryTests
{
    private const string Matches = "gateway.privacy.inspection.explicit_sensitive_matches";
    private const string Secondary = "gateway.privacy.inspection.secondary_pan_matches";
    private const string Outcomes = "gateway.privacy.inspection.outcomes";
    private const string Duration = "gateway.privacy.inspection.duration";
    private const string Input = "gateway.privacy.inspection.input_body_bytes";
    private const string Output = "gateway.privacy.inspection.output_body_bytes";
    private const string Masks = "gateway.privacy.inspection.masks";
    private const string Pan = "4111111111111111";
    private const string Body = "{\"explicit\":\"4111111111111111\"}";
    private const string Masked = "{\"explicit\":\"************1111\"}";
    private const string Blocked = "{\"code\":\"privacy_response_blocked\"}";
    private const string Error = "{\"code\":\"privacy_inspection_failed\"}";
    private static CompiledPanPolicy Policy(string mode = "Mask") => new("explicit-" + Guid.NewGuid().ToString("N"), "v1", "protected-payment", ["explicit"], 4096) { Mode = mode };

    [Theory]
    [InlineData("PAN", "Mask", false)] [InlineData("PAN", "Block", false)]
    [InlineData("Email", "Mask", false)] [InlineData("Email", "Block", false)]
    [InlineData("Phone", "Mask", false)] [InlineData("Phone", "Block", false)]
    [InlineData("PAN", "Mask", true)] [InlineData("PAN", "Block", true)]
    [InlineData("Email", "Mask", true)] [InlineData("Email", "Block", true)]
    [InlineData("Phone", "Mask", true)] [InlineData("Phone", "Block", true)]
    public async Task Declaration_counts_empty_incompatible_decoded_duplicates_and_arrays_for_primary_or_additional(string type, string mode, bool additional)
    {
        using var metrics = new Collector();
        var policy = Policy(mode) with { Transformation = type };
        if (additional) policy = policy with { Segments = ["absent"], AdditionalRules = [new(["explicit", "[*]"], type)] };
        else policy = policy with { Segments = ["explicit", "[*]"] };
        const string body = "{\"explicit\":[\"\",\"incompatible é\",null],\"explicit\":[\"\\u0034111111111111111\"]}";
        using var f = new Fixture(body, policy);
        await f.Run();
        metrics.Terminal(policy, mode == "Mask" ? "inspected" : "policy_blocked", 3, mode == "Mask" ? 3 : 0);
        Assert.Equal(mode == "Mask" ? 200 : 502, f.Context.HttpContext.Response.StatusCode);
        if (mode == "Block") Assert.Equal(Blocked, f.Text);
        else Assert.Equal(type == "PAN" ? "{\"explicit\":[\"[REDACTED]\",\"[REDACTED]\",null],\"explicit\":[\"************1111\"]}"
            : "{\"explicit\":[\"[REDACTED]\",\"[REDACTED]\",null],\"explicit\":[\"[REDACTED]\"]}", f.Text);
        f.Terminal();
    }

    [Theory]
    [InlineData("PAN", "Mask")] [InlineData("Email", "Mask")] [InlineData("Phone", "Mask")]
    [InlineData("PAN", "Block")] [InlineData("Email", "Block")] [InlineData("Phone", "Block")]
    public async Task Explicit_priority_partitions_secondary_matches_without_overlap(string type, string mode)
    {
        using var metrics = new Collector();
        using var f = new Fixture("{\"explicit\":\"4111111111111111\",\"a\":[\"4111111111111111\",\"\"],\"other\":\"4111111111111111\"}",
            Policy(mode) with { Transformation = type, AdditionalRules = [new(["a", "[*]"], type)], SecondaryPanDetection = true });
        await f.Run();
        metrics.Terminal(f.Policy, mode == "Mask" ? "inspected" : "policy_blocked", 3, mode == "Mask" ? 4 : 0, 1);
        f.Terminal();
    }

    [Theory]
    [InlineData("Email", "a@x.test", "***@x.test", false)]
    [InlineData("Phone", "123456789", "*****6789", false)]
    [InlineData("Email", "a@x.test", "***@x.test", true)]
    [InlineData("Phone", "123456789", "*****6789", true)]
    public async Task Compatible_declared_values_count_without_changing_existing_transformations(string type, string value, string masked, bool additional)
    {
        using var metrics = new Collector();
        var policy = Policy() with { Transformation = type };
        if (additional) policy = policy with { Segments = ["absent"], AdditionalRules = [new(["explicit"], type)] };
        using var f = new Fixture("{\"explicit\":\"" + value + "\"}", policy);
        await f.Run(); Assert.Equal("{\"explicit\":\"" + masked + "\"}", f.Text);
        metrics.Terminal(policy, "inspected", 1, 1); f.Terminal();
    }

    [Theory]
    [InlineData("Mask", "{\"explicit\":null}")] [InlineData("Block", "{\"explicit\":null}")]
    [InlineData("Mask", "{\"other\":\"4111111111111111\"}")] [InlineData("Block", "{\"other\":\"4111111111111111\"}")]
    [InlineData("Mask", "{\"explicit\":[]}")] [InlineData("Block", "{\"explicit\":{}}")]
    public async Task Null_absent_and_incompatible_intermediate_types_preserve_original_without_zero_sample(string mode, string body)
    {
        using var metrics = new Collector();
        using var f = new Fixture(body, Policy(mode) with { Segments = body.Contains("null") ? ["explicit"] : ["explicit", "[*]"] });
        await f.Run();
        Assert.Equal(body, f.Text);
        metrics.Terminal(f.Policy, "inspected", 0, 0); f.Terminal();
    }

    [Theory]
    [InlineData("Mask", "42", false)] [InlineData("Block", "true", false)]
    [InlineData("Mask", "{}", true)] [InlineData("Block", "[]", true)]
    [InlineData("Mask", "INVALID", false)] [InlineData("Block", "INVALID", true)]
    public async Task Late_nonstring_or_JSON_failure_discards_provisional_counts_and_fails_closed(string mode, string tail, bool multiple)
    {
        using var metrics = new Collector();
        using var f = new Fixture("{\"explicit\":\"\",\"explicit\":" + tail + "}", Policy(mode) with { AdditionalRules = multiple ? [new(["other"], "Email")] : [] });
        await f.Run();
        Assert.Equal(Error, f.Text); Assert.Equal(502, f.Context.HttpContext.Response.StatusCode);
        metrics.Terminal(f.Policy, "blocked", 0, 0); f.Terminal();
    }

    // Reuses MVP28 StackTrace boundary observations: JIT/runtime sensitive, not timer delivery proof.
    [Theory]
    [InlineData("Mask", false, false, false)] [InlineData("Block", true, false, false)]
    [InlineData("Mask", true, true, false)] [InlineData("Block", false, true, false)]
    [InlineData("Mask", false, false, true)] [InlineData("Block", true, false, true)]
    [InlineData("Mask", true, true, true)] [InlineData("Block", false, true, true)]
    public async Task Traversal_and_post_writer_deadline_or_cancel_discard_before_commit(string mode, bool cancel, bool afterWriter, bool multiple)
    {
        using var metrics = new Collector();
        using var f = new Fixture(Body, Policy(mode) with { AdditionalRules = multiple ? [new(["other"], "Phone")] : [] });
        var traversed = false; var checks = 0; var triggered = false;
        f.Clock.OnTimestamp = () =>
        {
            var inside = new System.Diagnostics.StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == (multiple ? "WriteMultiple" : "WriteSelected"));
            if (inside) { traversed = true; checks++; }
            if (triggered || (afterWriter ? !traversed || inside : !inside || checks != 4)) return;
            triggered = true; f.Clock.OnTimestamp = null;
            if (cancel) f.Caller.Cancel(); else f.Clock.Advance();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run);
        Assert.True(triggered); Assert.Empty(f.Text); Assert.Null(f.Context.HttpContext.Response.ContentLength);
        metrics.Terminal(f.Policy, cancel ? "canceled" : "timed_out", 0, 0); f.Terminal();
    }

    [Theory]
    [InlineData("Mask", "checkpoint")] [InlineData("Block", "checkpoint")]
    [InlineData("Mask", "headers")] [InlineData("Block", "headers")]
    [InlineData("Mask", "cancel")] [InlineData("Block", "cancel")]
    [InlineData("Mask", "deadline")] [InlineData("Block", "deadline")]
    public async Task Post_commit_checkpoint_header_cancel_and_deadline_retain_final_outcome_counts(string mode, string failure)
    {
        using var metrics = new Collector(); using var f = new Fixture(Body, Policy(mode));
        f.Checkpoint = _ =>
        {
            Assert.Empty(metrics.For(f.Policy, Matches)); Assert.Equal((1, 4096L), f.Budget.Snapshot);
            if (failure == "checkpoint") throw new InvalidOperationException("Synthetic checkpoint failure.");
            if (failure == "headers") ((HeaderDictionary)f.Context.HttpContext.Response.Headers).IsReadOnly = true;
            if (failure == "cancel") f.Caller.Cancel();
            if (failure == "deadline") f.Clock.Advance();
            return ValueTask.CompletedTask;
        };
        if (failure is "cancel" or "deadline") await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run);
        else await Assert.ThrowsAsync<InvalidOperationException>(f.Run);
        metrics.Terminal(f.Policy, failure == "cancel" ? "canceled" : failure == "deadline" ? "timed_out" : "faulted", 1, mode == "Mask" ? 1 : 0);
        Assert.Empty(f.Text); Assert.Empty(metrics.For(f.Policy, Output)); f.Terminal();
    }

    [Theory]
    [InlineData("Mask", "io")] [InlineData("Block", "io")]
    [InlineData("Mask", "cancel")] [InlineData("Block", "cancel")]
    [InlineData("Mask", "deadline")] [InlineData("Block", "deadline")]
    public async Task Pending_write_retains_lease_delays_publication_and_preserves_counts_after_failure(string mode, string failure)
    {
        using var metrics = new Collector(); using var gate = new WriteGate(); using var f = new Fixture(Body, Policy(mode), gate);
        metrics.OnSample = s => { if (s.Instrument.Name == Matches) throw new OperationCanceledException(); };
        var pending = f.Run();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(metrics.For(f.Policy, Matches)); Assert.Empty(metrics.For(f.Policy, Outcomes));
            Assert.Equal((1, 4096L), f.Budget.Snapshot); Assert.Equal(mode == "Mask" ? Masked : Blocked, gate.Attempted);
            var original = new IOException("Synthetic write failure.");
            if (failure == "io") gate.Release.TrySetException(original);
            if (failure == "cancel") f.Caller.Cancel();
            if (failure == "deadline") { f.Clock.Advance(); gate.Release.TrySetResult(); }
            if (failure == "io") Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            metrics.Terminal(f.Policy, failure == "io" ? "write_error" : failure == "cancel" ? "canceled" : "timed_out", 1, mode == "Mask" ? 1 : 0);
            Assert.Single(metrics.For(f.Policy, Output)); f.Terminal();
        }
        finally { gate.Release.TrySetResult(); try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is IOException or OperationCanceledException) { } }
    }

    [Theory]
    [InlineData("read-io")] [InlineData("encoding")] [InlineData("utf8")]
    public async Task Read_preflight_and_invalid_UTF8_failures_have_no_committed_matches(string failure)
    {
        using var metrics = new Collector(); using var f = new Fixture(Body, Policy());
        if (failure != "encoding")
        {
            f.Context.ProxyResponse!.Content.Dispose();
            f.Context.ProxyResponse.Content = failure == "read-io" ? new StreamContent(new FailingRead()) : new ByteArrayContent([0xff]);
        }
        f.Context.ProxyResponse!.Content.Headers.ContentType = new("application/json");
        if (failure == "encoding") f.Context.ProxyResponse.Content.Headers.ContentEncoding.Add("gzip");
        await f.Run(); Assert.Equal(Error, f.Text); metrics.Terminal(f.Policy, "blocked", 0, 0); f.Terminal();
    }

    [Theory]
    [InlineData("Mask", false)] [InlineData("Block", false)]
    [InlineData("Mask", true)] [InlineData("Block", true)]
    public async Task Pending_read_cancel_or_deadline_retains_lease_until_read_returns_and_never_commits(string mode, bool cancel)
    {
        using var metrics = new Collector(); using var gate = new ReadGate(); using var f = new Fixture(Body, Policy(mode));
        f.Context.ProxyResponse!.Content.Dispose(); f.Context.ProxyResponse.Content = new StreamContent(gate);
        f.Context.ProxyResponse.Content.Headers.ContentType = new("application/json");
        var pending = f.Run();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(metrics.For(f.Policy, Outcomes)); Assert.Empty(metrics.For(f.Policy, Matches)); Assert.Equal((1, 4096L), f.Budget.Snapshot);
            if (cancel) f.Caller.Cancel(); else f.Clock.Advance();
            // Deliberately noncooperative synthetic read: no early release while I/O owns the lease.
            Assert.False(pending.IsCompleted); Assert.Equal((1, 4096L), f.Budget.Snapshot);
            gate.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            metrics.Terminal(f.Policy, cancel ? "canceled" : "timed_out", 0, 0);
            Assert.Empty(metrics.For(f.Policy, Input)); Assert.Empty(metrics.For(f.Policy, Output)); Assert.Empty(f.Text); f.Terminal();
        }
        finally { gate.Release.TrySetResult(); try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData("head")] [InlineData("204")] [InlineData("304")] [InlineData("media")]
    [InlineData("upstream-null")] [InlineData("pre-cancel")] [InlineData("capacity")]
    public async Task Noneligible_precancel_and_capacity_have_no_explicit_sample(string scenario)
    {
        using var metrics = new Collector(); using var f = new Fixture(Body, Policy(), noUpstream: scenario == "upstream-null");
        if (scenario == "head") f.Context.HttpContext.Request.Method = "HEAD";
        if (scenario == "204") f.Context.ProxyResponse!.StatusCode = HttpStatusCode.NoContent;
        if (scenario == "304") f.Context.ProxyResponse!.StatusCode = HttpStatusCode.NotModified;
        if (scenario == "media") f.Context.ProxyResponse!.Content.Headers.ContentType = new("text/plain");
        using var held = scenario == "capacity" ? f.Budget.TryAcquire(4096) : null;
        if (scenario == "pre-cancel") { f.Caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run); }
        else await f.Run();
        foreach (var name in new[] { Matches, Masks, Secondary, Duration }) Assert.Empty(metrics.For(f.Policy, name));
        if (scenario == "capacity")
        {
            Assert.Equal(503, f.Context.HttpContext.Response.StatusCode);
            Assert.Equal("capacity_exceeded", Assert.Single(metrics.For(f.Policy, Outcomes)).Tags["gateway.privacy.outcome"]);
            Assert.Equal((1, 4096L), f.Budget.Snapshot);
        }
        else
        {
            if (scenario is "head" or "204" or "304" or "media")
            {
                var outcome = Assert.Single(metrics.For(f.Policy, Outcomes));
                Assert.Equal(1L, Assert.IsType<long>(outcome.Value));
                Assert.Equal(scenario == "304" ? "blocked" : scenario == "head" ? "excluded_head" : scenario == "media" ? "excluded_media_type" : "excluded_bodyless",
                    outcome.Tags["gateway.privacy.outcome"]);
            }
            else Assert.Empty(metrics.For(f.Policy, Outcomes));
            Assert.Equal((0, 0L), f.Budget.Snapshot);
            if (scenario == "304") Assert.Equal(502, f.Context.HttpContext.Response.StatusCode);
        }
    }

    [Theory]
    [InlineData(Outcomes)] [InlineData(Duration)] [InlineData(Input)] [InlineData(Output)]
    [InlineData(Masks)] [InlineData(Secondary)] [InlineData(Matches)]
    public async Task Each_instrument_ordinary_exception_is_independent_of_enforcement_and_other_publications(string failingInstrument)
    {
        using var metrics = new Collector();
        foreach (var mode in new[] { "Mask", "Block" })
        {
            using var f = new Fixture("{\"explicit\":\"4111111111111111\",\"other\":\"4111111111111111\"}", Policy(mode) with { SecondaryPanDetection = true });
            metrics.OnSample = s => { if (s.Instrument.Name == failingInstrument && s.Tags["gateway.policy.id"] == f.Policy.PolicyId) throw new InvalidOperationException("Synthetic listener failure."); };
            await f.Run(); metrics.Terminal(f.Policy, mode == "Mask" ? "inspected" : "policy_blocked", 1, mode == "Mask" ? 2 : 0, 1);
            Assert.Single(metrics.For(f.Policy, Input)); Assert.Single(metrics.For(f.Policy, Output));
            Assert.Equal(mode == "Mask" ? "{\"explicit\":\"************1111\",\"other\":\"************1111\"}" : Blocked, f.Text);
            Assert.Equal(mode == "Mask" ? 200 : 502, f.Context.HttpContext.Response.StatusCode); f.Terminal();
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Concurrent_repeated_completion_publishes_exact_long_once_even_with_throwing_listener(bool throws)
    {
        using var metrics = new Collector(); using var f = new Fixture(Body, Policy());
        using var measurement = f.Telemetry.TryStartInspection(f.Policy)!;
        measurement.ValidatedExplicitSensitiveMatches = 9007199254740993L;
        measurement.ValidatedSecondaryPanMatches = 7; measurement.ValidatedMasks = 2;
        if (throws) metrics.OnSample = s => { if (s.Instrument.Name == Matches) throw new OperationCanceledException(); };
        Parallel.For(0, 32, _ => measurement.Complete(PrivacyInspectionOutcome.Inspected));
        measurement.Dispose(); measurement.Complete(PrivacyInspectionOutcome.Canceled);
        metrics.Terminal(f.Policy, "inspected", 9007199254740993L, 2, 7); Assert.Equal((0, 0L), f.Budget.Snapshot);
        using var next = f.Telemetry.TryStartInspection(f.Policy); Assert.NotNull(next);
        measurement.Dispose(); Assert.Equal((1, 4096L), f.Budget.Snapshot);
    }

    [Theory]
    [InlineData("Mask")] [InlineData("Block")]
    public async Task Blocking_explicit_listener_retains_lease_and_denies_capacity_until_callback_returns(string mode)
    {
        using var metrics = new Collector(); using var f = new Fixture(Body, Policy(mode));
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        metrics.OnSample = s =>
        {
            if (s.Instrument.Name != Matches || s.Tags["gateway.policy.id"] != f.Policy.PolicyId) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic gate not released.");
        };
        var pending = Task.Run(f.Run);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
            Assert.Equal((1, 4096L), f.Budget.Snapshot); Assert.Equal(mode == "Mask" ? Masked : Blocked, f.Text);
            using var output = new MemoryStream(); var http = new DefaultHttpContext(); http.Response.Body = output;
            using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
            await new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, f.Telemetry).ApplyAsync(new() { HttpContext = http, ProxyResponse = upstream }, Policy(mode));
            Assert.Equal(503, http.Response.StatusCode); Assert.Equal("{\"code\":\"privacy_capacity_exceeded\"}", Encoding.UTF8.GetString(output.ToArray()));
            Assert.Equal((1, 4096L), f.Budget.Snapshot);
        }
        finally { release.Set(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        metrics.Terminal(f.Policy, mode == "Mask" ? "inspected" : "policy_blocked", 1, mode == "Mask" ? 1 : 0); f.Terminal();
    }

    [Fact]
    public async Task Concurrent_instances_with_identical_tags_keep_explicit_counts_and_meters_separate()
    {
        using var metrics = new Collector(); var policy = Policy() with { Segments = ["explicit", "[*]"] };
        using var first = new Fixture("{\"explicit\":[\"\"]}", policy);
        using var second = new Fixture("{\"explicit\":[\"\",\"\",\"\"]}", policy);
        var arrived = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Checkpoint(CancellationToken token) { if (Interlocked.Increment(ref arrived) == 2) release.TrySetResult(); return new(release.Task.WaitAsync(TimeSpan.FromSeconds(5), token)); }
        first.Checkpoint = Checkpoint; second.Checkpoint = Checkpoint;
        await Task.WhenAll(first.Run(), second.Run());
        var samples = metrics.For(policy, Matches); Assert.Equal(2, samples.Length);
        Assert.Equal(new long[] { 1, 3 }, samples.Select(s => Assert.IsType<long>(s.Value)).Order());
        Assert.NotSame(samples[0].Instrument.Meter, samples[1].Instrument.Meter);
        foreach (var s in samples) Assert.Equal(Assert.Single(metrics.For(policy, Outcomes), o => ReferenceEquals(o.Instrument.Meter, s.Instrument.Meter)).Tags.OrderBy(t => t.Key), s.Tags.OrderBy(t => t.Key));
        Assert.Equal(1, first.Telemetry.DashboardMetrics.ValidatedMasks); Assert.Equal(3, second.Telemetry.DashboardMetrics.ValidatedMasks);
        first.Terminal(); second.Terminal();
    }

    [Theory]
    [InlineData("Mask")] [InlineData("Block")]
    public async Task Real_HTTP_explicit_counts_publish_after_duration_and_zero_budget_terminal_barrier(string mode)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        const string body = "{\"payment\":{\"cardNumber\":\"4111111111111111\"},\"email\":\"a@x.test\",\"phone\":\"+1-202-555-0199\"}";
        const string masked = "{\"payment\":{\"cardNumber\":\"************1111\"},\"email\":\"***@x.test\",\"phone\":\"\\u002B*-***-***-0199\"}";
        api.MapGet("/test", async c => { c.Response.ContentType = "application/json"; c.Response.Headers.ETag = "\"synthetic\""; await c.Response.WriteAsync(body, c.RequestAborted); });
        await api.StartAsync();
        var values = PolicyConfiguration.Defaults(); values["PanPolicies:Policies:0:Mode"] = mode;
        values["PanPolicies:Policies:0:PolicyId"] = "http-explicit-" + Guid.NewGuid().ToString("N");
        values["PanPolicies:Policies:0:AdditionalRules:0:Selector"] = "$.email"; values["PanPolicies:Policies:0:AdditionalRules:0:Transformation"] = "Email";
        values["PanPolicies:Policies:0:AdditionalRules:1:Selector"] = "$.phone"; values["PanPolicies:Policies:0:AdditionalRules:1:Transformation"] = "Phone";
        await using var app = GatewayHost.Build([], Address(api) + "/", b => { b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(values); b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0"); });
        var policy = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        var telemetry = app.Services.GetRequiredService<PrivacyTelemetry>(); var budget = app.Services.GetRequiredService<InspectionBudget>();
        using var metrics = new Collector(); await app.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(app)), Timeout = TimeSpan.FromSeconds(10) };
        for (var n = 1; n <= 2; n++)
        {
            using var response = await client.GetAsync("/protected/test"); var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(mode == "Mask" ? HttpStatusCode.OK : HttpStatusCode.BadGateway, response.StatusCode); Assert.Equal(mode == "Mask" ? masked : Blocked, text);
            Assert.True(response.Headers.CacheControl!.NoStore); Assert.False(response.Headers.Contains("ETag"));
            Assert.Equal(Encoding.UTF8.GetByteCount(text), response.Content.Headers.ContentLength); Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (telemetry.DashboardMetrics.Duration.Samples < n || budget.Snapshot != (0, 0L)) await Task.Delay(10, timeout.Token);
            Assert.Equal(n, metrics.For(policy, Matches).Length);
            foreach (var sample in metrics.For(policy, Matches)) Collector.AssertMatch(sample, policy, mode == "Mask" ? "inspected" : "policy_blocked", 3);
            Assert.Equal(n, metrics.For(policy, Outcomes).Length); Assert.Empty(metrics.For(policy, Secondary));
            Assert.Equal(mode == "Mask" ? n * 3 : 0, telemetry.DashboardMetrics.ValidatedMasks);
        }
        using var raw = await client.GetAsync("/passthrough/test"); Assert.Equal(body, await raw.Content.ReadAsStringAsync()); Assert.NotNull(raw.Headers.ETag);
        Assert.Equal(2, metrics.For(policy, Matches).Length);
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private sealed class Fixture : IDisposable
    {
        public CompiledPanPolicy Policy { get; }
        public readonly CancellationTokenSource Caller = new(); public readonly ManualClock Clock = new();
        public readonly InspectionBudget Budget = new(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = 4096, InspectionTimeoutSeconds = 1 } }));
        public readonly PrivacyTelemetry Telemetry; public readonly ResponseTransformContext Context;
        private readonly MemoryStream output = new(); public Func<CancellationToken, ValueTask>? Checkpoint;
        public Fixture(string body, CompiledPanPolicy policy, Stream? downstream = null, bool noUpstream = false)
        {
            Policy = policy; Telemetry = new(Budget); var http = new DefaultHttpContext(); http.Request.Method = "GET"; http.Response.Body = downstream ?? output;
            Context = new() { HttpContext = http, ProxyResponse = noUpstream ? null : new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, HeadersCopied = true, CancellationToken = Caller.Token };
        }
        public string Text => Encoding.UTF8.GetString(output.ToArray());
        public Task Run() => new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, Telemetry, Checkpoint, Clock).ApplyAsync(Context, Policy).AsTask();
        public void Terminal() { Assert.Equal(1, Telemetry.DashboardMetrics.Duration.Samples); Assert.Equal((0, 0L), Budget.Snapshot); }
        public void Dispose() { Context.ProxyResponse?.Dispose(); Telemetry.Dispose(); Caller.Dispose(); output.Dispose(); }
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks; public Action? OnTimestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { OnTimestamp?.Invoke(); return Interlocked.Read(ref ticks); }
        public void Advance() => Interlocked.Add(ref ticks, TimeSpan.TicksPerSecond);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class WriteGate : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Attempted;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Attempted = Encoding.UTF8.GetString(buffer.Span); Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); }
    }
    private sealed class FailingRead : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("Synthetic read failure."));
    }
    private sealed class ReadGate : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Entered.TrySetResult(); await Release.Task; return 0; }
    }
    private sealed record Sample(Instrument Instrument, object Value, Dictionary<string, string?> Tags);
    private sealed class Collector : IDisposable
    {
        public readonly ConcurrentQueue<Sample> Samples = new(); public Action<Sample>? OnSample; private readonly MeterListener listener = new();
        public Collector()
        {
            listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == PrivacyTelemetry.MeterName) l.EnableMeasurementEvents(i); };
            listener.SetMeasurementEventCallback<long>((i, v, t, _) => Capture(i, v, t)); listener.SetMeasurementEventCallback<double>((i, v, t, _) => Capture(i, v, t)); listener.Start();
        }
        private void Capture(Instrument i, object v, ReadOnlySpan<KeyValuePair<string, object?>> tags) { var sample = new Sample(i, v, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString())); Samples.Enqueue(sample); OnSample?.Invoke(sample); }
        public Sample[] For(CompiledPanPolicy p, string name) => Samples.Where(s => s.Instrument.Name == name && s.Tags.GetValueOrDefault("gateway.policy.id") == p.PolicyId).ToArray();
        public void Terminal(CompiledPanPolicy p, string outcome, long matches, long masks, long secondary = 0)
        {
            var terminal = Assert.Single(For(p, Outcomes)); Assert.Equal(1L, Assert.IsType<long>(terminal.Value)); Assert.Equal(outcome, terminal.Tags["gateway.privacy.outcome"]); Assert.Single(For(p, Duration));
            foreach (var (name, count) in new[] { (Matches, matches), (Masks, masks), (Secondary, secondary) })
            {
                if (count == 0) Assert.Empty(For(p, name));
                else { var sample = Assert.Single(For(p, name)); Assert.Equal(count, Assert.IsType<long>(sample.Value)); Assert.Same(terminal.Instrument.Meter, sample.Instrument.Meter); if (name == Matches) AssertMatch(sample, p, outcome, count); }
            }
            foreach (var sample in Samples.Where(s => s.Tags.GetValueOrDefault("gateway.policy.id") == p.PolicyId))
            {
                Assert.Equal(terminal.Tags.OrderBy(t => t.Key), sample.Tags.OrderBy(t => t.Key));
                var tags = string.Join('\n', sample.Tags.Select(t => t.Value));
                foreach (var sensitive in new[] { Pan, "************1111", "incompatible é", "$.explicit", "a@x.test", "+1-202-555-0199" }) Assert.DoesNotContain(sensitive, tags);
            }
        }
        public static void AssertMatch(Sample s, CompiledPanPolicy p, string outcome, long count)
        {
            Assert.Equal(count, Assert.IsType<long>(s.Value)); Assert.IsType<Counter<long>>(s.Instrument); Assert.Equal(Matches, s.Instrument.Name); Assert.Equal("{match}", s.Instrument.Unit);
            Assert.Equal("Gateway.Privacy", s.Instrument.Meter.Name); Assert.Equal("1.0.0", s.Instrument.Meter.Version);
            Assert.Equal(new Dictionary<string, string?> { ["gateway.route.id"] = p.RouteId, ["gateway.policy.id"] = p.PolicyId, ["gateway.policy.version"] = p.Version, ["gateway.privacy.outcome"] = outcome }.OrderBy(t => t.Key), s.Tags.OrderBy(t => t.Key));
        }
        public void Dispose() => listener.Dispose();
    }
}
