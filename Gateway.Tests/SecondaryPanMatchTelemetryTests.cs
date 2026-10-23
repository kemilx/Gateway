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
public sealed class SecondaryPanMatchTelemetryTests
{
    private const string Matches = "gateway.privacy.inspection.secondary_pan_matches";
    private const string Outcomes = "gateway.privacy.inspection.outcomes";
    private const string Duration = "gateway.privacy.inspection.duration";
    private const string Input = "gateway.privacy.inspection.input_body_bytes";
    private const string Output = "gateway.privacy.inspection.output_body_bytes";
    private const string Masks = "gateway.privacy.inspection.masks";
    private const string Pan = "4111111111111111";
    private const string Mask = "************1111";
    private const string Body = "{\"other\":\"4111111111111111\"}";
    private const string BlockBody = "{\"code\":\"privacy_response_blocked\"}";
    private const string ErrorBody = "{\"code\":\"privacy_inspection_failed\"}";
    private static CompiledPanPolicy Policy(string mode = "Mask") => new("match-" + Guid.NewGuid().ToString("N"), "v1", "protected-payment", ["explicit"], 4096)
        { SecondaryPanDetection = true, Mode = mode };

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    public async Task Secondary_occurrences_count_arrays_duplicates_and_decoded_values_with_exact_private_contract(string mode)
    {
        using var metrics = new Collector();
        const string body = "{\"a\":[\"4111111111111111\",[\"4222222222222\"]],\"a\":\"\\u0034111111111111111\"}";
        using var f = new Fixture(body, Policy(mode));
        await f.Run();
        metrics.AssertTerminal(f.Policy, mode == "Mask" ? "inspected" : "policy_blocked", 3, mode == "Mask" ? 3 : 0);
        Assert.Equal(mode == "Mask" ? 200 : 502, f.Context.HttpContext.Response.StatusCode);
        Assert.Equal(mode == "Block" ? BlockBody : "{\"a\":[\"************1111\",[\"*********2222\"]],\"a\":\"************1111\"}", f.Text);
        Assert.DoesNotContain(Pan, string.Join('\n', metrics.Samples.SelectMany(s => s.Tags).Select(t => t.Value)));
        Assert.DoesNotContain(Mask, string.Join('\n', metrics.Samples.SelectMany(s => s.Tags).Select(t => t.Value)));
        f.Terminal();
    }

    [Theory]
    [InlineData("PAN", "Mask")]
    [InlineData("Email", "Mask")]
    [InlineData("Phone", "Mask")]
    [InlineData("PAN", "Block")]
    [InlineData("Email", "Block")]
    [InlineData("Phone", "Block")]
    public async Task Explicit_primary_and_additional_rules_are_excluded_even_for_Luhn_positive_strings(string type, string mode)
    {
        using var metrics = new Collector();
        var policy = Policy(mode) with { Transformation = type, AdditionalRules = [new(["a", "[*]"], type)] };
        using var f = new Fixture("{\"explicit\":\"4111111111111111\",\"a\":[\"4111111111111111\",\"4111111111111111\"],\"other\":\"4111111111111111\"}", policy);
        await f.Run();
        metrics.AssertTerminal(policy, mode == "Mask" ? "inspected" : "policy_blocked", 1, mode == "Mask" ? 4 : 0, 3);
        f.Terminal();
    }

    [Theory]
    [InlineData("no-match", "Mask")]
    [InlineData("no-match", "Block")]
    [InlineData("optout", "Mask")]
    [InlineData("optout", "Block")]
    [InlineData("explicit-only", "Mask")]
    [InlineData("explicit-only", "Block")]
    public async Task No_match_optout_names_nonstrings_and_explicit_only_never_publish_zero(string scenario, string mode)
    {
        using var metrics = new Collector();
        var body = scenario == "no-match" ? " {\"4111111111111111\":null,\"x\":[4111111111111111,true,null,\"4111111111111112\",\"PAN 4111111111111111\"]} \r\n"
            : scenario == "explicit-only" ? "{\"explicit\":\"4111111111111111\"}" : Body;
        using var f = new Fixture(body, Policy(mode) with { SecondaryPanDetection = scenario != "optout" });
        await f.Run();
        metrics.AssertTerminal(f.Policy, scenario == "explicit-only" && mode == "Block" ? "policy_blocked" : "inspected", 0,
            scenario == "explicit-only" && mode == "Mask" ? 1 : 0, scenario == "explicit-only" ? 1 : 0);
        if (scenario != "explicit-only") Assert.Equal(body, f.Text);
        f.Terminal();
    }

    [Theory]
    [InlineData("Mask", "42")]
    [InlineData("Block", "42")]
    [InlineData("Mask", "true")]
    [InlineData("Block", "{}")]
    [InlineData("Mask", "[]")]
    [InlineData("Block", "INVALID")]
    public async Task Late_terminal_type_or_JSON_failure_discards_all_provisional_matches(string mode, string tail)
    {
        using var metrics = new Collector();
        using var f = new Fixture("{\"other\":\"4111111111111111\",\"explicit\":" + tail + "}", Policy(mode));
        await f.Run();
        Assert.Equal(ErrorBody, f.Text);
        metrics.AssertTerminal(f.Policy, "blocked", 0, 0);
        f.Terminal();
    }

    // Historical fixture technique: StackTrace/checkpoint observations depend on JIT shape.
    // This proves monotonic checks at the observed boundary, NOT real timer delivery.
    [Theory]
    [InlineData("Mask", false, false)]
    [InlineData("Block", false, false)]
    [InlineData("Mask", true, false)]
    [InlineData("Block", true, false)]
    [InlineData("Mask", false, true)]
    [InlineData("Block", false, true)]
    [InlineData("Mask", true, true)]
    [InlineData("Block", true, true)]
    public async Task Deadline_or_cancel_during_traversal_or_post_writer_check_discards_matches(string mode, bool cancel, bool afterWriter)
    {
        using var metrics = new Collector();
        using var f = new Fixture("[\"4111111111111111\",\"tail\"]", Policy(mode));
        var traversing = false;
        var checks = 0;
        var triggered = false;
        f.Clock.OnTimestamp = () =>
        {
            var inTraversal = new System.Diagnostics.StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == "WriteMultiple");
            if (inTraversal) { traversing = true; checks++; }
            if (triggered || (afterWriter ? !traversing || inTraversal : !inTraversal || checks != 5)) return;
            triggered = true;
            f.Clock.OnTimestamp = null;
            if (cancel) f.Caller.Cancel(); else f.Clock.Advance();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run);
        Assert.True(triggered);
        Assert.Empty(f.Text);
        Assert.Null(f.Context.HttpContext.Response.ContentLength);
        metrics.AssertTerminal(f.Policy, cancel ? "canceled" : "timed_out", 0, 0);
        f.Terminal();
    }

    [Theory]
    [InlineData("Mask", "checkpoint")]
    [InlineData("Block", "checkpoint")]
    [InlineData("Mask", "headers")]
    [InlineData("Block", "headers")]
    [InlineData("Mask", "cancel")]
    [InlineData("Block", "cancel")]
    [InlineData("Mask", "deadline")]
    [InlineData("Block", "deadline")]
    public async Task Post_commit_checkpoint_header_cancel_or_deadline_retains_matches_with_final_outcome(string mode, string failure)
    {
        using var metrics = new Collector();
        using var f = new Fixture(Body, Policy(mode));
        f.Checkpoint = _ =>
        {
            Assert.Empty(metrics.For(f.Policy, Matches));
            Assert.Equal((1, 4096L), f.Budget.Snapshot);
            if (failure == "checkpoint") throw new InvalidOperationException("Synthetic checkpoint failure.");
            if (failure == "headers") ((HeaderDictionary)f.Context.HttpContext.Response.Headers).IsReadOnly = true;
            if (failure == "cancel") f.Caller.Cancel();
            if (failure == "deadline") f.Clock.Advance();
            return ValueTask.CompletedTask;
        };
        if (failure is "cancel" or "deadline") await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run);
        else await Assert.ThrowsAsync<InvalidOperationException>(f.Run);
        metrics.AssertTerminal(f.Policy, failure == "cancel" ? "canceled" : failure == "deadline" ? "timed_out" : "faulted", 1, mode == "Mask" ? 1 : 0);
        Assert.Empty(f.Text);
        Assert.Empty(metrics.For(f.Policy, Output));
        f.Terminal();
    }

    [Theory]
    [InlineData("Mask", "io")]
    [InlineData("Block", "io")]
    [InlineData("Mask", "cancel")]
    [InlineData("Block", "cancel")]
    [InlineData("Mask", "deadline")]
    [InlineData("Block", "deadline")]
    public async Task Pending_write_delays_publication_and_late_IO_cancel_or_deadline_retains_matches_even_with_throwing_listener(string mode, string failure)
    {
        using var metrics = new Collector();
        using var gate = new WriteGate();
        using var f = new Fixture(Body, Policy(mode), gate);
        metrics.OnSample = sample => { if (sample.Instrument.Name == Matches) throw new OperationCanceledException(); };
        var pending = f.Run();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(metrics.For(f.Policy, Matches));
        Assert.Empty(metrics.For(f.Policy, Outcomes));
        Assert.Equal((1, 4096L), f.Budget.Snapshot);
        Assert.Equal(mode == "Mask" ? Body.Replace(Pan, Mask) : BlockBody, gate.Attempted);
        var original = new IOException("Synthetic write failure.");
        if (failure == "io") gate.Release.TrySetException(original);
        if (failure == "cancel") f.Caller.Cancel();
        if (failure == "deadline") { f.Clock.Advance(); gate.Release.TrySetResult(); }
        if (failure == "io") Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        metrics.AssertTerminal(f.Policy, failure == "io" ? "write_error" : failure == "cancel" ? "canceled" : "timed_out", 1, mode == "Mask" ? 1 : 0);
        Assert.Single(metrics.For(f.Policy, Output));
        f.Terminal();
    }

    [Theory]
    [InlineData("read-io")]
    [InlineData("encoding")]
    [InlineData("invalid-utf8")]
    public async Task Precommit_transport_preflight_and_UTF8_failures_publish_no_secondary_sample(string failure)
    {
        using var metrics = new Collector();
        using var f = new Fixture(Body, Policy());
        if (failure is "read-io" or "invalid-utf8")
        {
            f.Context.ProxyResponse!.Content.Dispose();
            f.Context.ProxyResponse.Content = failure == "read-io" ? new StreamContent(new FailingReadStream()) : new ByteArrayContent([0xff]);
        }
        f.Context.ProxyResponse!.Content.Headers.ContentType = new("application/json");
        if (failure == "encoding") f.Context.ProxyResponse.Content.Headers.ContentEncoding.Add("gzip");
        await f.Run();
        Assert.Equal(ErrorBody, f.Text);
        metrics.AssertTerminal(f.Policy, "blocked", 0, 0);
        f.Terminal();
    }

    [Theory]
    [InlineData("head")]
    [InlineData("204")]
    [InlineData("304")]
    [InlineData("media")]
    [InlineData("upstream-null")]
    [InlineData("pre-cancel")]
    [InlineData("capacity")]
    public async Task Noneligible_precanceled_and_capacity_denied_responses_emit_no_matches(string scenario)
    {
        using var metrics = new Collector();
        using var f = new Fixture(Body, Policy(), noUpstream: scenario == "upstream-null");
        if (scenario == "head") f.Context.HttpContext.Request.Method = "HEAD";
        if (scenario == "204") f.Context.ProxyResponse!.StatusCode = HttpStatusCode.NoContent;
        if (scenario == "304") f.Context.ProxyResponse!.StatusCode = HttpStatusCode.NotModified;
        if (scenario == "media") f.Context.ProxyResponse!.Content.Headers.ContentType = new("text/plain");
        using var held = scenario == "capacity" ? f.Budget.TryAcquire(4096) : null;
        if (scenario == "pre-cancel") { f.Caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run); }
        else await f.Run();
        Assert.Empty(metrics.For(f.Policy, Matches));
        Assert.Empty(metrics.For(f.Policy, Masks));
        Assert.Empty(metrics.For(f.Policy, Duration));
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
                Assert.Equal(scenario == "head" ? "excluded_head" : scenario == "media" ? "excluded_media_type" : "excluded_bodyless",
                    outcome.Tags["gateway.privacy.outcome"]);
            }
            else Assert.Empty(metrics.For(f.Policy, Outcomes));
            Assert.Equal((0, 0L), f.Budget.Snapshot);
        }
    }

    [Theory]
    [InlineData(Outcomes)]
    [InlineData(Duration)]
    [InlineData(Input)]
    [InlineData(Output)]
    [InlineData(Masks)]
    [InlineData(Matches)]
    public async Task Ordinary_failure_of_each_instrument_is_isolated_and_does_not_change_enforcement(string failingInstrument)
    {
        using var metrics = new Collector();
        foreach (var mode in new[] { "Mask", "Block" })
        {
            using var f = new Fixture(Body, Policy(mode));
            metrics.OnSample = sample =>
            {
                if (sample.Tags["gateway.policy.id"] == f.Policy.PolicyId && sample.Instrument.Name == failingInstrument)
                    throw new InvalidOperationException("Synthetic listener failure.");
            };
            await f.Run();
            metrics.AssertTerminal(f.Policy, mode == "Mask" ? "inspected" : "policy_blocked", 1, mode == "Mask" ? 1 : 0);
            Assert.Single(metrics.For(f.Policy, Input)); Assert.Single(metrics.For(f.Policy, Output));
            Assert.Equal(mode == "Mask" ? Body.Replace(Pan, Mask) : BlockBody, f.Text);
            Assert.Equal(mode == "Mask" ? 200 : 502, f.Context.HttpContext.Response.StatusCode);
            f.Terminal();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Concurrent_and_repeated_completion_publishes_positive_long_once_even_if_listener_throws(bool throws)
    {
        using var metrics = new Collector();
        using var f = new Fixture(Body, Policy());
        using var measurement = f.Telemetry.TryStartInspection(f.Policy)!;
        measurement.ValidatedSecondaryPanMatches = 9007199254740993L;
        measurement.ValidatedMasks = 2;
        if (throws) metrics.OnSample = sample => { if (sample.Instrument.Name == Matches) throw new OperationCanceledException(); };
        Parallel.For(0, 32, _ => measurement.Complete(PrivacyInspectionOutcome.Inspected));
        measurement.Dispose(); measurement.Complete(PrivacyInspectionOutcome.Canceled);
        metrics.AssertTerminal(f.Policy, "inspected", 9007199254740993L, 2);
        Assert.Equal((0, 0L), f.Budget.Snapshot);
        using var next = f.Telemetry.TryStartInspection(f.Policy);
        Assert.NotNull(next);
        measurement.Dispose();
        Assert.Equal((1, 4096L), f.Budget.Snapshot);
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    public async Task Blocking_secondary_listener_retains_lease_and_capacity_without_changing_enforcement(string mode)
    {
        using var metrics = new Collector();
        using var f = new Fixture(Body, Policy(mode));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        metrics.OnSample = sample =>
        {
            if (sample.Instrument.Name != Matches || sample.Tags["gateway.policy.id"] != f.Policy.PolicyId) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic listener gate not released.");
        };
        var pending = Task.Run(f.Run);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.Equal((1, 4096L), f.Budget.Snapshot);
            Assert.Equal(mode == "Mask" ? Body.Replace(Pan, Mask) : BlockBody, f.Text);
            Assert.Equal(mode == "Mask" ? 200 : 502, f.Context.HttpContext.Response.StatusCode);
            using var deniedOutput = new MemoryStream();
            var deniedHttp = new DefaultHttpContext(); deniedHttp.Response.Body = deniedOutput;
            using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
            var denied = new ResponseTransformContext { HttpContext = deniedHttp, ProxyResponse = upstream };
            await new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, f.Telemetry).ApplyAsync(denied, Policy(mode));
            Assert.Equal(503, deniedHttp.Response.StatusCode);
            Assert.Equal("{\"code\":\"privacy_capacity_exceeded\"}", Encoding.UTF8.GetString(deniedOutput.ToArray()));
            Assert.Equal((1, 4096L), f.Budget.Snapshot);
        }
        finally { release.Set(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        metrics.AssertTerminal(f.Policy, mode == "Mask" ? "inspected" : "policy_blocked", 1, mode == "Mask" ? 1 : 0);
        f.Terminal();
    }

    [Fact]
    public async Task Concurrent_instances_with_identical_policy_tags_keep_counts_and_meters_separate()
    {
        using var metrics = new Collector();
        var policy = Policy();
        using var first = new Fixture(Body, policy);
        using var second = new Fixture("[\"4111111111111111\",\"4111111111111111\",\"4111111111111111\"]", policy);
        var arrived = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Checkpoint(CancellationToken token)
        {
            if (Interlocked.Increment(ref arrived) == 2) release.TrySetResult();
            return new(release.Task.WaitAsync(TimeSpan.FromSeconds(5), token));
        }
        first.Checkpoint = Checkpoint; second.Checkpoint = Checkpoint;
        await Task.WhenAll(first.Run(), second.Run());
        var samples = metrics.For(policy, Matches);
        Assert.Equal(2, samples.Length);
        Assert.Equal(new long[] { 1, 3 }, samples.Select(s => Assert.IsType<long>(s.Value)).Order());
        Assert.NotSame(samples[0].Instrument.Meter, samples[1].Instrument.Meter);
        foreach (var sample in samples)
        {
            var terminal = Assert.Single(metrics.For(policy, Outcomes), s => ReferenceEquals(s.Instrument.Meter, sample.Instrument.Meter));
            Assert.Equal(terminal.Tags.OrderBy(t => t.Key), sample.Tags.OrderBy(t => t.Key));
        }
        Assert.Equal(1, first.Telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(3, second.Telemetry.DashboardMetrics.ValidatedMasks);
        first.Terminal(); second.Terminal();
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    public async Task Real_HTTP_Mask_and_Block_publish_exact_secondary_counts_after_terminal_barrier(string mode)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        api.MapGet("/test", async context =>
        {
            context.Response.ContentType = "application/json"; context.Response.Headers.ETag = "\"synthetic\"";
            await context.Response.WriteAsync(Body, context.RequestAborted);
        });
        await api.StartAsync();
        var values = PolicyConfiguration.Defaults();
        values["PanPolicies:Policies:0:SecondaryPanDetection"] = "true";
        values["PanPolicies:Policies:0:Mode"] = mode;
        values["PanPolicies:Policies:0:PolicyId"] = "http-match-" + Guid.NewGuid().ToString("N");
        await using var app = GatewayHost.Build([], Address(api) + "/", b =>
        {
            b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(values);
            b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        var policy = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        var telemetry = app.Services.GetRequiredService<PrivacyTelemetry>();
        var budget = app.Services.GetRequiredService<InspectionBudget>();
        using var metrics = new Collector();
        await app.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(app)), Timeout = TimeSpan.FromSeconds(10) };
        for (var n = 1; n <= 2; n++)
        {
            using var response = await client.GetAsync("/protected/test");
            Assert.Equal(mode == "Mask" ? HttpStatusCode.OK : HttpStatusCode.BadGateway, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(mode == "Mask" ? Body.Replace(Pan, Mask) : BlockBody, text);
            Assert.True(response.Headers.CacheControl!.NoStore); Assert.False(response.Headers.Contains("ETag"));
            Assert.Equal(Encoding.UTF8.GetByteCount(text), response.Content.Headers.ContentLength);
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (telemetry.DashboardMetrics.Duration.Samples < n || budget.Snapshot != (0, 0L)) await Task.Delay(10, timeout.Token);
            var samples = metrics.For(policy, Matches);
            Assert.Equal(n, samples.Length);
            foreach (var sample in samples) Collector.AssertMatch(sample, policy, mode == "Mask" ? "inspected" : "policy_blocked", 1);
            Assert.Equal(n, metrics.For(policy, Outcomes).Length);
            Assert.Equal(mode == "Mask" ? n : 0, telemetry.DashboardMetrics.ValidatedMasks);
        }
        using var raw = await client.GetAsync("/passthrough/test");
        Assert.Equal(Body, await raw.Content.ReadAsStringAsync()); Assert.NotNull(raw.Headers.ETag);
        Assert.Equal(2, metrics.For(policy, Matches).Length);
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    // Reuses the existing transform, diagnostic checkpoint, manual clock and stream fixture
    // patterns from SecondaryPanDetectionTests/PrivacyTelemetryTests; no production seam.
    private sealed class Fixture : IDisposable
    {
        public CompiledPanPolicy Policy { get; }
        public readonly CancellationTokenSource Caller = new();
        public readonly ManualClock Clock = new();
        public readonly InspectionBudget Budget = new(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = 4096, InspectionTimeoutSeconds = 1 } }));
        public readonly PrivacyTelemetry Telemetry;
        public readonly ResponseTransformContext Context;
        private readonly MemoryStream output = new();
        public Func<CancellationToken, ValueTask>? Checkpoint;
        public Fixture(string body, CompiledPanPolicy policy, Stream? downstream = null, bool noUpstream = false)
        {
            Policy = policy; Telemetry = new(Budget);
            var http = new DefaultHttpContext(); http.Request.Method = "GET"; http.Response.Body = downstream ?? output;
            Context = new() { HttpContext = http, ProxyResponse = noUpstream ? null : new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, HeadersCopied = true, CancellationToken = Caller.Token };
        }
        public string Text => Encoding.UTF8.GetString(output.ToArray());
        public Task Run() => new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, Telemetry, Checkpoint, Clock).ApplyAsync(Context, Policy).AsTask();
        public void Terminal() { Assert.Equal(1, Telemetry.DashboardMetrics.Duration.Samples); Assert.Equal((0, 0L), Budget.Snapshot); }
        public void Dispose() { Context.ProxyResponse?.Dispose(); Telemetry.Dispose(); Caller.Dispose(); output.Dispose(); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public Action? OnTimestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { OnTimestamp?.Invoke(); return Interlocked.Read(ref ticks); }
        public void Advance() => Interlocked.Add(ref ticks, TimeSpan.TicksPerSecond);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class WriteGate : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Attempted;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Attempted = Encoding.UTF8.GetString(buffer.Span); Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class FailingReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("Synthetic read failure."));
    }

    private sealed record Sample(Instrument Instrument, object Value, Dictionary<string, string?> Tags);
    private sealed class Collector : IDisposable
    {
        public readonly ConcurrentQueue<Sample> Samples = new();
        public Action<Sample>? OnSample;
        private readonly MeterListener listener = new();
        public Collector()
        {
            listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == PrivacyTelemetry.MeterName) l.EnableMeasurementEvents(i); };
            listener.SetMeasurementEventCallback<long>((i, v, t, _) => Capture(i, v, t));
            listener.SetMeasurementEventCallback<double>((i, v, t, _) => Capture(i, v, t));
            listener.Start();
        }
        private void Capture(Instrument instrument, object value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var sample = new Sample(instrument, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString()));
            Samples.Enqueue(sample); OnSample?.Invoke(sample);
        }
        public Sample[] For(CompiledPanPolicy policy, string name) => Samples.Where(s => s.Instrument.Name == name && s.Tags.GetValueOrDefault("gateway.policy.id") == policy.PolicyId).ToArray();
        public void AssertTerminal(CompiledPanPolicy policy, string outcome, long matches, long masks, long explicitMatches = 0)
        {
            var terminal = Assert.Single(For(policy, Outcomes)); Assert.Equal(1L, Assert.IsType<long>(terminal.Value));
            Assert.Equal(outcome, terminal.Tags["gateway.privacy.outcome"]);
            Assert.Single(For(policy, Duration));
            if (matches == 0) Assert.Empty(For(policy, Matches));
            else { var sample = Assert.Single(For(policy, Matches)); AssertMatch(sample, policy, outcome, matches); Assert.Same(terminal.Instrument.Meter, sample.Instrument.Meter); }
            if (masks == 0) Assert.Empty(For(policy, Masks));
            else Assert.Equal(masks, Assert.IsType<long>(Assert.Single(For(policy, Masks)).Value));
            var explicitSamples = For(policy, "gateway.privacy.inspection.explicit_sensitive_matches");
            if (explicitMatches == 0) Assert.Empty(explicitSamples);
            else
            {
                var sample = Assert.Single(explicitSamples);
                Assert.Equal(explicitMatches, Assert.IsType<long>(sample.Value));
                Assert.IsType<Counter<long>>(sample.Instrument);
                Assert.Equal("{match}", sample.Instrument.Unit);
                Assert.Same(terminal.Instrument.Meter, sample.Instrument.Meter);
                Assert.Equal(terminal.Tags.OrderBy(t => t.Key), sample.Tags.OrderBy(t => t.Key));
            }
        }
        public static void AssertMatch(Sample sample, CompiledPanPolicy policy, string outcome, long value)
        {
            Assert.Equal(value, Assert.IsType<long>(sample.Value));
            Assert.IsType<Counter<long>>(sample.Instrument); Assert.Equal(Matches, sample.Instrument.Name);
            Assert.Equal("{match}", sample.Instrument.Unit);
            Assert.Equal("Gateway.Privacy", sample.Instrument.Meter.Name); Assert.Equal("1.0.0", sample.Instrument.Meter.Version);
            Assert.Equal(new Dictionary<string, string?>
            {
                ["gateway.route.id"] = policy.RouteId, ["gateway.policy.id"] = policy.PolicyId,
                ["gateway.policy.version"] = policy.Version, ["gateway.privacy.outcome"] = outcome
            }.OrderBy(t => t.Key), sample.Tags.OrderBy(t => t.Key));
        }
        public void Dispose() => listener.Dispose();
    }
}
