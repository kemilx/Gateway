using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gateway.Tests;

public sealed class DashboardTests
{
    private const string Summary = "/dashboard/api/summary";
    private static WebApplication Build(string? upstream = null, string mode = "Mask") =>
        GatewayHost.Build([], upstream, builder =>
        {
            var values = PolicyConfiguration.Defaults();
            values["PanPolicies:Policies:0:Mode"] = mode;
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(values);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        });
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static HttpClient Client(WebApplication app) => new()
        { BaseAddress = new Uri(Address(app)), Timeout = TimeSpan.FromSeconds(10) };
    private static void Keys(JsonElement element, params string[] names) =>
        Assert.Equal(names.Order(), element.EnumerateObject().Select(p => p.Name).Order());
    private static void Headers(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Summary_has_exact_redacted_shape_and_security_headers()
    {
        var before = DateTimeOffset.UtcNow;
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        using var response = await client.GetAsync(Summary + "?token=PRIVATE_MARKER");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Headers(response);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Keys(root, "schemaVersion", "startedAtUtc", "health", "policies", "metrics", "policyReload");
        var reload = root.GetProperty("policyReload");
        Keys(reload, "enabled", "status", "reason", "lastCheckedAtUtc", "lastAppliedAtUtc");
        Assert.False(reload.GetProperty("enabled").GetBoolean());
        Assert.Equal("disabled", reload.GetProperty("status").GetString());
        foreach (var name in new[] { "reason", "lastCheckedAtUtc", "lastAppliedAtUtc" })
            Assert.Equal(JsonValueKind.Null, reload.GetProperty(name).ValueKind);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.InRange(root.GetProperty("startedAtUtc").GetDateTimeOffset(), before, DateTimeOffset.UtcNow);
        Keys(root.GetProperty("health"), "live", "ready");
        Assert.True(root.GetProperty("health").GetProperty("live").GetBoolean());
        Assert.True(root.GetProperty("health").GetProperty("ready").GetBoolean());
        var policy = Assert.Single(root.GetProperty("policies").EnumerateArray());
        Keys(policy, "routeId", "policyId", "version", "mode", "maxBodyBytes", "transformation", "ruleCount", "transformations");
        Assert.Equal(1, policy.GetProperty("ruleCount").GetInt32());
        Assert.Equal(new[] { "PAN" }, policy.GetProperty("transformations").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("protected-payment", policy.GetProperty("routeId").GetString());
        Assert.Equal("Mask", policy.GetProperty("mode").GetString());
        var metrics = root.GetProperty("metrics");
        Keys(metrics, "outcomes", "validatedMasks", "input", "output", "duration", "activeInspections", "reservedBodyBytes");
        Keys(metrics.GetProperty("outcomes"), "inspected", "blocked", "capacity_exceeded", "canceled", "write_error", "faulted", "timed_out", "policy_blocked");
        foreach (var property in metrics.GetProperty("outcomes").EnumerateObject()) Assert.Equal(0, property.Value.GetInt64());
        Keys(metrics.GetProperty("input"), "samples", "totalBytes");
        Keys(metrics.GetProperty("output"), "samples", "totalBytes");
        Keys(metrics.GetProperty("duration"), "samples", "totalMilliseconds");
        foreach (var forbidden in new[] { "PRIVATE_MARKER", "selector", "http:", "cardNumber", "exception", "correlation", "synthetic-api" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        var options = app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value;
        options.Policies[0].PolicyId = "MUTATED";
        using var second = JsonDocument.Parse(await client.GetStringAsync(Summary));
        Assert.Equal(policy.GetProperty("policyId").GetString(), second.RootElement.GetProperty("policies")[0].GetProperty("policyId").GetString());
        Assert.Equal(root.GetProperty("startedAtUtc").GetString(), second.RootElement.GetProperty("startedAtUtc").GetString());
        await app.StopAsync();
    }

    [Theory]
    [InlineData("/dashboard", "text/html")]
    [InlineData("/dashboard/", "text/html")]
    [InlineData("/dashboard/app.js", "text/javascript")]
    [InlineData("/dashboard/styles.css", "text/css")]
    [InlineData(Summary, "application/json")]
    public async Task Embedded_resources_and_HEAD_have_safe_headers(string path, string contentType)
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        using var get = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(contentType, get.Content.Headers.ContentType!.MediaType);
        Assert.NotEmpty(await get.Content.ReadAsByteArrayAsync());
        Headers(get);
        using var request = new HttpRequestMessage(HttpMethod.Head, path);
        using var head = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Headers(head);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.2")]
    [InlineData("[::1]")]
    public async Task Local_hosts_are_accepted_without_trusting_forwarded_headers(string host)
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, Summary);
        request.Headers.Host = host + ":" + client.BaseAddress!.Port;
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.example");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "192.0.2.1");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.localhost")]
    [InlineData("localhost.evil")]
    [InlineData("192.0.2.1:5100")]
    [InlineData("[::ffff:127.0.0.1]:5100")]
    public async Task Malicious_hosts_are_rejected_before_resources_or_summary(string host)
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        foreach (var path in new[] { "/dashboard", "/dashboard/app.js", "/dashboard/styles.css", Summary })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = host;
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "localhost");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            Headers(response);
        }
        await app.StopAsync();
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost:5100", true)]
    [InlineData("::1", "[::1]:5100", true)]
    [InlineData("127.0.0.2", "127.0.0.2:80", true)]
    [InlineData("192.0.2.1", "localhost", false)]
    [InlineData("::ffff:127.0.0.1", "localhost", false)]
    [InlineData(null, "localhost", false)]
    public void Remote_guard_rejects_nonlocal_and_mapped_addresses(string? remote, string host, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        context.Request.Host = new HostString(host);
        context.Connection.LocalPort = context.Request.Host.Port ?? 80;
        Assert.Equal(allowed, GatewayDashboardEndpoints.IsLocalRequest(context));
    }

    [Fact]
    public async Task Non_read_methods_do_not_mutate_metrics()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        var before = await client.GetStringAsync(Summary);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Options })
        {
            using var request = new HttpRequestMessage(method, Summary) { Content = new StringContent("[DATO SINTÉTICO]") };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(before, await client.GetStringAsync(Summary));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("http", "localhost", 80, true)]
    [InlineData("https", "localhost", 443, true)]
    [InlineData("http", "localhost", 443, false)]
    [InlineData("https", "localhost", 80, false)]
    [InlineData("http", "localhost:81", 80, false)]
    [InlineData("http", "localhost.", 80, false)]
    [InlineData("http", "[::1%1]", 80, false)]
    public void Host_requires_effective_listener_port_and_no_zone_or_final_dot(string scheme, string host, int port, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = port;
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        Assert.Equal(allowed, GatewayDashboardEndpoints.IsLocalRequest(context));
    }

    [Fact]
    public async Task Wrong_Host_port_is_forbidden_over_real_HTTP()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, Summary);
        request.Headers.Host = "localhost:" + (client.BaseAddress!.Port == 1 ? 2 : 1);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Headers(response);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        await app.StopAsync();
    }

    [Theory]
    [InlineData("same", null, true)]
    [InlineData(null, null, true)]
    [InlineData("same", "same-origin", true)]
    [InlineData(null, "cross-site", false)]
    [InlineData("same", "cross-site", false)]
    [InlineData("null", null, false)]
    [InlineData("http://localhost.evil", null, false)]
    [InlineData("http://user@localhost", null, false)]
    [InlineData("path", null, false)]
    [InlineData("slash", null, false)]
    [InlineData("query", null, false)]
    [InlineData("fragment", null, false)]
    [InlineData("wrong-port", null, false)]
    [InlineData("wrong-scheme", null, false)]
    [InlineData("two", null, false)]
    public async Task Origin_and_fetch_metadata_are_checked_before_all_dashboard_resources(string? origin, string? site, bool allowed)
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        var local = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        origin = origin switch
        {
            "same" => local, "path" => local + "/private", "slash" => local + "/",
            "query" => local + "?private=1", "fragment" => local + "#private",
            "wrong-port" => "http://127.0.0.1:1", "wrong-scheme" => local.Replace("http:", "https:"),
            "two" => local + ", " + local, _ => origin
        };
        foreach (var path in new[] { Summary, "/dashboard", "/dashboard/app.js", "/dashboard/styles.css", "/dashboard/missing" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
            if (site is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
            using var response = await client.SendAsync(request);
            Assert.Equal(allowed ? path.EndsWith("missing") ? HttpStatusCode.NotFound : HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            Headers(response);
            if (!allowed) Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task Dashboard_budget_rejects_immediately_without_affecting_health_or_proxy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        var contacts = 0;
        upstream.MapGet("/ping", () => { Interlocked.Increment(ref contacts); return "synthetic"; });
        await upstream.StartAsync();
        await using var app = Build(Address(upstream) + "/");
        await app.StartAsync();
        var budget = app.Services.GetRequiredService<DashboardBudget>();
        var leases = Enumerable.Range(0, 4).Select(_ => budget.TryAcquire()!).ToArray();
        try
        {
            Assert.All(leases, lease => Assert.NotNull(lease));
            using var client = Client(app);
            foreach (var path in new[] { Summary, "/dashboard", "/dashboard/app.js", "/dashboard/styles.css" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
                Headers(response);
            }
            Assert.Equal(4, budget.Active);
            Assert.Equal("{\"status\":\"alive\"}", await client.GetStringAsync("/health/live"));
            Assert.Equal("{\"status\":\"ready\"}", await client.GetStringAsync("/health/ready"));
            Assert.Equal("synthetic", await client.GetStringAsync("/passthrough/ping"));
            Assert.Equal(1, Volatile.Read(ref contacts));
            Assert.Equal(0, app.Services.GetRequiredService<ProxyAdmission>().ActiveRequests);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
        Assert.Equal(0, budget.Active);
        using var recovered = Client(app);
        using var success = await recovered.GetAsync(Summary);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        await app.StopAsync();
        await upstream.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_budget_holds_until_actual_work_returns_and_releases_on_failure(bool fail)
    {
        var budget = new DashboardBudget();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = canceled.Token };
        var pending = budget.ExecuteAsync(context, async () =>
        {
            entered.SetResult();
            await finish.Task;
            if (fail) throw new IOException("Synthetic write failure");
        });
        await entered.Task;
        canceled.Cancel();
        Assert.Equal(1, budget.Active);
        Assert.False(pending.IsCompleted);
        finish.SetResult();
        if (fail) await Assert.ThrowsAsync<IOException>(() => pending); else await pending;
        Assert.Equal(0, budget.Active);
        using var lease = budget.TryAcquire()!;
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, budget.Active);
    }

    [Fact]
    public void Accumulator_additions_saturate_without_wrap_or_nonfinite_JSON()
    {
        Assert.Equal(long.MaxValue, PrivacyTelemetry.SaturatingAdd(long.MaxValue - 1, 2));
        Assert.Equal(long.MaxValue, PrivacyTelemetry.SaturatingAdd(long.MaxValue, 1));
        Assert.Equal(3, PrivacyTelemetry.SaturatingAdd(1L, 2L));
        Assert.Equal(1, PrivacyTelemetry.SaturatingAdd(1L, -2L));
        Assert.Equal(double.MaxValue, PrivacyTelemetry.SaturatingAdd(double.MaxValue, double.MaxValue));
        Assert.Equal(double.MaxValue, PrivacyTelemetry.SaturatingAdd(1d, double.PositiveInfinity));
        Assert.Equal(double.MaxValue, PrivacyTelemetry.SaturatingAdd(1d, double.NaN));
        Assert.Equal(3d, PrivacyTelemetry.SaturatingAdd(1d, 2d));
    }

    [Fact]
    public async Task Instance_totals_are_isolated_and_do_not_need_listeners()
    {
        await using var first = Build();
        await using var second = Build();
        await first.StartAsync();
        await second.StartAsync();
        var policy = first.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        using (var measurement = first.Services.GetRequiredService<PrivacyTelemetry>().TryStartInspection(policy)!)
            measurement.Complete(PrivacyInspectionOutcome.Inspected);
        using var a = Client(first);
        using var b = Client(second);
        using var one = JsonDocument.Parse(await a.GetStringAsync(Summary));
        using var two = JsonDocument.Parse(await b.GetStringAsync(Summary));
        Assert.Equal(1, one.RootElement.GetProperty("metrics").GetProperty("outcomes").GetProperty("inspected").GetInt64());
        Assert.Equal(0, two.RootElement.GetProperty("metrics").GetProperty("outcomes").GetProperty("inspected").GetInt64());
        await first.StopAsync();
        await second.StopAsync();
    }

    [Fact]
    public async Task Throwing_listeners_cannot_prevent_internal_totals_or_lease_release()
    {
        await using var app = Build();
        var telemetry = app.Services.GetRequiredService<PrivacyTelemetry>();
        var policy = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == PrivacyTelemetry.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("Synthetic consumer failure"));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("Synthetic consumer failure"));
        listener.Start();
        using var measurement = telemetry.TryStartInspection(policy)!;
        measurement.CapturedInputBodyBytes = 0;
        measurement.PreparedOutputBodyBytes = 7;
        measurement.ValidatedMasks = 2;
        measurement.Complete(PrivacyInspectionOutcome.WriteError);
        var totals = telemetry.DashboardMetrics;
        Assert.Equal(1, totals.Outcomes.WriteError);
        Assert.Equal(2, totals.ValidatedMasks);
        Assert.Equal(new DashboardBytes(1, 0), totals.Input);
        Assert.Equal(new DashboardBytes(1, 7), totals.Output);
        Assert.Equal(1, totals.Duration.Samples);
        Assert.Equal((0, 0L), app.Services.GetRequiredService<InspectionBudget>().Snapshot);
    }

    [Fact]
    public async Task Concurrent_completions_count_once_and_preserve_nullable_samples()
    {
        await using var app = Build();
        var telemetry = app.Services.GetRequiredService<PrivacyTelemetry>();
        var policy = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        using var measurement = telemetry.TryStartInspection(policy)!;
        measurement.ValidatedMasks = -1;
        Parallel.For(0, 100, _ => measurement.Complete(PrivacyInspectionOutcome.Canceled));
        var totals = telemetry.DashboardMetrics;
        Assert.Equal(1, totals.Outcomes.Canceled);
        Assert.Equal(0, totals.ValidatedMasks);
        Assert.Equal(new DashboardBytes(0, 0), totals.Input);
        Assert.Equal(new DashboardBytes(0, 0), totals.Output);
        Assert.Equal(1, totals.Duration.Samples);
        Assert.Equal(0, totals.ActiveInspections);
        foreach (var outcome in Enum.GetValues<PrivacyInspectionOutcome>().Where(o => o != PrivacyInspectionOutcome.CapacityExceeded))
        {
            using var next = telemetry.TryStartInspection(policy)!;
            next.Complete(outcome);
        }
        var leases = Enumerable.Range(0, 6).Select(_ => telemetry.TryStartInspection(policy)!).ToArray();
        try { Assert.Null(telemetry.TryStartInspection(policy)); }
        finally { foreach (var lease in leases) lease.Dispose(); }
        totals = telemetry.DashboardMetrics;
        Assert.Equal(new DashboardOutcomes(1, 1, 1, 2, 1, 7, 1, 1), totals.Outcomes);
        Assert.Equal(14, totals.Duration.Samples);
    }

    [Fact]
    public async Task Concurrent_inspections_keep_all_terminal_counts_without_retaining_samples()
    {
        await using var app = Build();
        var telemetry = app.Services.GetRequiredService<PrivacyTelemetry>();
        var policy = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        Parallel.For(0, 1000, _ =>
        {
            using var measurement = telemetry.TryStartInspection(policy);
            if (measurement is null) return;
            measurement.CapturedInputBodyBytes = 3;
            measurement.PreparedOutputBodyBytes = 5;
            measurement.ValidatedMasks = 1;
            measurement.Complete(PrivacyInspectionOutcome.Inspected);
        });
        var totals = telemetry.DashboardMetrics;
        Assert.Equal(1000, totals.Outcomes.Inspected + totals.Outcomes.CapacityExceeded);
        Assert.Equal(totals.Outcomes.Inspected, totals.Input.Samples);
        Assert.Equal(totals.Input.Samples * 3, totals.Input.TotalBytes);
        Assert.Equal(totals.Outcomes.Inspected, totals.Output.Samples);
        Assert.Equal(totals.Output.Samples * 5, totals.Output.TotalBytes);
        Assert.Equal(totals.Outcomes.Inspected, totals.ValidatedMasks);
        Assert.Equal(totals.Outcomes.Inspected, totals.Duration.Samples);
        Assert.Equal(0, totals.ActiveInspections);
        Assert.Equal(0, totals.ReservedBodyBytes);
    }

    [Fact]
    public async Task Summary_and_health_bypass_saturated_admission_and_inspection()
    {
        await using var app = Build();
        await app.StartAsync();
        var admission = app.Services.GetRequiredService<ProxyAdmission>();
        var budget = app.Services.GetRequiredService<InspectionBudget>();
        var leases = Enumerable.Range(0, 6).Select(_ => admission.TryAcquire()!).ToArray();
        var inspection = budget.TryAcquire(6291456)!;
        try
        {
            using var client = Client(app);
            using var summary = JsonDocument.Parse(await client.GetStringAsync(Summary));
            Assert.True(summary.RootElement.GetProperty("health").GetProperty("ready").GetBoolean());
            Assert.Equal(1, summary.RootElement.GetProperty("metrics").GetProperty("activeInspections").GetInt32());
            Assert.Equal(6291456, summary.RootElement.GetProperty("metrics").GetProperty("reservedBodyBytes").GetInt64());
            Assert.Equal("{\"status\":\"ready\"}", await client.GetStringAsync("/health/ready"));
            Assert.Equal("{\"status\":\"alive\"}", await client.GetStringAsync("/health/live"));
            Assert.Equal(6, admission.ActiveRequests);
            Assert.Equal((1, 6291456L), budget.Snapshot);
        }
        finally { inspection.Dispose(); foreach (var lease in leases) lease.Dispose(); }
        await app.StopAsync();
    }

    [Theory]
    [InlineData("Mask", false, "inspected", 1)]
    [InlineData("Block", false, "policy_blocked", 0)]
    [InlineData("Mask", true, "blocked", 0)]
    public async Task Proxy_Mask_Block_and_invalid_JSON_update_summary(string mode, bool invalid, string outcome, long masks)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        var contacts = 0;
        var body = invalid ? "{" : "{\"payment\":{\"cardNumber\":\"[DATO SINTÉTICO]\"}}";
        upstream.MapGet("/payment", async context =>
        {
            Interlocked.Increment(ref contacts);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body);
        });
        await upstream.StartAsync();
        await using var app = Build(Address(upstream) + "/", mode);
        await app.StartAsync();
        using var client = Client(app);
        using var proxy = await client.GetAsync("/protected/payment");
        var delivered = await proxy.Content.ReadAsByteArrayAsync();
        Assert.Equal(outcome == "inspected" ? HttpStatusCode.OK : HttpStatusCode.BadGateway, proxy.StatusCode);
        using var document = JsonDocument.Parse(await client.GetStringAsync(Summary));
        var totals = document.RootElement.GetProperty("metrics");
        Assert.Equal(1, totals.GetProperty("outcomes").GetProperty(outcome).GetInt64());
        Assert.Equal(masks, totals.GetProperty("validatedMasks").GetInt64());
        Assert.Equal(1, totals.GetProperty("input").GetProperty("samples").GetInt64());
        Assert.Equal(Encoding.UTF8.GetByteCount(body), totals.GetProperty("input").GetProperty("totalBytes").GetInt64());
        Assert.Equal(delivered.Length, totals.GetProperty("output").GetProperty("totalBytes").GetInt64());
        Assert.Equal(1, totals.GetProperty("duration").GetProperty("samples").GetInt64());
        Assert.Equal(1, Volatile.Read(ref contacts));
        Assert.Equal((0, 0L), app.Services.GetRequiredService<InspectionBudget>().Snapshot);
        await app.StopAsync();
        await upstream.StopAsync();
    }
}
