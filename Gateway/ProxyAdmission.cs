using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;

namespace Gateway;

public sealed class ProxyAdmissionOptions
{
    public const string SectionName = "ProxyAdmission";
    public int MaxConcurrentProxyRequests { get; set; } = 6;
}

// Per-instance, independent of response inspection. No waiters or cancellation-based release.
public sealed class ProxyAdmission
{
    private readonly int limit;
    private int active;

    public ProxyAdmission(IOptions<ProxyAdmissionOptions> options) => limit = options.Value.MaxConcurrentProxyRequests;

    public int ActiveRequests => Volatile.Read(ref active);

    public IDisposable? TryAcquire()
    {
        var observed = Volatile.Read(ref active);
        while (observed < limit)
        {
            var previous = Interlocked.CompareExchange(ref active, observed + 1, observed);
            if (previous == observed) return new Lease(this);
            observed = previous;
        }
        return null;
    }

    private sealed class Lease(ProxyAdmission owner) : IDisposable
    {
        private ProxyAdmission? owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is not null) Interlocked.Decrement(ref current.active);
        }
    }
}

public sealed class ProxyAdmissionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ProxyAdmission admission)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>() is null)
        {
            await next(context);
            return;
        }

        using var lease = admission.TryAcquire();
        if (lease is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync("{\"code\":\"gateway_capacity_exceeded\"}", context.RequestAborted);
            return;
        }

        // Dispose only after the entire downstream pipeline and its finally blocks return.
        await next(context);
    }
}
