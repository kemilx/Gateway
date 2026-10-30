using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Tests;

[Collection("Secondary PAN telemetry isolation")]
public sealed class TechnicalCorrelationIntegrationTests
{
    private const string Header = "X-Gateway-Correlation-Id";
    private const string Body = "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}";
    private static readonly string[] Ambient = ["traceparent", "tracestate", "baggage", "Request-Id", "Correlation-Context"];

    [Theory]
    [InlineData(null)] [InlineData("false")]
    public async Task Disabled_mode_preserves_existing_behavior(string? enabled)
    {
        await using var f = await Host.Start(enabled);
        using var request = Request("protected", "json");
        using var response = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream_marker", Assert.Single(response.Headers.GetValues(Header)));
        var seen = Assert.Single(f.Seen);
        Assert.Equal("incoming_marker, second_marker, 11111111111111111111111111111111", Assert.Single(seen.Headers[Header]));
        Assert.Equal("baggage_marker=value", Assert.Single(seen.Headers["baggage"]));
        await f.WaitEvents(2);
        var terminal = Assert.Single(f.Logs.Events, e => e.Id.Id == 5);
        Assert.Matches("\\A[0-9a-f]{32}\\z", (string)terminal.State["CorrelationId"]!);
        Assert.DoesNotContain(terminal.State["CorrelationId"], seen.Headers[Header]);
    }

    [Theory]
    [InlineData("invalid-bool")] [InlineData("unknown-key")]
    public async Task Invalid_configuration_fails_before_listening(string scenario)
    {
        var config = PolicyConfiguration.Defaults();
        config[scenario == "unknown-key" ? "TechnicalCorrelation:Enabld" : "TechnicalCorrelation:Enabled"] = scenario == "unknown-key" ? "true" : "not-a-bool";
        WebApplication? unexpected = null;
        try
        {
            Assert.ThrowsAny<Exception>(() => unexpected = GatewayHost.Build([], "http://127.0.0.1:1/", b =>
            {
                b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(config);
                b.WebHost.UseUrls("http://127.0.0.1:0");
            }));
        }
        finally { if (unexpected is not null) await unexpected.DisposeAsync(); }
    }

    [Theory]
    [InlineData("protected")] [InlineData("passthrough")]
    public async Task Generated_id_overwrites_all_incoming_values(string route)
    {
        await using var f = await Host.Start("true");
        using var request = Request(route, "json");
        using var response = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(Header));
        AssertGenerated(Assert.Single(f.Seen));
        if (route == "passthrough") { Assert.Equal(Body, await response.Content.ReadAsStringAsync()); Assert.Empty(f.Logs.Events); }
    }

    [Theory]
    [InlineData("protected", false)] [InlineData("passthrough", false)]
    [InlineData("protected", true)] [InlineData("passthrough", true)]
    public async Task Connection_tokens_cannot_remove_generated_id(string route, bool mixedCaseDuplicates)
    {
        await using var f = await Host.Start("true");
        using var request = Request(route, "json");
        request.Headers.TryAddWithoutValidation("Connection", mixedCaseDuplicates
            ? new[] { "KEEP-ALIVE, X-Gateway-Correlation-Id, TraceParent", "x-GATEWAY-correlation-ID, BAGGAGE, TRACEPARENT" }
            : new[] { "keep-alive, x-gateway-correlation-id, traceparent, baggage" });
        using var response = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertGenerated(Assert.Single(f.Seen));
    }

    [Theory]
    [InlineData("protected", true)] [InlineData("passthrough", true)]
    [InlineData("protected", false)] [InlineData("passthrough", false)]
    public async Task Ambient_context_is_not_propagated_with_active_listeners(string route, bool enabled)
    {
        using var listener = new ActivityListener { ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded };
        ActivitySource.AddActivityListener(listener);
        using var observer = new DiagnosticObserver();
        using var diagnosticListener = DiagnosticListener.AllListeners.Subscribe(observer);
        using var source = new ActivitySource("synthetic-correlation-test");
        using var activity = source.StartActivity("synthetic")!;
        activity.AddBaggage("ambient", "ambient_marker"); activity.TraceStateString = "synthetic=state_marker";
        await using var f = await Host.Start(enabled ? "true" : "false");
        using var request = Request(route, "json");
        using var response = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(f.Seen);
        if (enabled) AssertGenerated(seen);
        else
        {
            // Positive control: the same listeners really cause transport propagation.
            Assert.True(seen.Headers.ContainsKey("traceparent"));
            Assert.NotEqual("00-11111111111111111111111111111111-2222222222222222-01", Assert.Single(seen.Headers["traceparent"]));
            Assert.True(response.Headers.Contains(Header));
        }
        Assert.Same(activity, Activity.Current);
    }

    [Theory]
    [InlineData("protected", "json", 200)] [InlineData("passthrough", "json", 200)]
    [InlineData("protected", "invalid", 502)] [InlineData("protected", "text", 200)]
    [InlineData("protected", "empty", 204)]
    public async Task Reserved_response_header_is_not_returned(string route, string path, int status)
    {
        await using var f = await Host.Start("true");
        using var response = await f.Client.GetAsync($"/{route}/{path}");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.False(response.Headers.Contains(Header));
        Assert.False(response.Content.Headers.Contains(Header));
    }

    [Theory]
    [InlineData("protected", true)] [InlineData("passthrough", true)]
    [InlineData("protected", false)] [InlineData("passthrough", false)]
    public async Task Reserved_response_trailer_is_not_returned_over_http2(string route, bool enabled)
    {
        await using var f = await Host.Start(enabled ? "true" : "false", http2Trailers: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/{route}/text")
        { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var response = await f.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Body, await response.Content.ReadAsStringAsync());
        Assert.Equal("HTTP/2", Assert.Single(f.Seen).Protocol);
        // An unrelated trailer proves end-to-end trailer delivery really occurred.
        Assert.Equal("control_marker", Assert.Single(response.TrailingHeaders.GetValues("X-Synthetic-Trailer")));
        Assert.False(response.Content.Headers.Contains(Header));
        if (enabled)
        {
            Assert.False(response.Headers.Contains(Header));
            Assert.False(response.TrailingHeaders.Contains(Header));
        }
        else
        {
            Assert.Equal("upstream_marker", Assert.Single(response.Headers.GetValues(Header)));
            Assert.Equal("trailer_marker", Assert.Single(response.TrailingHeaders.GetValues(Header)));
        }
    }

    [Theory]
    [InlineData("head", 200, "excluded_head")]
    [InlineData("empty", 204, "excluded_bodyless")]
    [InlineData("notmodified", 304, "excluded_bodyless")]
    [InlineData("text", 200, "excluded_media_type")]
    public async Task Exclusion_events_match_the_id_observed_upstream(string scenario, int status, string reason)
    {
        await using var f = await Host.Start("true");
        using var request = new HttpRequestMessage(scenario == "head" ? HttpMethod.Head : HttpMethod.Get, $"/protected/{scenario}");
        using var response = await f.Client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode); Assert.False(response.Headers.Contains(Header));
        var id = AssertGenerated(Assert.Single(f.Seen));
        await f.WaitEvents(1);
        var entry = Assert.Single(f.Logs.Events);
        Assert.Equal(4, entry.Id.Id); Assert.Equal(id, entry.State["CorrelationId"]);
        Assert.Equal(reason, entry.State["Reason"]); Assert.Equal("not_evaluated", entry.State["RuleEvaluation"]);
        Assert.Equal(1, entry.State["SchemaVersion"]);
        Assert.Equal(scenario == "text" ? Body : "", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Capacity_failure_removes_reserved_header_and_keeps_upstream_event_identity()
    {
        await using var f = await Host.Start("true");
        using var held = f.Gateway.Services.GetRequiredService<InspectionBudget>().TryAcquire(6291456);
        Assert.NotNull(held);
        using var response = await f.Client.GetAsync("/protected/json");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.False(response.Headers.Contains(Header));
        Assert.Equal("{\"code\":\"privacy_capacity_exceeded\"}", await response.Content.ReadAsStringAsync());
        var id = AssertGenerated(Assert.Single(f.Seen));
        await f.WaitEvents(1);
        var entry = Assert.Single(f.Logs.Events);
        Assert.Equal(5, entry.Id.Id); Assert.Equal("capacity_exceeded", entry.State["Outcome"]); Assert.Equal(id, entry.State["CorrelationId"]);
    }

    [Theory]
    [InlineData("Mask")] [InlineData("Block")] [InlineData("Observe")]
    public async Task Concurrent_requests_keep_distinct_ids_and_matching_events(string mode)
    {
        const int count = 4;
        var arrived = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Host.Start("true", mode, async c =>
        {
            if (Interlocked.Increment(ref arrived) == count) release.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), c.RequestAborted);
        });
        await Task.WhenAll(Enumerable.Range(0, count).Select(async _ =>
        {
            using var response = await f.Client.GetAsync("/protected/json");
            Assert.Equal(mode == "Block" ? 502 : 200, (int)response.StatusCode);
            Assert.False(response.Headers.Contains(Header));
            Assert.Equal(mode == "Observe" ? Body : mode == "Block" ? "{\"code\":\"privacy_response_blocked\"}" : "{\"payment\":{\"cardNumber\":\"************1111\"}}", await response.Content.ReadAsStringAsync());
        }));
        await f.WaitEvents(count * 2);
        var ids = f.Seen.Select(AssertGenerated).ToArray();
        Assert.Equal(count, ids.Distinct().Count());
        Assert.Equal(count * 2, f.Logs.Events.Count);
        foreach (var id in ids)
        {
            var events = f.Logs.Events.Where(e => Equals(e.State["CorrelationId"], id)).ToArray();
            Assert.Equal(new[] { 5, 6 }, events.Select(e => e.Id.Id).Order());
            Assert.All(events, e => { Assert.Equal(1, e.State["SchemaVersion"]); Assert.Null(e.Exception); Assert.Equal(mode == "Block" ? "policy_blocked" : "inspected", e.State["Outcome"]); });
        }
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Configuration_is_frozen_and_nonproxy_routes_are_excluded(bool enabled)
    {
        await using var f = await Host.Start(enabled ? "true" : "false");
        f.Gateway.Configuration["TechnicalCorrelation:Enabled"] = enabled ? "false" : "true";
        ((IConfigurationRoot)f.Gateway.Configuration).Reload();
        foreach (var path in new[] { "/health/live", "/health/ready", "/dashboard" })
        {
            using var response = await f.Client.GetAsync(path);
            Assert.False(response.Headers.Contains(Header));
        }
        Assert.Empty(f.Seen); Assert.Empty(f.Logs.Events);
        using var proxy = await f.Client.GetAsync("/passthrough/json");
        Assert.Equal(HttpStatusCode.OK, proxy.StatusCode);
        var seen = Assert.Single(f.Seen);
        if (enabled) AssertGenerated(seen); else Assert.False(seen.Headers.ContainsKey(Header));
    }

    private static HttpRequestMessage Request(string route, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/{route}/{path}");
        request.Headers.TryAddWithoutValidation(Header, new[] { "incoming_marker", "second_marker", "11111111111111111111111111111111" });
        request.Headers.TryAddWithoutValidation("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
        request.Headers.TryAddWithoutValidation("tracestate", "synthetic=input_marker");
        request.Headers.TryAddWithoutValidation("baggage", "baggage_marker=value");
        request.Headers.TryAddWithoutValidation("Request-Id", "|request_marker.");
        request.Headers.TryAddWithoutValidation("Correlation-Context", "context_marker=value");
        return request;
    }

    private static string AssertGenerated(Seen seen)
    {
        Assert.True(seen.Headers.ContainsKey(Header));
        var id = Assert.Single(seen.Headers[Header]);
        Assert.Matches("\\A[0-9a-f]{32}\\z", id);
        Assert.NotEqual("incoming_marker", id); Assert.NotEqual("second_marker", id);
        Assert.NotEqual("11111111111111111111111111111111", id);
        Assert.All(Ambient, name => Assert.False(seen.Headers.ContainsKey(name), name));
        return id;
    }

    private sealed record Seen(Dictionary<string, string[]> Headers, string Protocol);
    internal sealed record LogEvent(EventId Id, Dictionary<string, object?> State, Exception? Exception);
    internal sealed class Logs : ILogger<PanResponseTransform>, ILoggerProvider
    {
        internal readonly ConcurrentQueue<LogEvent> Events = new();
        internal bool ThrowEvents;
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (id.Id is not (4 or 5 or 6)) return;
            if (ThrowEvents) throw new InvalidOperationException("synthetic_logger_failure");
            Events.Enqueue(new(id, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(p => p.Key, p => p.Value), exception));
        }
        public void Dispose() { }
    }

    private sealed class Host(WebApplication api, WebApplication gateway, HttpClient client, Logs logs, ConcurrentQueue<Seen> seen) : IAsyncDisposable
    {
        public WebApplication Gateway => gateway;
        public HttpClient Client => client;
        public Logs Logs => logs;
        public ConcurrentQueue<Seen> Seen => seen;
        public static async Task<Host> Start(string? enabled, string mode = "Mask", Func<HttpContext, Task>? beforeResponse = null,
            bool http2Trailers = false)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            if (http2Trailers) builder.WebHost.ConfigureKestrel(options =>
                options.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http2));
            var api = builder.Build(); var seen = new ConcurrentQueue<Seen>();
            api.Run(async c =>
            {
                seen.Enqueue(new(c.Request.Headers.ToDictionary(p => p.Key, p => p.Value.Select(v => v!).ToArray(), StringComparer.OrdinalIgnoreCase), c.Request.Protocol));
                if (beforeResponse is not null) await beforeResponse(c);
                c.Response.Headers[Header] = "upstream_marker";
                if (c.Request.Path == "/empty") { c.Response.StatusCode = 204; return; }
                if (c.Request.Path == "/notmodified") { c.Response.StatusCode = 304; return; }
                c.Response.ContentType = c.Request.Path == "/text" ? "text/plain" : "application/json";
                if (http2Trailers)
                {
                    Assert.True(c.Response.SupportsTrailers());
                    c.Response.DeclareTrailer(Header); c.Response.DeclareTrailer("X-Synthetic-Trailer");
                }
                await c.Response.WriteAsync(c.Request.Path == "/invalid" ? "invalid" : Body);
                if (http2Trailers)
                {
                    c.Response.AppendTrailer(Header, "trailer_marker");
                    c.Response.AppendTrailer("X-Synthetic-Trailer", "control_marker");
                }
            });
            await api.StartAsync();
            WebApplication? gateway = null;
            try
            {
                var logs = new Logs(); var config = PolicyConfiguration.Defaults();
                if (enabled is not null) config["TechnicalCorrelation:Enabled"] = enabled;
                config["PanPolicies:Policies:0:Mode"] = mode;
                gateway = GatewayHost.Build([], Address(api) + "/", b =>
                {
                    b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(config);
                    b.WebHost.UseUrls("http://127.0.0.1:0"); b.Logging.ClearProviders(); b.Logging.AddProvider(logs);
                    if (http2Trailers)
                    {
                        b.WebHost.ConfigureKestrel(options =>
                            options.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http2));
                        b.Services.AddSingleton<IProxyConfigFilter, Http2FixtureConfigFilter>();
                    }
                });
                await gateway.StartAsync();
                var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, ActivityHeadersPropagator = null }) { BaseAddress = new(Address(gateway)), Timeout = TimeSpan.FromSeconds(15) };
                return new(api, gateway, client, logs, seen);
            }
            catch { if (gateway is not null) await gateway.DisposeAsync(); await api.DisposeAsync(); throw; }
        }
        public async Task WaitEvents(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (logs.Events.Count < count) await Task.Delay(10, timeout.Token);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await gateway.DisposeAsync(); await api.DisposeAsync(); }
        private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    // Test-only h2c on both hops; production listeners and cluster defaults stay unchanged.
    private sealed class Http2FixtureConfigFilter : IProxyConfigFilter
    {
        public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel) =>
            ValueTask.FromResult(cluster with { HttpRequest = cluster.HttpRequest! with
                { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact } });
        public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel) =>
            ValueTask.FromResult(route);
    }

    private sealed class DiagnosticObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly ConcurrentBag<IDisposable> subscriptions = new();
        public void OnNext(DiagnosticListener value) { if (value.Name == "HttpHandlerDiagnosticListener") subscriptions.Add(value.Subscribe(this)); }
        public void OnNext(KeyValuePair<string, object?> value) { }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { foreach (var subscription in subscriptions) subscription.Dispose(); }
    }
}
