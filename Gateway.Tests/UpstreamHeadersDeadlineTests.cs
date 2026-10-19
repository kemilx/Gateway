using System.Net;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gateway.Tests;

public sealed class UpstreamHeadersDeadlineTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("GET", false, true, false, false, true)]
    [InlineData("HEAD", false, true, false, false, true)]
    [InlineData("GET", true, true, false, false, false)]
    [InlineData("POST", false, true, false, false, false)]
    [InlineData("CONNECT", false, true, false, false, false)]
    [InlineData("GET", false, false, false, false, false)]
    [InlineData("GET", false, true, true, false, false)]
    [InlineData("GET", false, true, false, true, false)]
    public void Eligibility_requires_positive_body_detection_and_excludes_upgrades(string method, bool body,
        bool feature, bool upgrade, bool connect, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (feature) context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(body));
        context.Features.Set<IHttpUpgradeFeature>(new Upgrade(upgrade));
        context.Features.Set<IHttpExtendedConnectFeature>(new ExtendedConnect(connect));
        Assert.Equal(expected, UpstreamHeadersEligibility.IsEligible(context));
        context.Request.Headers.Upgrade = "synthetic";
        Assert.False(UpstreamHeadersEligibility.IsEligible(context));
        context.Request.Headers.Remove("Upgrade");
        context.Request.Headers.Connection = "keep-alive, Upgrade";
        Assert.False(UpstreamHeadersEligibility.IsEligible(context));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Disabled_or_ineligible_passes_original_token_without_timer(bool enabled, bool eligible)
    {
        var clock = new Clock();
        using var source = new CancellationTokenSource();
        using var response = new HttpResponseMessage(HttpStatusCode.Accepted);
        using var inner = new Stub((request, token) =>
        {
            Assert.Equal(source.Token, token);
            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
            Assert.Empty(request.Headers);
            return Task.FromResult(response);
        });
        using var invoker = new HttpMessageInvoker(new UpstreamHeadersDeadlineHandler(
            enabled ? TimeSpan.FromSeconds(1) : null, clock)
        { InnerHandler = inner });
        using var request = Request(eligible);
        Assert.Same(response, await invoker.SendAsync(request, source.Token));
        Assert.Empty(clock.Timers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_or_inbound_cancel_disposes_late_response_without_abandoning_send(bool inbound)
    {
        var clock = new Clock();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new CancellationTokenSource();
        CancellationToken observed = default;
        using var invoker = Wrap(new Stub((_, token) => { observed = token; return release.Task; }), clock);
        using var request = Request();
        var pending = invoker.SendAsync(request, source.Token);
        Assert.NotEqual(source.Token, observed);
        if (inbound) source.Cancel();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(observed.IsCancellationRequested);
        Assert.Equal(inbound, source.IsCancellationRequested);
        Assert.False(pending.IsCompleted);
        var content = new TrackedContent();
        release.SetResult(new(HttpStatusCode.OK) { Content = content });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Guard));
        Assert.Equal(inbound ? source.Token : observed, error.CancellationToken);
        Assert.True(content.Disposed);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
    }

    [Theory]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    public async Task Headers_checkpoint_arbitrates_exact_boundary_even_without_timer_delivery(int milliseconds, bool success)
    {
        var clock = new Clock();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var invoker = Wrap(new Stub((_, _) => release.Task), clock);
        using var request = Request();
        var pending = invoker.SendAsync(request, default);
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds), fire: false);
        var content = new TrackedContent();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        release.SetResult(response);
        if (success)
        {
            Assert.Same(response, await pending.WaitAsync(Guard));
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.False(content.Disposed);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Guard));
            Assert.True(content.Disposed);
        }
    }

    [Fact]
    public async Task Accepted_headers_retire_timer_and_losing_cancellation_cannot_affect_slow_body()
    {
        var clock = new Clock();
        using var original = new CancellationTokenSource();
        CancellationToken observed = default;
        var body = new GateStream();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        using var invoker = Wrap(new Stub((_, token) => { observed = token; return Task.FromResult(response); }), clock);
        using var request = Request();
        Assert.Same(response, await invoker.SendAsync(request, original.Token));
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        var read = response.Content.ReadAsStringAsync();
        await body.Entered.Task.WaitAsync(Guard);
        clock.Advance(TimeSpan.FromSeconds(2));
        original.Cancel();
        Assert.False(observed.IsCancellationRequested);
        Assert.False(read.IsCompleted);
        body.Release.TrySetResult();
        Assert.Equal("", await read.WaitAsync(Guard));
    }

    [Fact]
    public async Task Cleanup_waits_for_started_cancellation_callbacks_without_holding_an_external_lock()
    {
        var clock = new Clock();
        var callbackEntered = Signal();
        using var callbackRelease = new ManualResetEventSlim();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        using var invoker = Wrap(new Stub((_, token) =>
        {
            registration = token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(); });
            return release.Task;
        }), clock);
        using var request = Request();
        var pending = invoker.SendAsync(request, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        try
        {
            await callbackEntered.Task.WaitAsync(Guard);
            var content = new TrackedContent();
            release.SetResult(new(HttpStatusCode.OK) { Content = content });
            await content.Disposal.Task.WaitAsync(Guard);
            Assert.False(pending.IsCompleted);
        }
        finally { callbackRelease.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Guard));
        await registration.DisposeAsync();
    }

    [Fact]
    public async Task Transport_fault_before_deadline_is_preserved_and_timer_retired()
    {
        var clock = new Clock();
        var failure = new HttpRequestException("synthetic");
        using var invoker = Wrap(new Stub((_, _) => Task.FromException<HttpResponseMessage>(failure)), clock);
        using var request = Request();
        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, default)));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
    }

    [Fact]
    public async Task Concurrent_headers_expiry_and_cancel_have_one_terminal_owner()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var clock = new Clock();
            var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var source = new CancellationTokenSource();
            var content = new TrackedContent();
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            using var invoker = Wrap(new Stub((_, _) => release.Task), clock);
            using var request = Request();
            var pending = invoker.SendAsync(request, source.Token);
            var start = Signal();
            var headers = Task.Run(async () => { await start.Task; release.TrySetResult(response); });
            var expiry = Task.Run(async () => { await start.Task; clock.Advance(TimeSpan.FromSeconds(1)); });
            var cancel = Task.Run(async () => { await start.Task; source.Cancel(); });
            start.TrySetResult();
            await Task.WhenAll(headers, expiry, cancel).WaitAsync(Guard);
            var error = await Record.ExceptionAsync(async () => Assert.Same(response, await pending.WaitAsync(Guard)));
            if (error is null) Assert.False(content.Disposed);
            else { Assert.IsAssignableFrom<OperationCanceledException>(error); Assert.True(content.Disposed); }
            Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        }
    }

    [Fact]
    public async Task Cooperative_expiry_cancels_inner_send_but_not_original_source()
    {
        var clock = new Clock();
        using var source = new CancellationTokenSource();
        using var invoker = Wrap(new Stub(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        }), clock);
        using var request = Request();
        var pending = invoker.SendAsync(request, source.Token);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Guard));
        Assert.False(source.IsCancellationRequested);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
    }

    [Fact]
    public async Task Noncooperative_send_retains_admission_after_expiry_until_io_finishes()
    {
        var clock = new Clock();
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var invoker = Wrap(new Stub((_, _) => release.Task), clock);
        using var request = Request();
        var admission = new ProxyAdmission(Options.Create(new ProxyAdmissionOptions { MaxConcurrentProxyRequests = 1 }));
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(
            new Yarp.ReverseProxy.Model.RouteModel(new Yarp.ReverseProxy.Configuration.RouteConfig { RouteId = "synthetic" },
                null!, Yarp.ReverseProxy.Forwarder.HttpTransformer.Default)), "proxy"));
        var pending = new ProxyAdmissionMiddleware(async _ =>
        {
            using var response = await invoker.SendAsync(request, default);
        }).InvokeAsync(context, admission);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, admission.ActiveRequests);
        Assert.Null(admission.TryAcquire());
        var content = new TrackedContent();
        release.SetResult(new(HttpStatusCode.OK) { Content = content });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Guard));
        Assert.True(content.Disposed);
        Assert.Equal(0, admission.ActiveRequests);
        using var recovered = admission.TryAcquire();
        Assert.NotNull(recovered);
    }

    [Theory]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("TimeoutSeconds", "-1")]
    [InlineData("TimeoutSeconds", "121")]
    [InlineData("TimeoutSeconds", "bad")]
    [InlineData("Unknown", "1")]
    public async Task Invalid_configuration_prevents_startup(string field, string value)
    {
        var values = PolicyConfiguration.Defaults();
        values["UpstreamHeadersDeadline:" + field] = value;
        var error = await Record.ExceptionAsync(async () =>
        {
            await using var app = Build(values);
            await app.StartAsync();
        });
        Assert.NotNull(error);
        if (field == "TimeoutSeconds" && value != "bad") Assert.IsType<OptionsValidationException>(error);
        else Assert.IsType<InvalidOperationException>(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(120)]
    public async Task Settings_freeze_valid_configuration_and_ignore_reload(int? seconds)
    {
        var values = PolicyConfiguration.Defaults();
        if (seconds.HasValue) values["UpstreamHeadersDeadline:TimeoutSeconds"] = seconds.ToString();
        await using var app = Build(values);
        await app.StartAsync();
        var settings = app.Services.GetRequiredService<UpstreamHeadersDeadlineSettings>();
        app.Configuration["UpstreamHeadersDeadline:TimeoutSeconds"] = "7";
        ((IConfigurationRoot)app.Configuration).Reload();
        Assert.Equal(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null, settings.Timeout);
        Assert.Null(new UpstreamHeadersDeadlineOptions().TimeoutSeconds);
        var options = new UpstreamHeadersDeadlineOptions { TimeoutSeconds = seconds };
        var snapshot = new UpstreamHeadersDeadlineSettings(Options.Create(options));
        options.TimeoutSeconds = 7;
        Assert.Equal(settings.Timeout, snapshot.Timeout);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("protected")]
    [InlineData("passthrough")]
    public async Task Kestrel_no_headers_yields_504_and_recovers_admission(string route)
    {
        var entered = Signal();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.MapGet("/stall", async context =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { }
        });
        upstream.MapGet("/ok", () => "recovered");
        await upstream.StartAsync();
        var clock = new Clock();
        var values = PolicyConfiguration.Defaults();
        values["UpstreamHeadersDeadline:TimeoutSeconds"] = "1";
        values["ProxyAdmission:MaxConcurrentProxyRequests"] = "1";
        await using var gateway = Build(values, Address(upstream) + "/", clock);
        await gateway.StartAsync();
        using var client = new HttpClient { BaseAddress = new(Address(gateway)), Timeout = Guard };
        try
        {
            var pending = client.GetAsync($"/{route}/stall");
            await entered.Task.WaitAsync(Guard);
            Assert.Equal(1, gateway.Services.GetRequiredService<ProxyAdmission>().ActiveRequests);
            clock.Advance(TimeSpan.FromSeconds(1));
            using var response = await pending.WaitAsync(Guard);
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            await Eventually(() => gateway.Services.GetRequiredService<ProxyAdmission>().ActiveRequests == 0);
            using var recovered = await client.GetAsync($"/{route}/ok");
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal("recovered", await recovered.Content.ReadAsStringAsync());
        }
        finally { await gateway.StopAsync(); await upstream.StopAsync(); }
    }

    [Theory]
    [InlineData("protected")]
    [InlineData("passthrough")]
    public async Task Kestrel_fast_headers_slow_body_outlives_headers_timer(string route)
    {
        var release = Signal();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.MapGet("/body", async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.StartAsync();
            await context.Response.Body.FlushAsync();
            await release.Task;
            await context.Response.WriteAsync("{\"payment\":{\"cardNumber\":\"4111111111111111\"}}");
        });
        await upstream.StartAsync();
        var clock = new Clock();
        var values = PolicyConfiguration.Defaults();
        values["UpstreamHeadersDeadline:TimeoutSeconds"] = "1";
        values["PanPolicies:InspectionBudget:InspectionTimeoutSeconds"] = "10";
        await using var gateway = Build(values, Address(upstream) + "/", clock);
        await gateway.StartAsync();
        using var client = new HttpClient { BaseAddress = new(Address(gateway)), Timeout = Guard };
        try
        {
            var pending = client.GetAsync($"/{route}/body");
            await clock.FirstDisposed.Task.WaitAsync(Guard); // The handler has accepted response headers.
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.False(pending.IsCompleted);
            release.TrySetResult();
            using var response = await pending.WaitAsync(Guard);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(route == "protected" ? "{\"payment\":{\"cardNumber\":\"************1111\"}}"
                : "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}", await response.Content.ReadAsStringAsync());
        }
        finally { release.TrySetResult(); await gateway.StopAsync(); await upstream.StopAsync(); }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (!condition()) await Task.Delay(10, guard.Token);
    }

    private static WebApplication Build(Dictionary<string, string?> values, string? upstream = null, Clock? clock = null) =>
        GatewayHost.Build([], upstream, builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(values);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            if (clock is not null) builder.Services.AddSingleton<TimeProvider>(clock);
        });

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static HttpRequestMessage Request(bool eligible = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/")
        { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        request.Options.Set(UpstreamHeadersEligibility.Key, eligible);
        return request;
    }
    private static HttpMessageInvoker Wrap(HttpMessageHandler inner, Clock clock, TimeSpan? timeout = null) =>
        new(new UpstreamHeadersDeadlineHandler(timeout ?? TimeSpan.FromSeconds(1), clock) { InnerHandler = inner });

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class BodyDetection(bool body) : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => body; }
    private sealed class Upgrade(bool upgrade) : IHttpUpgradeFeature
    {
        public bool IsUpgradableRequest => upgrade;
        public Task<Stream> UpgradeAsync() => throw new NotSupportedException();
    }
    private sealed class ExtendedConnect(bool connect) : IHttpExtendedConnectFeature
    {
        public bool IsExtendedConnect => connect;
        public string? Protocol => connect ? "synthetic" : null;
        public ValueTask<Stream> AcceptAsync() => throw new NotSupportedException();
    }
    private sealed class TrackedContent : ByteArrayContent
    {
        public bool Disposed;
        public TaskCompletionSource Disposal { get; } = Signal();
        public TrackedContent() : base([]) { }
        protected override void Dispose(bool disposing) { Disposed = true; Disposal.TrySetResult(); base.Dispose(disposing); }
    }
    private sealed class GateStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return 0; }
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public readonly System.Collections.Concurrent.ConcurrentBag<Timer> Timers = [];
        public TaskCompletionSource FirstDisposed { get; } = Signal();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            Timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed, bool fire = true)
        {
            Interlocked.Add(ref ticks, elapsed.Ticks);
            if (fire) foreach (var timer in Timers) timer.Fire();
        }
        public sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue;
            public bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed) return false;
                Interlocked.Exchange(ref due, dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks);
                return true;
            }
            public void Fire()
            {
                var target = Interlocked.Read(ref due);
                if (!Disposed && clock.GetTimestamp() >= target && Interlocked.CompareExchange(ref due, long.MaxValue, target) == target)
                    callback(state);
            }
            public void Dispose() { Disposed = true; Interlocked.Exchange(ref due, long.MaxValue); clock.FirstDisposed.TrySetResult(); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
