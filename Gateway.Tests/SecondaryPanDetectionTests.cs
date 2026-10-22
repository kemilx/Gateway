using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

[CollectionDefinition("Secondary PAN telemetry isolation", DisableParallelization = true)]
public sealed class SecondaryPanTelemetryCollection;

// Other collections install process-wide throwing MeterListeners. Policy IDs filter
// unrelated measurements but cannot prevent those callbacks from interrupting dispatch.
[Collection("Secondary PAN telemetry isolation")]
public sealed class SecondaryPanDetectionTests
{
    private const string Pan = "4111111111111111";
    private const string Mask = "************1111";
    private const string Key = "PanPolicies:Policies:0:SecondaryPanDetection";
    private const string InspectionError = "{\"code\":\"privacy_inspection_failed\"}";
    private const string BlockError = "{\"code\":\"privacy_response_blocked\"}";
    private static CompiledPanPolicy Policy() => new("secondary-" + Guid.NewGuid().ToString("N"), "v1", "protected-payment", ["explicit"], 4096)
        { SecondaryPanDetection = true };

    [Theory]
    [InlineData("000000000000", false)]
    [InlineData("0000000000000", true)]
    [InlineData("0000000000000000000", true)]
    [InlineData("00000000000000000000", false)]
    [InlineData("4111111111111111", true)]
    [InlineData("4111111111111112", false)]
    [InlineData("4222222222222", true)]
    [InlineData("4222222222223", false)]
    [InlineData("0000000000000000001", false)]
    [InlineData("4 1-1 1-1 1-1 1-1 1-1 1-1 1-1 1", true)]
    [InlineData("0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0", true)]
    [InlineData("0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0 ", false)]
    [InlineData(" 4111111111111111", false)]
    [InlineData("4111111111111111 ", false)]
    [InlineData("-4111111111111111", false)]
    [InlineData("4111111111111111-", false)]
    [InlineData("4111--111111111111", false)]
    [InlineData("4111  111111111111", false)]
    [InlineData("4111 -111111111111", false)]
    [InlineData("4111\t111111111111", false)]
    [InlineData("4111\u00a0111111111111", false)]
    [InlineData("４１１１１１１１１１１１１１１１", false)]
    [InlineData("٤١١١١١١١١١١١١١١١", false)]
    [InlineData("PAN 4111111111111111", false)]
    [InlineData("4111111111111111 suffix", false)]
    [InlineData("", false)]
    public void Whole_value_ASCII_format_length_and_Luhn_corpus(string value, bool expected)
        => Assert.Equal(expected, SecondaryPanDetector.IsMatch(value));

    [Theory]
    [InlineData("\"4111111111111111\"", "\"************1111\"", 1)]
    [InlineData("[\"4111111111111111\",[\"4222222222222\",null,42,true]]", "[\"************1111\",[\"*********2222\",null,42,true]]", 2)]
    [InlineData("{\"4111111111111111\":\"safe\",\"a\":\"4111111111111111\",\"a\":\"4111111111111111\",\"nested\":{\"v\":\"\\u0034111111111111111\"}}", "{\"4111111111111111\":\"safe\",\"a\":\"************1111\",\"a\":\"************1111\",\"nested\":{\"v\":\"************1111\"}}", 3)]
    [InlineData("\"4 1-1 1-1 1-1 1-1 1-1 1-1 1-1 1\"", "\"* *-* *-* *-* *-* *-* *-1 1-1 1\"", 1)]
    [InlineData("\"0000000000000000000\"", "\"***************0000\"", 1)]
    [InlineData("\"4111\\u00201111\\u002d1111 1111\"", "\"**** ****-**** 1111\"", 1)]
    public async Task All_original_string_locations_duplicates_escapes_and_exact_masks(string body, string expected, long masks)
    {
        using var f = new Fixture(body, Policy());
        await f.Run();
        Assert.Equal(expected, f.Text);
        f.Completed(masks);
    }

    [Theory]
    [InlineData("PAN", "4111111111111111", "************1111")]
    [InlineData("Email", "4111111111111111", "[REDACTED]")]
    [InlineData("Phone", "4111111111111111", "[REDACTED]")]
    [InlineData("Phone", "4222222222222", "*********2222")]
    [InlineData("PAN", "4111111111111112", "************1112")]
    [InlineData("Email", "4111111111111112", "[REDACTED]")]
    [InlineData("Phone", "4111111111111112", "[REDACTED]")]
    [InlineData("Phone", "4222222222223", "*********2223")]
    public async Task Explicit_primary_and_additional_array_rules_win_once_even_when_Luhn_negative(string type, string value, string expected)
    {
        var body = JsonSerializer.Serialize(new { explicitValue = value, a = new[] { value, value }, other = Pan });
        var policy = Policy() with { Segments = ["explicitValue"], Transformation = type, AdditionalRules = [new(["a", "[*]"], type)] };
        using var f = new Fixture(body, policy);
        await f.Run();
        using var json = JsonDocument.Parse(f.Text);
        Assert.Equal(expected, json.RootElement.GetProperty("explicitValue").GetString());
        Assert.All(json.RootElement.GetProperty("a").EnumerateArray(), e => Assert.Equal(expected, e.GetString()));
        Assert.Equal(Mask, json.RootElement.GetProperty("other").GetString());
        f.Completed(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Optout_is_byte_equivalent_to_legacy_for_single_and_multiple_rules(bool multiple)
    {
        const string body = " {\"explicit\":\"4111111111111112\",\"other\":\"4111111111111111\",\"e\":\"a@x\"} \r\n";
        var policy = Policy() with { SecondaryPanDetection = false, AdditionalRules = multiple ? [new(["e"], "Email")] : [] };
        using var first = new Fixture(body, policy);
        using var second = new Fixture(body, new(policy.PolicyId, policy.Version, policy.RouteId, policy.Segments, policy.MaxBodyBytes) { AdditionalRules = policy.AdditionalRules });
        await first.Run(); await second.Run();
        Assert.Equal(second.Text, first.Text);
        Assert.Contains(Pan, first.Text);
        first.Completed(multiple ? 2 : 1); second.Completed(multiple ? 2 : 1);
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    public async Task No_match_preserves_exact_bytes_and_property_names_are_not_candidates(string mode)
    {
        const string body = " { \"4111111111111111\":\"4111111111111112\", \"x\":[null,42,\"PAN 4111111111111111\",\"é\"] }\r\n";
        using var f = new Fixture(body, Policy() with { Mode = mode });
        await f.Run(); Assert.Equal(body, f.Text); f.Completed(0);
    }

    [Theory]
    [InlineData("late-match")]
    [InlineData("late-invalid-json")]
    [InlineData("late-number")]
    [InlineData("late-bool")]
    [InlineData("late-object")]
    [InlineData("late-array")]
    [InlineData("explicit-negative")]
    public async Task Block_finishes_validation_and_late_errors_override_secondary_match(string scenario)
    {
        var body = scenario switch
        {
            "late-match" => "{\"safe\":\"no\",\"a\":[null,\"4111111111111111\"]}",
            "late-invalid-json" => "{\"x\":\"4111111111111111\",\"tail\":INVALID}",
            "explicit-negative" => "{\"explicit\":\"4111111111111112\"}",
            _ => "{\"x\":\"4111111111111111\",\"explicit\":" + (scenario == "late-number" ? "42" : scenario == "late-bool" ? "true" : scenario == "late-object" ? "{}" : "[]") + "}"
        };
        using var f = new Fixture(body, Policy() with { Mode = "Block" });
        await f.Run();
        var failure = scenario is not ("late-match" or "explicit-negative");
        Assert.Equal(failure ? InspectionError : BlockError, f.Text);
        Assert.Equal(502, f.Context.HttpContext.Response.StatusCode);
        Assert.False(f.Context.HttpContext.Response.Headers.ContainsKey("X-Upstream-Marker"));
        Assert.Equal(failure ? 1 : 0, f.Telemetry.DashboardMetrics.Outcomes.Blocked);
        Assert.Equal(failure ? 0 : 1, f.Telemetry.DashboardMetrics.Outcomes.PolicyBlocked);
        f.Completed(0);
    }

    [Theory]
    [InlineData("PAN")]
    [InlineData("Email")]
    [InlineData("Phone")]
    public async Task Explicit_Block_ignores_Luhn_and_formatter_validity_for_all_transformations(string transformation)
    {
        using var f = new Fixture("{\"a\":[\"4111111111111112\"]}", Policy() with
        { Mode = "Block", AdditionalRules = [new(["a", "[*]"], transformation)] });
        await f.Run(); Assert.Equal(BlockError, f.Text); f.Completed(0);
    }

    [Fact]
    public async Task Mask_late_invalid_terminal_discards_secondary_substitution_and_counts_zero()
    {
        using var f = new Fixture("{\"other\":\"4111111111111111\",\"explicit\":42}", Policy());
        await f.Run(); Assert.Equal(InspectionError, f.Text); f.Completed(0);
    }

    [Theory]
    [InlineData("Mask", false)]
    [InlineData("Block", false)]
    [InlineData("Mask", true)]
    [InlineData("Block", true)]
    public async Task Deadline_or_cancel_after_secondary_candidate_discards_output_masks_and_releases_lease(string mode, bool cancel)
    {
        using var f = new Fixture("[\"4111111111111111\",\"tail\"]", Policy() with { Mode = mode });
        var checkpoints = 0;
        var triggered = false;
        f.Clock.OnTimestamp = () =>
        {
            if (triggered || !new System.Diagnostics.StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == "WriteMultiple")) return;
            // root entry, item checkpoint, string entry, post-detection, post-candidate.
            if (++checkpoints != 5) return;
            triggered = true;
            if (cancel) f.Caller.Cancel(); else f.Clock.Advance();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run);
        Assert.True(triggered);
        Assert.Empty(f.Text);
        Assert.Null(f.Context.HttpContext.Response.ContentLength);
        Assert.Equal(cancel ? 1 : 0, f.Telemetry.DashboardMetrics.Outcomes.Canceled);
        Assert.Equal(cancel ? 0 : 1, f.Telemetry.DashboardMetrics.Outcomes.TimedOut);
        f.Completed(0);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    [InlineData("False", false)]
    public async Task Effective_boolean_is_optional_case_insensitive_and_catalog_is_frozen(string? value, bool expected)
    {
        var values = PolicyConfiguration.Defaults();
        if (value is not null) values[Key] = value;
        await using var app = Build(values);
        var catalog = app.Services.GetRequiredService<PanPolicyCatalog>();
        var compiled = catalog.Find("protected-payment")!;
        Assert.Equal(expected, compiled.SecondaryPanDetection);
        app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value.Policies[0].SecondaryPanDetection = !expected;
        app.Configuration[Key] = (!expected).ToString();
        ((IConfigurationRoot)app.Configuration).Reload();
        Assert.Same(compiled, catalog.Find("protected-payment"));
        Assert.Equal(expected, compiled.SecondaryPanDetection);
        var (_, _, _, _, bytes) = compiled;
        Assert.Equal(compiled.MaxBodyBytes, bytes);
        Assert.False(new CompiledPanPolicy("id", "v1", "route", ["x"], 64).SecondaryPanDetection);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("number")]
    [InlineData("word")]
    [InlineData("padded")]
    [InlineData("children")]
    [InlineData("scalar-and-children")]
    public void Invalid_effective_configuration_fails_closed(string scenario)
    {
        var values = PolicyConfiguration.Defaults();
        values[Key] = scenario switch { "null" => null, "empty" => "", "number" => "1", "word" => "yes", "padded" => " true ", "scalar-and-children" => "true", _ => null };
        if (scenario.Contains("children", StringComparison.Ordinal)) values[Key + ":0"] = "true";
        Assert.ThrowsAny<InvalidOperationException>(() => Build(values));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]
    [InlineData("1", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    [InlineData("{\"child\":true}", null)]
    public async Task Real_JSON_provider_boolean_and_invalid_shapes(string scalar, bool? expected)
    {
        WebApplication Create() => GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
            b.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("{\"PanPolicies\":{\"Policies\":[{\"SecondaryPanDetection\":" + scalar + "}]}}")));
        });
        if (expected is null) { Assert.ThrowsAny<InvalidOperationException>(Create); return; }
        await using var app = Create();
        Assert.Equal(expected.Value, app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!.SecondaryPanDetection);
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    public async Task Real_HTTP_secondary_detection_preserves_contract_and_telemetry_has_no_values(string mode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        const string body = "{\"other\":[\"4111111111111111\"],\"payment\":{\"cardNumber\":null}}";
        api.MapGet("/test", async context =>
        {
            context.Response.ContentType = "application/json";
            context.Response.Headers.ETag = "\"synthetic\"";
            await context.Response.WriteAsync(body, context.RequestAborted);
        });
        await api.StartAsync();
        var values = PolicyConfiguration.Defaults(); values[Key] = "true"; values["PanPolicies:Policies:0:Mode"] = mode;
        values["PanPolicies:Policies:0:PolicyId"] = "http-secondary-" + Guid.NewGuid().ToString("N");
        var logs = new CapturedLogs();
        await using var app = GatewayHost.Build([], Address(api) + "/", b =>
        {
            b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(values);
            b.WebHost.UseUrls("http://127.0.0.1:0"); b.Logging.ClearProviders(); b.Logging.AddProvider(logs);
        });
        var telemetry = app.Services.GetRequiredService<PrivacyTelemetry>();
        var budget = app.Services.GetRequiredService<InspectionBudget>();
        var policyId = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!.PolicyId;
        var measurements = new ConcurrentQueue<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, subscription) => { if (instrument.Meter.Name == PrivacyTelemetry.MeterName) subscription.EnableMeasurementEvents(instrument); };
        void Capture(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        { if (tags.ToArray().Any(t => t.Key == "gateway.policy.id" && Equals(t.Value, policyId))) measurements.Enqueue((instrument.Name, tags.ToArray())); }
        listener.SetMeasurementEventCallback<long>((i, _, t, _) => Capture(i, t));
        listener.SetMeasurementEventCallback<double>((i, _, t, _) => Capture(i, t));
        listener.Start();
        await app.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(app)), Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync("/protected/test");
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(mode == "Mask" ? HttpStatusCode.OK : HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(mode == "Mask" ? body.Replace(Pan, Mask) : BlockError, text);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.Contains("ETag"));
        Assert.Equal(Encoding.UTF8.GetByteCount(text), response.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var delay = 1;
        while (telemetry.DashboardMetrics.Duration.Samples < 1 || budget.Snapshot != (0, 0L))
        { await Task.Delay(delay, timeout.Token); delay = Math.Min(delay * 2, 50); }
        Assert.Equal(mode == "Mask" ? 1 : 0, telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(mode == "Block" ? 1 : 0, telemetry.DashboardMetrics.Outcomes.PolicyBlocked);
        Assert.NotEmpty(measurements);
        Assert.Equal(mode == "Mask" ? 6 : 5, measurements.Count);
        Assert.Equal(new[] { "gateway.privacy.inspection.duration", "gateway.privacy.inspection.input_body_bytes", "gateway.privacy.inspection.outcomes", "gateway.privacy.inspection.output_body_bytes" }
            .Concat(["gateway.privacy.inspection.secondary_pan_matches"])
            .Concat(mode == "Mask" ? ["gateway.privacy.inspection.masks"] : Array.Empty<string>()).Order(), measurements.Select(m => m.Name).Order());
        foreach (var measurement in measurements)
            Assert.Equal(new[] { "gateway.policy.id", "gateway.policy.version", "gateway.privacy.outcome", "gateway.route.id" }, measurement.Tags.Select(t => t.Key).Order());
        var observed = string.Join('\n', logs.Messages) + JsonSerializer.Serialize(measurements.Select(m => m.Tags));
        Assert.Contains("Protected response inspected", observed);
        Assert.DoesNotContain(Pan, observed); Assert.DoesNotContain(Mask, observed); Assert.DoesNotContain("$.payment", observed);
        using var raw = await client.GetAsync("/passthrough/test");
        Assert.Equal(body, await raw.Content.ReadAsStringAsync());
        Assert.NotNull(raw.Headers.ETag);
    }

    private static WebApplication Build(Dictionary<string, string?> values) => GatewayHost.Build([], configure: b =>
    { b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(values); b.WebHost.UseUrls("http://127.0.0.1:0"); });
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private sealed class Fixture : IDisposable
    {
        public readonly MemoryStream Output = new();
        public readonly CancellationTokenSource Caller = new();
        public readonly ManualClock Clock = new();
        public readonly InspectionBudget Budget = new(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = 4096, InspectionTimeoutSeconds = 1 } }));
        public readonly PrivacyTelemetry Telemetry;
        public readonly ResponseTransformContext Context;
        private readonly TrackedStream source;
        private readonly CompiledPanPolicy policy;
        public Fixture(string body, CompiledPanPolicy policy)
        {
            this.policy = policy; Telemetry = new(Budget);
            var http = new DefaultHttpContext(); http.Request.Method = "GET"; http.Response.Body = Output;
            http.Response.Headers["X-Upstream-Marker"] = "synthetic";
            source = new(Encoding.UTF8.GetBytes(body));
            var content = new StreamContent(source); content.Headers.ContentType = new("application/json");
            Context = new() { HttpContext = http, ProxyResponse = new(HttpStatusCode.OK) { Content = content }, HeadersCopied = true, CancellationToken = Caller.Token };
        }
        public string Text => Encoding.UTF8.GetString(Output.ToArray());
        public Task Run() => new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, Telemetry, Clock).ApplyAsync(Context, policy).AsTask();
        public void Completed(long masks)
        {
            Assert.True(Context.SuppressResponseBody); Assert.Equal((0, 0L), Budget.Snapshot);
            Assert.Equal(masks, Telemetry.DashboardMetrics.ValidatedMasks);
            Assert.Equal(1, Telemetry.DashboardMetrics.Duration.Samples);
            Assert.True(source.Disposed);
        }
        public void Dispose() { Context.ProxyResponse!.Dispose(); Telemetry.Dispose(); Caller.Dispose(); Output.Dispose(); }
    }
    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public Action? OnTimestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { OnTimestamp?.Invoke(); return ticks; }
        public void Advance() => ticks += TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        public readonly ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Messages.Enqueue(formatter(state, exception)); }
        public void Dispose() { }
    }
}
