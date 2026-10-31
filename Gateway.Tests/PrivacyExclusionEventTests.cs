using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
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
using Microsoft.Extensions.Logging.Console;
using TestApi;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

[Collection("Secondary PAN telemetry isolation")]
public sealed class PrivacyExclusionEventTests
{
    private const string Template = "Privacy excluded; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} reason {Reason} rule evaluation {RuleEvaluation} correlation {CorrelationId}.";
    private const string Outcomes = "gateway.privacy.inspection.outcomes";
    private static CompiledPanPolicy Policy(string mode = "Mask") => new("EventPolicy", "v1", "protected-payment", ["selector_marker"], 1024) { Mode = mode };
    private static InspectionBudget Budget() => new(Options.Create(new PanPolicyOptions { InspectionBudget = new() }));

    public static IEnumerable<object[]> UnsafePolicyIdentifiers()
    {
        foreach (var field in new[] { "route", "policy", "version" })
        foreach (var scenario in new[] { "newline", "trailing-newline", "long", "empty", "null", "space", "unicode", "invalid-first", "slash", "control" })
            yield return [field, scenario];
    }

    [Theory]
    [MemberData(nameof(UnsafePolicyIdentifiers))]
    public async Task Public_api_unsafe_policy_identifiers_omit_only_new_event(string field, string scenario)
    {
        string? value = scenario switch
        {
            "newline" => "unsafe\nmarker", "trailing-newline" => "safe\n", "long" => new string('a', 65),
            "empty" => "", "null" => null, "space" => "has space", "unicode" => "á",
            "invalid-first" => "_first", "slash" => "route/path", _ => "nul\0marker"
        };
        var policy = Policy() with
        {
            RouteId = field == "route" ? value! : "protected-payment",
            PolicyId = field == "policy" ? value! : "EventPolicy",
            Version = field == "version" ? value! : "v1"
        };
        using var metrics = new Metrics();
        var budget = Budget();
        using var telemetry = new PrivacyTelemetry(budget);
        var dashboard = telemetry.DashboardMetrics;
        var logs = new Logs();
        using var upstream = new HttpResponseMessage { Content = new UnreadableContent() };
        var context = Context(upstream);

        await new PanResponseTransform(logs, telemetry).ApplyAsync(context, policy);

        Assert.Empty(logs.Events);
        Assert.Equal(0, logs.LogCalls);
        var metric = Assert.Single(metrics.Values);
        Assert.Equal(Outcomes, metric.Name);
        Assert.Equal(1, metric.Value);
        Assert.Equal(4, metric.Tags.Count);
        Assert.Equal(policy.RouteId, metric.Tags["gateway.route.id"]);
        Assert.Equal(policy.PolicyId, metric.Tags["gateway.policy.id"]);
        Assert.Equal(policy.Version, metric.Tags["gateway.policy.version"]);
        Assert.Equal("excluded_media_type", metric.Tags["gateway.privacy.outcome"]);
        Assert.Equal(200, context.HttpContext.Response.StatusCode);
        Assert.Equal("no-store", context.HttpContext.Response.Headers.CacheControl);
        Assert.False(context.SuppressResponseBody);
        Assert.Equal(0, ((UnreadableContent)upstream.Content).ReadAttempts);
        Assert.Equal((0, 0L), budget.Snapshot);
        Assert.Equal(dashboard, telemetry.DashboardMetrics);
    }

    [Theory]
    [InlineData("one")]
    [InlineData("max")]
    [InlineData("grammar")]
    public async Task Public_api_valid_policy_identifier_boundaries_emit_exact_values(string scenario)
    {
        var value = scenario switch { "one" => "0", "max" => new string('Z', 64), _ => "aZ09._-" };
        var policy = Policy() with { RouteId = value, PolicyId = value, Version = value };
        using var telemetry = new PrivacyTelemetry(Budget());
        var logs = new Logs();
        using var upstream = new HttpResponseMessage { Content = new UnreadableContent() };

        await new PanResponseTransform(logs, telemetry).ApplyAsync(Context(upstream), policy);

        AssertEvent(Assert.Single(logs.Events), "excluded_media_type", "unknown", value, value, value);
    }

    [Fact]
    public async Task Host_console_configuration_adds_no_scopes_or_activity_enrichment()
    {
        await using var gateway = GatewayHost.Build([], "http://127.0.0.1:5101/", b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        var providers = gateway.Services.GetServices<ILoggerProvider>().ToArray();
        Assert.IsType<ConsoleLoggerProvider>(Assert.Single(providers));
        Assert.False(gateway.Services.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue.IncludeScopes);
        // Ambient Activity flags belong to LoggerFactory's existing defaults, not this event.
        // Console does not render their scope because IncludeScopes is false.
        var activity = gateway.Services.GetRequiredService<IOptions<LoggerFactoryOptions>>().Value.ActivityTrackingOptions;
        Assert.Equal(ActivityTrackingOptions.SpanId | ActivityTrackingOptions.TraceId | ActivityTrackingOptions.ParentId, activity);
    }

    public static IEnumerable<object[]> Exclusions()
    {
        foreach (var mode in new[] { "Mask", "Block", "Observe" })
        foreach (var scenario in new[] { "head", "head204", "head304", "head-plain", "204", "304", "204-plain", "304-plain", "plain", "missing", "invalid", "problem" })
            yield return [mode, scenario];
    }

    [Theory]
    [MemberData(nameof(Exclusions))]
    public async Task Exclusions_have_exact_schema_priority_and_no_inspection(string mode, string scenario)
    {
        using var metrics = new Metrics();
        var budget = Budget();
        using var held = budget.TryAcquire(1);
        var beforeBudget = budget.Snapshot;
        using var telemetry = new PrivacyTelemetry(budget);
        var beforeDashboard = telemetry.DashboardMetrics;
        var logs = new Logs();
        using var content = new UnreadableContent();
        if (scenario.EndsWith("plain")) content.Headers.TryAddWithoutValidation("Content-Type", "text/plain");
        else if (scenario == "invalid") content.Headers.TryAddWithoutValidation("Content-Type", "invalid media_marker");
        else if (scenario == "problem") content.Headers.TryAddWithoutValidation("Content-Type", "application/problem+json");
        else if (scenario != "missing") content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        using var upstream = new HttpResponseMessage(scenario.Contains("204") ? HttpStatusCode.NoContent
            : scenario.Contains("304") ? HttpStatusCode.NotModified : HttpStatusCode.OK) { Content = content };
        var context = Context(upstream);
        context.HttpContext.Request.Method = scenario.StartsWith("head") ? "HEAD" : "GET";
        await new PanResponseTransform(logs, telemetry, new NoInspectionClock()).ApplyAsync(context, Policy(mode));
        var rejected = scenario.StartsWith("304");
        var reason = rejected ? "blocked" : scenario.StartsWith("head") ? "excluded_head" : scenario.StartsWith("204") ? "excluded_bodyless" : "excluded_media_type";
        if (rejected) Assert.Empty(logs.Events); else AssertEvent(Assert.Single(logs.Events), reason, "unknown");
        var metric = Assert.Single(metrics.Values);
        Assert.Equal(Outcomes, metric.Name);
        Assert.Equal(1, metric.Value);
        Assert.Equal(4, metric.Tags.Count);
        Assert.Equal("protected-payment", metric.Tags["gateway.route.id"]);
        Assert.Equal("EventPolicy", metric.Tags["gateway.policy.id"]);
        Assert.Equal("v1", metric.Tags["gateway.policy.version"]);
        Assert.Equal(reason, metric.Tags["gateway.privacy.outcome"]);
        Assert.Equal(reason != "excluded_media_type", context.SuppressResponseBody);
        Assert.Equal("no-store", context.HttpContext.Response.Headers.CacheControl);
        Assert.Equal(rejected ? 502 : 200, context.HttpContext.Response.StatusCode);
        Assert.Equal(0, content.ReadAttempts);
        Assert.Equal(beforeBudget, budget.Snapshot);
        if (rejected) Assert.Equal(beforeDashboard.Outcomes.Blocked + 1, telemetry.DashboardMetrics.Outcomes.Blocked);
        else Assert.Equal(beforeDashboard, telemetry.DashboardMetrics);
    }

    [Fact]
    public async Task Sensitive_markers_are_absent_from_state_and_rendered_event()
    {
        using var telemetry = new PrivacyTelemetry(Budget());
        var logs = new Logs();
        using var activity = new Activity("activity_marker").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.AddBaggage("baggage", "baggage_marker");
        activity.AddTag("tag", "tag_marker");
        using var upstream = new HttpResponseMessage { Content = new StringContent("body_marker") };
        upstream.Headers.TryAddWithoutValidation("X-Marker", "upstream_header_marker");
        var context = Context(upstream);
        context.HttpContext.Request.QueryString = new QueryString("?secret=query_marker");
        context.HttpContext.Request.Headers["X-Correlation-Id"] = "incoming_correlation_marker";
        context.HttpContext.Request.Headers["X-Secret"] = "request_header_marker";
        context.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("request_body_marker"));
        context.HttpContext.TraceIdentifier = "trace_identifier_marker";
        await new PanResponseTransform(logs, telemetry).ApplyAsync(context, Policy());
        var item = Assert.Single(logs.Events);
        AssertEvent(item, "excluded_media_type", "unknown");
        var text = item.Rendered + string.Join("|", item.State.Select(x => x.Key + "=" + x.Value));
        foreach (var marker in new[] { "body_marker", "upstream_header_marker", "query_marker", "incoming_correlation_marker", "request_header_marker", "request_body_marker", "trace_identifier_marker", "activity_marker", "baggage_marker", "tag_marker", "selector_marker", activity.Id!, activity.TraceId.ToString() })
            Assert.DoesNotContain(marker, text);
        Assert.False(context.HttpContext.Response.Headers.ContainsKey("X-Correlation-Id"));
        Assert.Equal("incoming_correlation_marker", context.HttpContext.Request.Headers["X-Correlation-Id"]);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("json")]
    [InlineData("json-parameters")]
    public async Task No_upstream_or_eligible_json_emits_no_exclusion_event(string scenario)
    {
        using var telemetry = new PrivacyTelemetry(Budget());
        var logs = new Logs();
        using var upstream = new HttpResponseMessage { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (scenario == "json-parameters")
        {
            upstream.Content.Headers.Remove("Content-Type");
            upstream.Content.Headers.TryAddWithoutValidation("Content-Type", "APPLICATION/JSON; charset=utf-8; synthetic=media_marker");
        }
        var context = Context(scenario == "absent" ? null : upstream);
        await new PanResponseTransform(logs, telemetry).ApplyAsync(context, Policy());
        Assert.Empty(logs.Events);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("enabled-error")]
    [InlineData("enabled-cancel")]
    [InlineData("log-error")]
    [InlineData("log-cancel")]
    public async Task Disabled_or_failing_logger_preserves_counter_and_http(string behavior)
    {
        using var metrics = new Metrics();
        using var telemetry = new PrivacyTelemetry(Budget());
        var logs = new Logs { Behavior = behavior };
        using var upstream = new HttpResponseMessage { Content = new UnreadableContent() };
        var context = Context(upstream);
        await new PanResponseTransform(logs, telemetry).ApplyAsync(context, Policy());
        Assert.Equal(1, Assert.Single(metrics.Values).Value);
        Assert.Equal(200, context.HttpContext.Response.StatusCode);
        Assert.False(context.SuppressResponseBody);
        Assert.Equal(1, logs.EnabledCalls);
        Assert.Equal(behavior.StartsWith("log-") ? 1 : 0, logs.LogCalls);
        Assert.Empty(logs.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Throwing_metric_listener_does_not_prevent_event(bool cancel)
    {
        using var metrics = new Metrics(cancel ? new OperationCanceledException("metric_marker") : new InvalidOperationException("metric_marker"));
        using var telemetry = new PrivacyTelemetry(Budget());
        var logs = new Logs();
        using var upstream = new HttpResponseMessage { Content = new UnreadableContent() };
        var context = Context(upstream);
        await new PanResponseTransform(logs, telemetry).ApplyAsync(context, Policy());
        AssertEvent(Assert.Single(logs.Events), "excluded_media_type", "unknown");
        Assert.Single(metrics.Values);
        Assert.Equal(200, context.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("max")]
    [InlineData("long")]
    [InlineData("url")]
    [InlineData("unicode")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("first")]
    public async Task Internal_service_metadata_is_bounded_and_validated(string scenario)
    {
        var service = scenario switch { "valid" => "synthetic-api", "max" => new string('a', 64), "long" => new string('a', 65), "url" => "http://address_marker/", "unicode" => "á", "empty" => "", "null" => null, _ => "-invalid" };
        using var telemetry = new PrivacyTelemetry(Budget());
        var logs = new Logs();
        using var upstream = new HttpResponseMessage { Content = new UnreadableContent() };
        await new PanResponseTransform(logs, telemetry).ApplyAsync(Context(upstream), Policy(), service);
        AssertEvent(Assert.Single(logs.Events), "excluded_media_type", scenario is "valid" or "max" ? service! : "unknown");
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("log-error")]
    [InlineData("log-cancel")]
    [InlineData("enabled-error")]
    [InlineData("enabled-cancel")]
    public async Task Real_yarp_uses_host_cluster_and_local_distinct_correlations(string behavior)
    {
        var logs = new Logs { Behavior = behavior };
        await using var api = SyntheticApi.Build([], b => b.WebHost.UseUrls("http://127.0.0.1:0"), app =>
            app.MapGet("/event-plain", () => Results.Text("synthetic_body_marker", "text/plain")));
        await api.StartAsync();
        await using var gateway = GatewayHost.Build([], Address(api) + "/", b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Logging.ClearProviders();
            b.Logging.AddProvider(logs);
        });
        await gateway.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(10) };
        var telemetry = gateway.Services.GetRequiredService<PrivacyTelemetry>();
        var before = telemetry.DashboardMetrics;
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/protected/event-plain?secret=query_marker");
            request.Headers.Add("X-Correlation-Id", "incoming_marker");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("synthetic_body_marker", await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.False(response.Headers.Contains("X-Correlation-Id"));
        }
        Assert.Equal(before, telemetry.DashboardMetrics);
        Assert.Equal((0, 0L), gateway.Services.GetRequiredService<InspectionBudget>().Snapshot);
        if (behavior == "normal")
        {
            var events = logs.Events.ToArray();
            Assert.Equal(2, events.Length);
            foreach (var item in events)
            {
                Assert.Equal("synthetic-api", item.State["ServiceId"]);
                Assert.Equal("protected-payment", item.State["RouteId"]);
                Assert.Equal("PAN", item.State["PolicyId"]);
                Assert.Equal("v1", item.State["PolicyVersion"]);
                Assert.Equal(Template, item.State["{OriginalFormat}"]);
                Assert.Matches("\\A[0-9a-f]{32}\\z", Assert.IsType<string>(item.State["CorrelationId"]));
                Assert.Equal("excluded_media_type", item.State["Reason"]);
                Assert.DoesNotContain("marker", item.Rendered);
                Assert.DoesNotContain(Address(api), item.Rendered);
            }
            Assert.NotEqual(events[0].State["CorrelationId"], events[1].State["CorrelationId"]);
            using var passthrough = await client.GetAsync("/passthrough/event-plain");
            Assert.Equal("synthetic_body_marker", await passthrough.Content.ReadAsStringAsync());
            Assert.Equal(2, logs.Events.Count); // No policy: no new event.
        }
        else Assert.Empty(logs.Events);
        await gateway.StopAsync();
        await api.StopAsync();
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static ResponseTransformContext Context(HttpResponseMessage? upstream)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Response.Body = new MemoryStream();
        return new ResponseTransformContext { HttpContext = http, ProxyResponse = upstream, CancellationToken = default };
    }

    private static void AssertEvent(Event item, string reason, string service,
        string route = "protected-payment", string policy = "EventPolicy", string version = "v1")
    {
        Assert.Equal(LogLevel.Information, item.Level);
        Assert.Equal(new EventId(4, "PrivacyExcluded"), item.Id);
        Assert.Null(item.Exception);
        Assert.Equal(new[] { "CorrelationId", "PolicyId", "PolicyVersion", "Reason", "RouteId", "RuleEvaluation", "SchemaVersion", "ServiceId", "{OriginalFormat}" }.Order(), item.State.Keys.Order());
        Assert.Equal(1, item.State["SchemaVersion"]);
        Assert.Equal(route, item.State["RouteId"]);
        Assert.Equal(service, item.State["ServiceId"]);
        Assert.Equal(policy, item.State["PolicyId"]);
        Assert.Equal(version, item.State["PolicyVersion"]);
        Assert.Equal(reason, item.State["Reason"]);
        Assert.Equal("not_evaluated", item.State["RuleEvaluation"]);
        Assert.Equal(Template, item.State["{OriginalFormat}"]);
        var correlation = Assert.IsType<string>(item.State["CorrelationId"]);
        Assert.Matches("\\A[0-9a-f]{32}\\z", correlation);
        Assert.True(Guid.TryParseExact(correlation, "N", out var guid));
        Assert.NotEqual(Guid.Empty, guid);
        Assert.Equal($"Privacy excluded; schema 1 route {route} service {service} policy {policy} version {version} reason {reason} rule evaluation not_evaluated correlation {correlation}.", item.Rendered);
    }

    private sealed record Event(LogLevel Level, EventId Id, Dictionary<string, object?> State, Exception? Exception, string Rendered);
    private sealed class Logs : ILogger<PanResponseTransform>, ILoggerProvider
    {
        public string Behavior { get; init; } = "normal";
        public int EnabledCalls, LogCalls;
        public ConcurrentQueue<Event> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel)
        {
            Interlocked.Increment(ref EnabledCalls);
            if (Behavior == "enabled-error") throw new InvalidOperationException("logger_marker");
            if (Behavior == "enabled-cancel") throw new OperationCanceledException("logger_marker");
            return Behavior != "disabled";
        }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 4) return;
            Interlocked.Increment(ref LogCalls);
            if (Behavior == "log-error") throw new InvalidOperationException("logger_marker");
            if (Behavior == "log-cancel") throw new OperationCanceledException("logger_marker");
            Events.Enqueue(new(logLevel, eventId, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(x => x.Key, x => x.Value), exception, formatter(state, exception)));
        }
        public void Dispose() { }
    }

    private sealed class UnreadableContent : HttpContent
    {
        public int ReadAttempts;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { ReadAttempts++; throw new InvalidOperationException("body_marker"); }
        protected override Task<Stream> CreateContentReadStreamAsync()
        { ReadAttempts++; throw new InvalidOperationException("body_marker"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class NoInspectionClock : TimeProvider
    {
        public override long GetTimestamp() => throw new InvalidOperationException("Exclusion started inspection");
    }

    private sealed class Metrics : IDisposable
    {
        private readonly MeterListener listener = new();
        public List<(string Name, long Value, Dictionary<string, object?> Tags)> Values { get; } = [];
        public Metrics(Exception? failure = null)
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == PrivacyTelemetry.MeterName && instrument is not ObservableGauge<int> && instrument is not ObservableGauge<long>) l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((i, value, tags, _) =>
            {
                Values.Add((i.Name, value, tags.ToArray().ToDictionary(x => x.Key, x => x.Value)));
                if (failure is not null) throw failure;
            });
            listener.SetMeasurementEventCallback<double>((i, value, tags, _) => Assert.Fail("Exclusion emitted " + i.Name));
            listener.Start();
        }
        public void Dispose() => listener.Dispose();
    }
}
