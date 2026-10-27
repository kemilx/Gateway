using System.Collections.Immutable;
using System.Net;

namespace Gateway;

internal static class GatewayDashboardEndpoints
{
    private const string Csp = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    internal static void MapGatewayDashboard(this WebApplication app, IEnumerable<string> routeIds)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var snapshots = app.Services.GetRequiredService<LocalPolicySnapshots>();
        var fixedRouteIds = routeIds.ToImmutableArray();
        ImmutableArray<DashboardPolicy> Policies(LocalPolicySnapshot snapshot) => fixedRouteIds.Select(snapshot.Catalog.Find).OfType<CompiledPanPolicy>()
            .Select(p => new DashboardPolicy(p.RouteId, p.PolicyId, p.Version, p.Mode, p.MaxBodyBytes)
            {
                Transformation = p.Transformation,
                RuleCount = 1 + p.AdditionalRules.Length,
                Transformations = p.AdditionalRules.Select(rule => rule.Transformation).Prepend(p.Transformation)
                    .Distinct(StringComparer.Ordinal).ToImmutableArray()
            })
            .ToImmutableArray();
        var telemetry = app.Services.GetRequiredService<PrivacyTelemetry>();
        var lifetime = app.Lifetime;
        var budget = app.Services.GetRequiredService<DashboardBudget>();

        // A branch catches every method and unknown subpath before proxy admission.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/dashboard")) { await next(context); return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = Csp;
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            if (!IsLocalRequest(context)) { context.Response.StatusCode = 403; return; }
            await budget.ExecuteAsync(context, async () =>
            {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.StatusCode = 405;
                context.Response.Headers.Allow = "GET, HEAD";
                return;
            }
            var path = context.Request.Path.Value;
            if (path == "/dashboard/api/summary")
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                if (HttpMethods.IsHead(context.Request.Method)) return;
                var ready = lifetime.ApplicationStarted.IsCancellationRequested
                    && !lifetime.ApplicationStopping.IsCancellationRequested;
                await context.Response.WriteAsJsonAsync(new DashboardSnapshot(1, startedAtUtc,
                    new(true, ready), Policies(snapshots.Active), telemetry.DashboardMetrics), context.RequestAborted);
                return;
            }
            var resource = path switch
            {
                "/dashboard" or "/dashboard/" => ("index.html", "text/html; charset=utf-8"),
                "/dashboard/app.js" => ("app.js", "text/javascript; charset=utf-8"),
                "/dashboard/styles.css" => ("styles.css", "text/css; charset=utf-8"),
                _ => default
            };
            if (resource.Item1 is null) { context.Response.StatusCode = 404; return; }
            await using var stream = typeof(GatewayDashboardEndpoints).Assembly
                .GetManifestResourceStream("Gateway.DashboardAssets." + resource.Item1);
            if (stream is null) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = resource.Item2;
            context.Response.ContentLength = stream.Length;
            if (!HttpMethods.IsHead(context.Request.Method))
                await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
            });
        });
    }

    internal static bool IsLocalRequest(HttpContext context)
    {
        static bool Loopback(IPAddress? address) => address is not null
            && !address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address);
        if (!Loopback(context.Connection.RemoteIpAddress)) return false;
        var host = context.Request.Host.Host;
        if (host.Contains('%') || host.EndsWith('.')) return false;
        var port = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
        if (port != context.Connection.LocalPort) return false;
        var ipHost = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !(IPAddress.TryParse(ipHost, out var ip) && Loopback(ip))) return false;
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"];
        if (fetchSite.Any(value => !string.IsNullOrEmpty(value)
            && value.Split(',').Any(site => site.Trim().Equals("cross-site", StringComparison.OrdinalIgnoreCase)))) return false;
        if (!context.Request.Headers.TryGetValue("Origin", out var origins)) return true;
        if (origins.Count != 1 || origins[0] is not { } origin
            || origin.Contains('%') || origin.Contains('\\')
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !origin.Equals(uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Scheme.Equals(context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && uri.Port == port
            && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
    }
}

// Independent, no queue or timer release; the using scope covers the actual awaited write.
internal sealed class DashboardBudget
{
    private int active;
    internal int Active => Volatile.Read(ref active);
    internal async Task ExecuteAsync(HttpContext context, Func<Task> write)
    {
        using var lease = TryAcquire();
        if (lease is null) { context.Response.StatusCode = 503; return; }
        await write();
    }
    internal IDisposable? TryAcquire()
    {
        while (true)
        {
            var current = Active;
            if (current >= 4) return null;
            if (Interlocked.CompareExchange(ref active, current + 1, current) == current) return new Lease(this);
        }
    }
    private sealed class Lease(DashboardBudget owner) : IDisposable
    {
        private DashboardBudget? budget = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref budget, null);
            if (current is not null) Interlocked.Decrement(ref current.active);
        }
    }
}
