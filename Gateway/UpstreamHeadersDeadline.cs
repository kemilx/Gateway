using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace Gateway;

public sealed class UpstreamHeadersDeadlineOptions
{
    public const string SectionName = "UpstreamHeadersDeadline";
    public int? TimeoutSeconds { get; set; }
}

// Immutable process snapshot, including when disabled. No configuration reload in pooled handlers.
internal sealed class UpstreamHeadersDeadlineSettings(IOptions<UpstreamHeadersDeadlineOptions> options)
{
    public TimeSpan? Timeout { get; } = options.Value.TimeoutSeconds is { } seconds
        ? TimeSpan.FromSeconds(seconds) : null;
}

internal static class UpstreamHeadersEligibility
{
    internal static readonly HttpRequestOptionsKey<bool> Key = new("Gateway.UpstreamHeadersDeadline.Eligible");

    internal static bool IsEligible(HttpContext context) =>
        (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false
        && context.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest != true
        && context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect != true
        && !context.Request.Headers.ContainsKey("Upgrade")
        && !context.Request.Headers.GetCommaSeparatedValues("Connection")
            .Any(value => string.Equals(value, "upgrade", StringComparison.OrdinalIgnoreCase));
}

internal sealed class UpstreamHeadersHttpClientFactory(
    ILogger<ForwarderHttpClientFactory> logger, UpstreamHeadersDeadlineSettings settings, TimeProvider clock,
    TechnicalCorrelationSettings correlation)
    : ForwarderHttpClientFactory(logger)
{
    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);
        if (correlation.Enabled) handler.ActivityHeadersPropagator = null;
    }

    protected override HttpMessageHandler WrapHandler(ForwarderHttpClientContext context, HttpMessageHandler handler) =>
        new UpstreamHeadersDeadlineHandler(settings.Timeout, clock) { InnerHandler = base.WrapHandler(context, handler) };
}

internal sealed class UpstreamHeadersDeadlineHandler(TimeSpan? timeout, TimeProvider clock) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (timeout is not { } duration || !request.Options.TryGetValue(UpstreamHeadersEligibility.Key, out var eligible) || !eligible)
            return await base.SendAsync(request, cancellationToken);

        await using var invocation = new Invocation(clock, duration, cancellationToken);
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, invocation.Token);
            if (!invocation.AcceptHeaders()) invocation.ThrowCanceled();
            return response;
        }
        catch
        {
            response?.Dispose(); // A non-cooperative send may return after expiry.
            invocation.Fail();
            if (invocation.IsCanceled) invocation.ThrowCanceled();
            throw;
        }
    }

    // Explicit per-invocation linking through the arbiter prevents a losing cancellation
    // from canceling accepted headers. No user callback runs under a lock.
    private sealed class Invocation : IAsyncDisposable
    {
        private const int Pending = 0, Headers = 1, Expired = 2, Canceled = 3, Failed = 4;
        private readonly TimeProvider clock;
        private readonly TimeSpan duration;
        private readonly long started;
        private readonly CancellationToken original;
        private readonly CancellationTokenSource linked = new();
        private readonly TaskCompletionSource cancellationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ITimer timer;
        private readonly CancellationTokenRegistration registration;
        private int terminal;

        public Invocation(TimeProvider clock, TimeSpan duration, CancellationToken original)
        {
            this.clock = clock;
            this.duration = duration;
            this.original = original;
            started = clock.GetTimestamp();
            timer = clock.CreateTimer(static state => ((Invocation)state!).Tick(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            registration = original.UnsafeRegister(static state => ((Invocation)state!).Cancel(Canceled), this);
            timer.Change(duration, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => linked.Token;
        public bool IsCanceled => Volatile.Read(ref terminal) is Expired or Canceled;

        private void Tick()
        {
            if (Volatile.Read(ref terminal) != Pending) return;
            var remaining = duration - clock.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) timer.Change(remaining, Timeout.InfiniteTimeSpan);
            else Cancel(original.IsCancellationRequested ? Canceled : Expired);
        }

        private void Cancel(int reason)
        {
            if (Interlocked.CompareExchange(ref terminal, reason, Pending) != Pending) return;
            _ = FinishCancellationAsync();
        }

        private async Task FinishCancellationAsync()
        {
            try { await linked.CancelAsync(); }
            catch (AggregateException) { /* Callback faults cannot escape a timer or replace cancellation. */ }
            finally { cancellationFinished.TrySetResult(); }
        }

        public bool AcceptHeaders()
        {
            if (original.IsCancellationRequested) Cancel(Canceled);
            else if (clock.GetElapsedTime(started) >= duration) Cancel(Expired);
            return Interlocked.CompareExchange(ref terminal, Headers, Pending) == Pending;
        }

        public void Fail() => Interlocked.CompareExchange(ref terminal, Failed, Pending);

        public void ThrowCanceled() => throw new OperationCanceledException(
            Volatile.Read(ref terminal) == Expired ? "Upstream headers deadline elapsed." : "Upstream request canceled.",
            Volatile.Read(ref terminal) == Canceled ? original : linked.Token);

        public async ValueTask DisposeAsync()
        {
            await timer.DisposeAsync();
            await registration.DisposeAsync();
            if (IsCanceled) await cancellationFinished.Task;
            linked.Dispose();
        }
    }
}
