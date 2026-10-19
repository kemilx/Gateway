namespace Gateway;

// Requests termination only; the awaiting caller retains its lease.
internal sealed class InspectionDeadline : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly TimeSpan timeout;
    private readonly HttpContext context;
    private readonly CancellationToken caller;
    private readonly CancellationToken requestAborted;
    private readonly CancellationTokenSource linked = new();
    private readonly CancellationToken token;
    private readonly CancellationTokenRegistration callerRegistration;
    private readonly CancellationTokenRegistration requestRegistration;
    private readonly TaskCompletionSource<Task> cancellationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource abortFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ITimer timer;
    private bool stopped;
    private bool timedOut;
    private bool cancellationStarted;

    public InspectionDeadline(TimeProvider clock, long started, TimeSpan timeout, HttpContext context, CancellationToken caller)
    {
        this.clock = clock;
        this.timeout = timeout;
        this.context = context;
        this.caller = caller;
        this.started = started;
        requestAborted = context.RequestAborted;
        token = linked.Token;
        timer = clock.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // Explicit forwarding makes client-triggered callbacks awaitable during cleanup too.
        callerRegistration = caller.Register(() => RequestCancellation(false));
        requestRegistration = requestAborted.Register(() => RequestCancellation(false));
        var remaining = timeout - clock.GetElapsedTime(started);
        timer.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token => token;
    public bool TimedOut { get { lock (gate) return timedOut; } }
    public bool ClientCanceled => caller.IsCancellationRequested || requestAborted.IsCancellationRequested;

    private void OnTimer()
    {
        Expire();
        lock (gate) { if (stopped || cancellationStarted) return; }
        var remaining = timeout - clock.GetElapsedTime(started);
        try { timer.Change(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds))), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Expire()
    {
        if (clock.GetElapsedTime(started) >= timeout) RequestCancellation(true);
    }

    private void RequestCancellation(bool deadlineExpired)
    {
        bool abort;
        lock (gate)
        {
            if (stopped || cancellationStarted) return;
            cancellationStarted = true;
            abort = deadlineExpired && !ClientCanceled;
            timedOut = abort;
        }
        // No external callback under gate. Publish the task before calling Abort, which
        // may itself execute RequestAborted callbacks. Cleanup awaits both operations.
        try { cancellationReady.TrySetResult(linked.CancelAsync()); }
        catch (Exception ex) { cancellationReady.TrySetResult(Task.FromException(ex)); }
        try { if (abort) context.Abort(); }
        catch (Exception) { /* An arbitrary lifetime feature must not fault a timer thread. */ }
        finally { abortFinished.TrySetResult(); }
    }

    public void Check()
    {
        Expire();
        if (TimedOut || ClientCanceled) throw new OperationCanceledException(token);
        token.ThrowIfCancellationRequested();
    }

    public void PrepareResponse(Action action)
    {
        Check();
        action();
        Check();
    }

    public async ValueTask DisposeAsync()
    {
        Expire();
        bool owner;
        bool awaitCancellation;
        lock (gate)
        {
            owner = !stopped;
            stopped = true;
            awaitCancellation = cancellationStarted;
        }
        if (!owner) { await disposed.Task; return; }
        try
        {
            await timer.DisposeAsync();
            await callerRegistration.DisposeAsync();
            await requestRegistration.DisposeAsync();
            if (awaitCancellation)
            {
                try { await (await cancellationReady.Task); }
                catch (Exception) { /* Observe callback faults; cancellation remains requested. */ }
                await abortFinished.Task;
            }
            linked.Dispose();
        }
        finally { disposed.TrySetResult(); }
    }
}
