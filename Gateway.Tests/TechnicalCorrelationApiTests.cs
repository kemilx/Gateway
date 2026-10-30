using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using TestApi;
using Xunit;

namespace Gateway.Tests;

[Collection("Secondary PAN telemetry isolation")]
public sealed class TechnicalCorrelationApiTests
{
    private const string Header = "X-Gateway-Correlation-Id";
    private const string Id = "0123456789abcdef0123456789abcdef";
    private const string Category = "TestApi.SyntheticApi";
    private const string Template = "Synthetic request received; correlation {CorrelationId}.";

    [Theory]
    [InlineData(null)] [InlineData("false")]
    public async Task Synthetic_observer_is_disabled_by_default(string? enabled)
    {
        await using var f = await ApiHost.Start(enabled);
        using var request = Request([Id]); using var response = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SyntheticApi.Sample, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(f.Logs.Events, e => e.Category == Category); AssertNoEcho(response);
    }

    [Theory]
    [InlineData("valid")] [InlineData("missing")] [InlineData("duplicate")]
    [InlineData("comma")] [InlineData("uppercase")] [InlineData("zero")]
    [InlineData("hyphens")] [InlineData("short")] [InlineData("long")]
    [InlineData("nonhex")] [InlineData("empty")] [InlineData("leading-space")]
    [InlineData("trailing-space")] [InlineData("control")]
    public async Task Synthetic_observer_accepts_only_one_safe_id(string scenario)
    {
        string[]? values = scenario switch
        {
            "valid" => [Id], "missing" => null, "duplicate" => [Id, Id], "comma" => [Id + "," + Id],
            "uppercase" => [Id.ToUpperInvariant()], "zero" => [new('0', 32)],
            "hyphens" => ["01234567-89ab-cdef-0123-456789abcdef"], "short" => [Id[..31]],
            "long" => [Id + "0"], "nonhex" => ["g123456789abcdef0123456789abcdef"],
            "empty" => [""], "leading-space" => [" " + Id], "trailing-space" => [Id + " "], _ => [Id[..31] + "\n"]
        };
        // HTTP parsing trims OWS and rejects controls. Inject only these parsed-header
        // edge cases before the observer, without pretending they traversed the wire.
        var raw = scenario is "leading-space" or "trailing-space" or "control";
        await using var f = await ApiHost.Start("true", rawValues: raw ? values : null);
        using var activity = new Activity("activity_marker").Start();
        activity.AddBaggage("baggage_marker", "value_marker"); activity.AddTag("tag_marker", "value_marker");
        using var request = Request(raw ? null : values); using var response = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SyntheticApi.Sample, await response.Content.ReadAsStringAsync()); AssertNoEcho(response);
        if (scenario == "valid") AssertObservation(Assert.Single(f.Logs.Events), Id);
        else Assert.Empty(f.Logs.Events);
        Assert.Equal(0, f.Logs.ScopeCalls);
    }

    [Theory]
    [InlineData("is-enabled-error")] [InlineData("is-enabled-cancel")]
    [InlineData("log-error")] [InlineData("log-cancel")]
    public async Task Synthetic_observer_failure_does_not_change_response(string behavior)
    {
        await using var f = await ApiHost.Start("true", behavior);
        using var request = Request([Id]); using var response = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SyntheticApi.Sample, await response.Content.ReadAsStringAsync()); AssertNoEcho(response);
        Assert.True(f.Logs.ObserverAttempts > 0); Assert.Empty(f.Logs.Events);
        Assert.Equal("\"raw-v1\"", response.Headers.ETag!.ToString());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Synthetic_observer_uses_startup_snapshot(bool enabled)
    {
        await using var f = await ApiHost.Start(enabled ? "true" : "false");
        f.App.Configuration["TechnicalCorrelation:Enabled"] = enabled ? "false" : "true";
        ((IConfigurationRoot)f.App.Configuration).Reload();
        using var request = Request([Id]); using var response = await f.Client.SendAsync(request);
        Assert.Equal(SyntheticApi.Sample, await response.Content.ReadAsStringAsync());
        var observed = f.Logs.Events.Where(e => e.Category == Category).ToArray();
        if (enabled) AssertObservation(Assert.Single(observed), Id); else Assert.Empty(observed);
    }

    [Theory]
    [InlineData("Mask")] [InlineData("Block")] [InlineData("Observe")]
    [InlineData("HEAD")] [InlineData("nonJSON")] [InlineData("204")]
    [InlineData("304")] [InlineData("passthrough")]
    public async Task Real_gateway_api_and_privacy_events_share_id(string scenario)
    {
        await using var api = await ApiHost.Start("true", scenario: scenario);
        var config = PolicyConfiguration.Defaults(); config["TechnicalCorrelation:Enabled"] = "true";
        config["PanPolicies:Policies:0:Mode"] = scenario is "Block" or "Observe" ? scenario : "Mask";
        var gatewayLogs = new Logs();
        await using var gateway = GatewayHost.Build([], Address(api.App) + "/", b =>
        {
            b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(config);
            b.WebHost.UseUrls("http://127.0.0.1:0"); b.Logging.ClearProviders(); b.Logging.AddProvider(gatewayLogs);
        });
        await gateway.StartAsync();
        using var client = Client(gateway);
        var excluded = scenario is "HEAD" or "nonJSON" or "204" or "304";
        using var request = new HttpRequestMessage(scenario == "HEAD" ? HttpMethod.Head : HttpMethod.Get,
            scenario == "passthrough" ? "/passthrough/payment?query_marker=value_marker" : excluded ? "/protected/excluded?query_marker=value_marker" : "/protected/payment?query_marker=value_marker");
        request.Headers.TryAddWithoutValidation(Header, new[] { "spoof_marker", Id });
        request.Headers.TryAddWithoutValidation("X-Private", "header_marker");
        using var response = await client.SendAsync(request); var text = await response.Content.ReadAsStringAsync();
        AssertNoEcho(response);
        Assert.Equal(scenario == "Block" ? 502 : scenario == "204" ? 204 : scenario == "304" ? 304 : 200, (int)response.StatusCode);
        Assert.Equal(scenario == "Block" ? "{\"code\":\"privacy_response_blocked\"}" : scenario is "HEAD" or "204" or "304" ? "" : scenario == "nonJSON" ? "body_marker" : scenario is "Observe" or "passthrough" ? SyntheticApi.Sample : "{\"payment\":{\"cardNumber\":\"************1111\",\"currency\":\"EUR\"},\"amount\":12.50,\"note\":\"synthetic-\\u00F1-\\uD83D\\uDE00\",\"items\":[true,null,7]}", text);
        var observation = Assert.Single(api.Logs.Events); var id = Assert.IsType<string>(observation.State["CorrelationId"]);
        AssertObservation(observation, id); Assert.NotEqual(Id, id);
        var expectedCount = scenario == "passthrough" ? 0 : excluded ? 1 : 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (gatewayLogs.Events.Count(e => e.Id.Id is 4 or 5 or 6) < expectedCount) await Task.Delay(10, timeout.Token);
        var privacy = gatewayLogs.Events.Where(e => e.Id.Id is 4 or 5 or 6).ToArray();
        Assert.Equal(expectedCount, privacy.Length);
        Assert.All(privacy, e => { Assert.Equal(id, e.State["CorrelationId"]); Assert.Equal(1, e.State["SchemaVersion"]); Assert.Null(e.Exception); });
        Assert.Equal(scenario == "passthrough" ? Array.Empty<int>() : excluded ? new[] { 4 } : new[] { 5, 6 }, privacy.Select(e => e.Id.Id).Order());
        if (excluded) Assert.Equal(scenario == "HEAD" ? "excluded_head" : scenario == "nonJSON" ? "excluded_media_type" : "excluded_bodyless", Assert.Single(privacy).State["Reason"]);
    }

    private static HttpRequestMessage Request(string[]? values)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/payment?query_marker=value_marker");
        if (values is not null) request.Headers.TryAddWithoutValidation(Header, values);
        request.Headers.TryAddWithoutValidation("X-Private", "header_marker");
        return request;
    }
    private static void AssertObservation(Entry entry, string id)
    {
        Assert.Matches("\\A[0-9a-f]{32}\\z", id); Assert.NotEqual(new string('0', 32), id);
        Assert.Equal(Category, entry.Category); Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(new EventId(1, "TechnicalCorrelationObserved"), entry.Id); Assert.Null(entry.Exception);
        Assert.Equal("TechnicalCorrelationObserved", entry.Id.Name);
        Assert.Equal(new[] { "CorrelationId", "{OriginalFormat}" }, entry.State.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(id, entry.State["CorrelationId"]); Assert.Equal(Template, entry.State["{OriginalFormat}"]);
        Assert.Equal($"Synthetic request received; correlation {id}.", entry.Rendered);
    }
    private static void AssertNoEcho(HttpResponseMessage response)
    {
        Assert.False(response.Headers.Contains(Header)); Assert.False(response.Content.Headers.Contains(Header));
        Assert.False(response.TrailingHeaders.Contains(Header));
    }
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static HttpClient Client(WebApplication app) => new(new SocketsHttpHandler { UseProxy = false, ActivityHeadersPropagator = null })
        { BaseAddress = new(Address(app)), Timeout = TimeSpan.FromSeconds(10) };

    private sealed record Entry(string Category, LogLevel Level, EventId Id, Dictionary<string, object?> State, Exception? Exception, string Rendered);
    private sealed class Logs(string behavior = "normal") : ILoggerProvider
    {
        public readonly ConcurrentQueue<Entry> Events = new(); public int ObserverAttempts, ScopeCalls;
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName, behavior);
        public void Dispose() { }
        private sealed class CaptureLogger(Logs owner, string category, string behavior) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull { Interlocked.Increment(ref owner.ScopeCalls); return null; }
            public bool IsEnabled(LogLevel level)
            {
                if (category != Category) return true;
                Interlocked.Increment(ref owner.ObserverAttempts);
                if (behavior == "is-enabled-error") throw new InvalidOperationException("logger_marker");
                if (behavior == "is-enabled-cancel") throw new OperationCanceledException("logger_marker");
                return true;
            }
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == Category)
                {
                    Interlocked.Increment(ref owner.ObserverAttempts);
                    if (behavior == "log-error") throw new InvalidOperationException("logger_marker");
                    if (behavior == "log-cancel") throw new OperationCanceledException("logger_marker");
                }
                owner.Events.Enqueue(new(category, level, id, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(p => p.Key, p => p.Value), exception, formatter(state, exception)));
            }
        }
    }
    private sealed class RawHeaderFilter(string[] values) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, onward) => { context.Request.Headers[Header] = new StringValues(values); await onward(context); });
            next(app);
        };
    }
    private sealed class ApiHost(WebApplication app, HttpClient client, Logs logs) : IAsyncDisposable
    {
        public WebApplication App => app; public HttpClient Client => client; public Logs Logs => logs;
        public static async Task<ApiHost> Start(string? enabled, string behavior = "normal", string[]? rawValues = null, string scenario = "Mask")
        {
            var logs = new Logs(behavior);
            var app = SyntheticApi.Build([], b =>
            {
                b.Configuration.Sources.Clear();
                b.Configuration.AddInMemoryCollection(enabled is null ? [] : new Dictionary<string, string?> { ["TechnicalCorrelation:Enabled"] = enabled });
                b.WebHost.UseUrls("http://127.0.0.1:0"); b.Logging.ClearProviders(); b.Logging.AddProvider(logs);
                if (rawValues is not null) b.Services.AddSingleton<IStartupFilter>(new RawHeaderFilter(rawValues));
            }, endpoints => endpoints.MapMethods("/excluded", ["GET", "HEAD"], async c =>
            {
                c.Response.StatusCode = scenario == "204" ? 204 : scenario == "304" ? 304 : 200;
                c.Response.ContentType = "text/plain";
                if (scenario is not ("HEAD" or "204" or "304")) await c.Response.WriteAsync("body_marker");
            }));
            try { await app.StartAsync(); return new(app, TechnicalCorrelationApiTests.Client(app), logs); }
            catch { await app.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }
}
