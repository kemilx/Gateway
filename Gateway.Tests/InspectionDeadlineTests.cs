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
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Xunit;

namespace Gateway.Tests;

public sealed class InspectionDeadlineTests
{
    private const string Json = "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}";

    [Fact]
    public async Task Blocking_linked_callback_cannot_delay_abort_and_cleanup_duration_includes_callback()
    {
        using var fixture = new Fixture("read", true);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = fixture.Run();
        await fixture.Stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var registration = fixture.Stream.ObservedToken.Register(() => { entered.TrySetResult(); release.Wait(); });
        var advance = Dedicated(() => fixture.Clock.Advance(TimeSpan.FromSeconds(1)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Lifetime.AbortRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Lifetime.Aborts);
            Assert.False(pending.IsCompleted);
            Assert.Equal((1, 1024L), fixture.Budget.Snapshot);
            Assert.Empty(fixture.Metrics);
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(250), fireTimers: false);
        }
        finally { release.Set(); }
        await advance.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal((0, 0L), fixture.Budget.Snapshot);
        fixture.AssertMetrics("timed_out", 1250);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Callback_can_query_deadline_and_start_concurrent_disposal_without_gate_deadlock(bool requestCallback)
    {
        var clock = new ManualClock();
        var lifetime = new Lifetime();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var deadline = new InspectionDeadline(clock, clock.GetTimestamp(), TimeSpan.FromSeconds(1), context, lifetime.Source.Token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = (requestCallback ? lifetime.Source.Token : deadline.Token)
            .Register(() => { entered.TrySetResult(); release.Wait(); });
        var advance = Dedicated(() => clock.Advance(TimeSpan.FromSeconds(1)));
        Task? cleanup = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await lifetime.AbortRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var probe = Task.Factory.StartNew(() =>
            {
                Assert.True(deadline.TimedOut);
                Assert.ThrowsAny<OperationCanceledException>(deadline.Check);
                return deadline.DisposeAsync().AsTask();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            cleanup = await probe.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cleanup.IsCompleted);
        }
        finally { release.Set(); }
        await advance.WaitAsync(TimeSpan.FromSeconds(5));
        await (cleanup ?? deadline.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        await deadline.DisposeAsync();
        Assert.Equal(1, lifetime.Aborts);
    }

    [Fact]
    public void Inspection_timestamp_is_captured_before_lease_without_renewal()
    {
        var budget = new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() }));
        var activeAtTimestamp = new List<int>();
        var clock = new ManualClock { TimestampObserved = () => activeAtTimestamp.Add(budget.Snapshot.ActiveInspections) };
        using var telemetry = new PrivacyTelemetry(budget);
        using var measurement = telemetry.TryStartInspection(new("clock", "v1", "protected-payment", ["payment", "cardNumber"], 1024), clock);
        Assert.NotNull(measurement);
        Assert.Equal(new[] { 0 }, activeAtTimestamp);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, measurement.StartedAt);
    }

    private static Task Dedicated(Action action) => Task.Factory.StartNew(action, CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [Fact]
    public async Task Early_timer_wakeup_rearms_until_monotonic_deadline()
    {
        using var fixture = new Fixture("read", true);
        var pending = fixture.Run();
        await fixture.Stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.FireEarly();
        Assert.Equal(0, fixture.Lifetime.Aborts);
        Assert.False(pending.IsCompleted);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, fixture.Lifetime.Aborts);
        fixture.AssertMetrics("timed_out");
    }

    [Fact]
    public async Task Throwing_cancellation_callback_does_not_prevent_abort()
    {
        var clock = new ManualClock();
        var lifetime = new Lifetime();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        await using var deadline = new InspectionDeadline(clock, clock.GetTimestamp(), TimeSpan.FromSeconds(1), context, lifetime.Source.Token);
        using var registration = deadline.Token.Register(() => throw new IOException("synthetic callback failure"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(deadline.TimedOut);
        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.Equal(1, lifetime.Aborts);
    }

    [Fact]
    [Trait("Category", "TransportSmoke")]
    public async Task Kestrel_one_second_read_stall_aborts_transport_without_response_body()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.MapGet("/stall", async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.StartAsync();
            await context.Response.WriteAsync("{");
            await context.Response.Body.FlushAsync();
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { }
        });
        await upstream.StartAsync();
        string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var values = PolicyConfiguration.Defaults();
        values["PanPolicies:InspectionBudget:InspectionTimeoutSeconds"] = "1";
        await using var gateway = GatewayHost.Build([], Address(upstream) + "/", b =>
        {
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(values);
        });
        await gateway.StartAsync();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var request = client.GetAsync(Address(gateway) + "/protected/stall");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<HttpRequestException>(() => request);
        }
        finally
        {
            await gateway.StopAsync();
            await upstream.StopAsync();
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public async Task Timeout_configuration_boundaries(int seconds, bool valid)
    {
        var values = PolicyConfiguration.Defaults();
        values["PanPolicies:InspectionBudget:InspectionTimeoutSeconds"] = seconds.ToString();
        if (!valid)
        {
            Assert.Throws<InvalidOperationException>(() => GatewayHost.Build([], configure: b =>
            {
                b.Configuration.Sources.Clear();
                b.Configuration.AddInMemoryCollection(values);
            }));
            return;
        }
        await using var app = GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(values);
        });
    }

    [Fact]
    public void Timeout_defaults_and_snapshot_are_immutable()
    {
        var options = new PanPolicyOptions { InspectionBudget = new() };
        var budget = new InspectionBudget(Options.Create(options));
        options.InspectionBudget.InspectionTimeoutSeconds = 120;
        Assert.Equal(TimeSpan.FromSeconds(10), budget.InspectionTimeout);
        Assert.Equal(10, new InspectionBudgetOptions().InspectionTimeoutSeconds);
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "pan-policy-defaults.json")).Build();
        Assert.Equal("10", config["PanPolicies:InspectionBudget:InspectionTimeoutSeconds"]);
    }

    [Theory]
    [InlineData("read", true)]
    [InlineData("checkpoint", true)]
    [InlineData("write", true)]
    [InlineData("read", false)]
    [InlineData("checkpoint", false)]
    [InlineData("write", false)]
    [InlineData("cleanup", false)]
    public async Task Deadline_aborts_without_replacement_and_retains_lease_until_work_ceases(string phase, bool cooperative)
    {
        using var fixture = new Fixture(phase, cooperative);
        var pending = fixture.Run();
        await fixture.Stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Context.HttpContext.Response.HasStarted);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Lifetime.Aborts);
        if (!cooperative)
        {
            Assert.False(pending.IsCompleted);
            Assert.Equal((1, 1024L), fixture.Budget.Snapshot);
            Assert.Empty(fixture.Metrics);
            Assert.Null(fixture.Budget.TryAcquire(1024));
            if (phase == "cleanup") fixture.Clock.Advance(TimeSpan.FromMilliseconds(375), fireTimers: false);
            fixture.Stream.Release.TrySetResult();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(200, fixture.Context.HttpContext.Response.StatusCode);
        Assert.Empty(fixture.Output.ToArray());
        if (phase == "write")
        {
            Assert.Empty(fixture.Stream.ToArray());
            Assert.Equal(1, fixture.Stream.WriteCalls);
            Assert.Equal(Json.Replace("4111111111111111", "************1111"), fixture.Stream.AttemptedBody);
        }
        Assert.Equal((0, 0L), fixture.Budget.Snapshot);
        using var recovered = fixture.Budget.TryAcquire(1024);
        Assert.NotNull(recovered);
        fixture.AssertMetrics("timed_out", phase == "cleanup" ? 1375 : 1000,
            expectedInputBytes: phase == "read" ? null : Encoding.UTF8.GetByteCount(Json),
            expectedOutputBytes: phase == "write" ? 45 : null,
            expectedMasks: phase == "read" ? 0 : 1);
    }

    [Fact]
    public async Task Client_cancellation_before_deadline_remains_canceled_without_own_abort()
    {
        using var fixture = new Fixture("read", false);
        var pending = fixture.Run();
        await fixture.Stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Lifetime.Source.Cancel();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Stream.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, fixture.Lifetime.Aborts);
        fixture.AssertMetrics("canceled");
    }

    [Fact]
    public async Task Capacity_503_has_no_deadline_or_duration()
    {
        using var fixture = new Fixture("none", true);
        using var held = fixture.Budget.TryAcquire(1024);
        await fixture.Run();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(503, fixture.Context.HttpContext.Response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_capacity_exceeded\"}", Encoding.UTF8.GetString(fixture.Output.ToArray()));
        Assert.Equal(0, fixture.Lifetime.Aborts);
        var metric = Assert.Single(fixture.Metrics);
        Assert.Equal("gateway.privacy.inspection.outcomes", metric.Instrument);
        Assert.Equal("capacity_exceeded", metric.Outcome);
        Assert.Equal(1, metric.Value);
        Assert.Equal((1, 1024L), fixture.Budget.Snapshot);
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("media")]
    [InlineData("head")]
    public async Task Excluded_paths_do_not_start_deadline(string reason)
    {
        using var fixture = new Fixture(reason, true);
        if (reason == "media") fixture.Context.ProxyResponse!.Content.Headers.ContentType = new("text/plain");
        if (reason == "head") fixture.Context.HttpContext.Request.Method = "HEAD";
        await fixture.Run();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, fixture.Lifetime.Aborts);
        Assert.Empty(fixture.Metrics);
        Assert.Empty(fixture.Output.ToArray());
        Assert.Equal((0, 0L), fixture.Budget.Snapshot);
    }

    [Fact]
    public async Task Abort_induced_transport_exception_never_becomes_502()
    {
        using var fixture = new Fixture("read", false);
        var pending = fixture.Run();
        await fixture.Stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Stream.Release.TrySetException(new IOException("synthetic"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(200, fixture.Context.HttpContext.Response.StatusCode);
        Assert.Empty(fixture.Output.ToArray());
        fixture.AssertMetrics("timed_out");
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    public async Task Monotonic_checkpoint_enforces_exact_boundary_without_timer_delivery(int milliseconds, bool expired)
    {
        using var fixture = new Fixture("checkpoint", false);
        var pending = fixture.Run();
        await fixture.Stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds), fireTimers: false);
        Assert.Equal(0, fixture.Lifetime.Aborts);
        fixture.Stream.Release.TrySetResult();
        if (expired)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(1, fixture.Lifetime.Aborts);
            Assert.Empty(fixture.Output.ToArray());
        }
        else await pending;
        fixture.AssertMetrics(expired ? "timed_out" : "inspected", milliseconds,
            expectedInputBytes: Encoding.UTF8.GetByteCount(Json), expectedOutputBytes: expired ? null : 45, expectedMasks: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_before_deadline_preserves_success_and_502(bool invalid)
    {
        using var fixture = new Fixture("none", true, invalid);
        await fixture.Run();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, fixture.Lifetime.Aborts);
        Assert.Equal(invalid ? 502 : 200, fixture.Context.HttpContext.Response.StatusCode);
        Assert.Equal(invalid ? "{\"code\":\"privacy_inspection_failed\"}" : Json.Replace("4111111111111111", "************1111"), Encoding.UTF8.GetString(fixture.Output.ToArray()));
        fixture.AssertMetrics(invalid ? "blocked" : "inspected", 0,
            expectedInputBytes: invalid ? 1 : Encoding.UTF8.GetByteCount(Json), expectedOutputBytes: invalid ? 36 : 45,
            expectedMasks: invalid ? 0 : 1);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ManualClock Clock = new();
        public readonly Lifetime Lifetime = new();
        public readonly MemoryStream Output = new();
        public readonly GateStream Stream;
        public readonly InspectionBudget Budget = new(Options.Create(new PanPolicyOptions
        { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = 1024, InspectionTimeoutSeconds = 1 } }));
        public readonly ResponseTransformContext Context;
        public readonly ConcurrentQueue<Measurement> Metrics = new();
        private readonly MeterListener listener = new();
        private readonly PrivacyTelemetry telemetry;
        private readonly PanResponseTransform transform;
        private readonly CompiledPanPolicy policy = new(Guid.NewGuid().ToString(), "v1", "protected-payment", ["payment", "cardNumber"], 1024);

        public Fixture(string phase, bool cooperative, bool invalid = false)
        {
            listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == PrivacyTelemetry.MeterName) l.EnableMeasurementEvents(i); };
            listener.SetMeasurementEventCallback<long>((i, value, tags, _) => Record(i, value, tags));
            listener.SetMeasurementEventCallback<double>((i, value, tags, _) => Record(i, value, tags));
            listener.Start();
            telemetry = new(Budget);
            Stream = new(cooperative, phase == "cleanup");
            if (phase == "cleanup")
            {
                Stream.Write(Encoding.UTF8.GetBytes(Json));
                Stream.Position = 0;
            }
            var http = new DefaultHttpContext();
            http.Features.Set<IHttpRequestLifetimeFeature>(Lifetime);
            http.Response.Body = phase == "write" ? Stream : Output;
            HttpContent content = phase is "read" or "cleanup" ? new StreamContent(Stream) : new StringContent(invalid ? "{" : Json);
            content.Headers.ContentType = new("application/json");
            Context = new() { HttpContext = http, ProxyResponse = phase == "headers" ? null : new(HttpStatusCode.OK) { Content = content }, CancellationToken = Lifetime.Source.Token };
            if (phase == "headers") content.Dispose();
            transform = new(NullLogger<PanResponseTransform>.Instance, telemetry,
                phase == "checkpoint" ? token => Stream.Wait(token) : null, Clock);
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (!tags.ToArray().Any(t => t.Key == "gateway.policy.id" && Equals(t.Value, policy.PolicyId))) return;
            Metrics.Enqueue(new(instrument.Name, value, tags.ToArray(), instrument));
        }

        public Task Run() => transform.ApplyAsync(Context, policy).AsTask();
        public void AssertMetrics(string outcome, double expectedMilliseconds = 1000, long? expectedInputBytes = null,
            long? expectedOutputBytes = null, long expectedMasks = 0)
        {
            Assert.Equal(2 + (expectedInputBytes.HasValue ? 1 : 0) + (expectedOutputBytes.HasValue ? 1 : 0) + (expectedMasks > 0 ? 1 : 0), Metrics.Count);
            Assert.Equal(1, Assert.Single(Metrics, m => m.Instrument == "gateway.privacy.inspection.outcomes" && m.Outcome == outcome).Value);
            Assert.Equal(expectedMilliseconds, Assert.Single(Metrics, m => m.Instrument == "gateway.privacy.inspection.duration" && m.Outcome == outcome).Value);
            AssertSize("gateway.privacy.inspection.input_body_bytes", expectedInputBytes);
            AssertSize("gateway.privacy.inspection.output_body_bytes", expectedOutputBytes);
            var masks = Metrics.Where(m => m.Instrument == "gateway.privacy.inspection.masks");
            if (expectedMasks > 0)
            {
                var sample = Assert.Single(masks);
                Assert.Equal(expectedMasks, sample.Value);
                Assert.IsType<Counter<long>>(sample.Source);
                Assert.Equal("{mask}", sample.Source.Unit);
                Assert.Same(Assert.Single(Metrics, m => m.Instrument == "gateway.privacy.inspection.outcomes").Source.Meter,
                    sample.Source.Meter);
            }
            else Assert.Empty(masks);
            void AssertSize(string instrument, long? expectedBytes)
            {
                var sizes = Metrics.Where(m => m.Instrument == instrument);
                if (expectedBytes is { } bytes)
                {
                    var size = Assert.Single(sizes);
                    Assert.Equal(bytes, size.Value);
                    Assert.IsType<Histogram<long>>(size.Source);
                    Assert.Equal("By", size.Source.Unit);
                    Assert.Equal("Gateway.Privacy", size.Source.Meter.Name);
                    Assert.Equal("1.0.0", size.Source.Meter.Version);
                    Assert.Same(Assert.Single(Metrics, m => m.Instrument == "gateway.privacy.inspection.outcomes").Source.Meter,
                        size.Source.Meter);
                }
                else Assert.Empty(sizes);
            }
            var expected = new Dictionary<string, object?>
            {
                ["gateway.policy.id"] = policy.PolicyId,
                ["gateway.policy.version"] = policy.Version,
                ["gateway.route.id"] = policy.RouteId,
                ["gateway.privacy.outcome"] = outcome
            };
            foreach (var metric in Metrics)
                Assert.Equal(expected.OrderBy(t => t.Key), metric.Tags.OrderBy(t => t.Key));
        }
        public void Dispose() { Context.ProxyResponse?.Dispose(); telemetry.Dispose(); listener.Dispose(); Output.Dispose(); Stream.Dispose(); Lifetime.Source.Dispose(); }
    }

    private sealed record Measurement(string Instrument, double Value, KeyValuePair<string, object?>[] Tags, Instrument Source)
    {
        public string Outcome => (string)Tags.Single(t => t.Key == "gateway.privacy.outcome").Value!;
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public CancellationTokenSource Source { get; } = new();
        public int Aborts;
        public TaskCompletionSource AbortRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RequestAborted { get => Source.Token; set => throw new NotSupportedException(); }
        public void Abort() { Interlocked.Increment(ref Aborts); AbortRequested.TrySetResult(); Source.Cancel(); }
    }

    private sealed class GateStream(bool cooperative, bool stallCleanup = false) : MemoryStream
    {
        public int WriteCalls;
        public string? AttemptedBody;
        public CancellationToken ObservedToken { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask Wait(CancellationToken token)
        {
            ObservedToken = token;
            Entered.TrySetResult();
            if (cooperative) await Release.Task.WaitAsync(token);
            else await Release.Task;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stallCleanup) return await base.ReadAsync(buffer, cancellationToken);
            await Wait(cancellationToken);
            return 0;
        }
        public override async ValueTask DisposeAsync()
        {
            if (stallCleanup) await Wait(CancellationToken.None);
            await base.DisposeAsync();
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            AttemptedBody = Encoding.UTF8.GetString(buffer.Span);
            await Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            base.Write(buffer.Span);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        private readonly List<ManualTimer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public Action? TimestampObserved;
        public override long GetTimestamp() { TimestampObserved?.Invoke(); return Interlocked.Read(ref ticks); }
        public void FireEarly() { foreach (var timer in timers.ToArray()) timer.Fire(early: true); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed, bool fireTimers = true)
        {
            Interlocked.Add(ref ticks, elapsed.Ticks);
            if (fireTimers) foreach (var timer in timers.ToArray()) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks; return true; }
            public void Fire(bool early = false) { if (!early && clock.ticks < due) return; due = long.MaxValue; callback(state); }
            public void Dispose() => due = long.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
