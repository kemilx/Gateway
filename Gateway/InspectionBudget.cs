using Microsoft.Extensions.Options;

namespace Gateway;

// Immutable limits, atomic accounting, no capacity waiters or queued inspections.
public sealed class InspectionBudget
{
    private readonly object gate = new();
    private readonly int maxConcurrent;
    private readonly long maxBytes;
    private int active;
    private long reserved;

    public InspectionBudget(IOptions<PanPolicyOptions> options)
    {
        var limits = options.Value.InspectionBudget!;
        maxConcurrent = limits.MaxConcurrentInspections;
        maxBytes = limits.MaxReservedBodyBytes;
    }

    public (int ActiveInspections, long ReservedBodyBytes) Snapshot
    {
        get { lock (gate) return (active, reserved); }
    }

    public IDisposable? TryAcquire(long reservedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reservedBytes);
        lock (gate)
        {
            if (active >= maxConcurrent || reservedBytes > maxBytes - reserved) return null;
            var lease = new Lease(this, reservedBytes);
            active++;
            reserved += reservedBytes;
            return lease;
        }
    }

    private sealed class Lease(InspectionBudget owner, long bytes) : IDisposable
    {
        private InspectionBudget? budget = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref budget, null);
            if (current is null) return;
            lock (current.gate)
            {
                current.active--;
                current.reserved -= bytes;
            }
        }
    }
}
