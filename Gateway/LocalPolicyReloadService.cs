namespace Gateway;

internal sealed class LocalPolicyReloadService(LocalPolicyFileReader reader, LocalPolicySnapshots snapshots,
    TimeProvider timeProvider, ILogger<LocalPolicyReloadService> logger) : BackgroundService
{
    private string? lastRejectedReason;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var initial = await reader.ReadAsync(cancellationToken);
            snapshots.Publish(initial, cancellationToken, initial: true, checkedAtUtc: timeProvider.GetUtcNow());
            Notify("applied", "none");
        }
        catch (LocalPolicyReadException)
        { throw new InvalidOperationException("Local policy snapshot initialization failed."); }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
                await ReloadAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var candidate = await reader.ReadAsync(cancellationToken);
            var published = snapshots.Publish(candidate, cancellationToken, checkedAtUtc: timeProvider.GetUtcNow());
            lastRejectedReason = null; // valid, including unchanged, resets suppression silently
            if (published) Notify("applied", "none");
        }
        catch (LocalPolicyReadException e)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.RecordRejection(e.Reason, timeProvider.GetUtcNow());
            if (lastRejectedReason == e.Reason) return;
            lastRejectedReason = e.Reason;
            Notify("rejected", e.Reason);
        }
    }

    private void Notify(string status, string reason)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(new EventId(7, "LocalPolicySnapshot"),
                    "Local policy snapshot; status {Status} reason {Reason}.", status, reason);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }
}
