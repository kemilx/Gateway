using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

public sealed class ScalarCopyTests
{
    private static readonly Action<JsonElement, Utf8JsonWriter, InspectionDeadline> Copy =
        typeof(PanResponseTransform).GetMethod("CopyElement", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Action<JsonElement, Utf8JsonWriter, InspectionDeadline>>();

    [Theory]
    [InlineData("unicode")]
    [InlineData("duplicates")]
    [InlineData("numbers")]
    [InlineData("nested")]
    [InlineData("large-name")]
    [InlineData("large-value")]
    [InlineData("depth64")]
    [InlineData("unpaired-name")]
    [InlineData("unpaired-value")]
    [InlineData("unpaired-low-name")]
    [InlineData("unpaired-low-value")]
    public async Task CopyElement_matches_legacy_bytes_or_exception(string scenario)
    {
        var json = scenario switch
        {
            "unicode" => "{\"é😀\\u0001\\\"\\/\\\\\":\"é😀\\u0002\\\"\\/\\\\\"}",
            "duplicates" => "{\"a\":1,\"a\":2,\"\\u0061\":3}",
            "numbers" => "{\"a\":-0,\"b\":1.2300,\"c\":1e+20,\"d\":true,\"e\":null}",
            "nested" => "{\"a\":[],\"b\":{},\"c\":[{},[1,{\"x\":false}]]}",
            "large-name" => "{\"" + new string('n', 1024 * 1024 - 100) + "\":1}",
            "large-value" => "{\"v\":\"" + new string('v', 1024 * 1024 - 100) + "\"}",
            "depth64" => new string('[', 64) + "0" + new string(']', 64),
            "unpaired-name" => "{\"\\uD800\":1}",
            "unpaired-value" => "{\"x\":\"\\uD800\"}",
            "unpaired-low-name" => "{\"\\uDC00\":1}",
            "unpaired-low-value" => "{\"x\":\"\\uDC00\"}",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        await using var deadline = new InspectionDeadline(TimeProvider.System, TimeProvider.System.GetTimestamp(), TimeSpan.FromMinutes(1), new DefaultHttpContext(), default);
        var expected = Render(document.RootElement, (v, w) => LegacyCopy(v, w));
        var actual = Render(document.RootElement, (v, w) => Copy(v, w, deadline));
        if (scenario.StartsWith("unpaired", StringComparison.Ordinal)) Assert.Equal(typeof(InvalidOperationException), expected.Error);
        else Assert.Null(expected.Error);
        Assert.Equal(expected.Error, actual.Error);
        if (expected.Error is null) Assert.Equal(expected.Bytes, actual.Bytes);
    }

    [Fact]
    public void Depth65_is_rejected_by_document_parser()
    {
        var json = new string('[', 65) + "0" + new string(']', 65);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 }));
    }

    private static (byte[] Bytes, Type? Error) Render(JsonElement element, Action<JsonElement, Utf8JsonWriter> copy)
    {
        using var output = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(output)) copy(element, writer);
            return (output.ToArray(), null);
        }
        catch (Exception error) { return ([], error.GetType()); }
    }

    private static void LegacyCopy(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                LegacyCopy(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var element in value.EnumerateArray()) LegacyCopy(element, writer);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Observe")]
    [InlineData("Block")]
    public async Task Selected_pan_modes_preserve_or_block_unselected_scalar_structure(string mode)
    {
        const string body = "{ \"pan\":\"4111111111111111\",\"other\":{\"a\":1.00,\"\\u0061\":\"é\",\"a\":false,\"array\":[null,{}]} }";
        using var fixture = new Fixture(Encoding.UTF8.GetBytes(body), Policy() with { Mode = mode });
        await fixture.Run();
        var expected = mode switch
        {
            "Mask" => "{\"pan\":\"************1111\",\"other\":{\"a\":1.00,\"a\":\"\\u00E9\",\"a\":false,\"array\":[null,{}]}}",
            "Observe" => body,
            _ => "{\"code\":\"privacy_response_blocked\"}"
        };
        Assert.Equal(expected, Encoding.UTF8.GetString(fixture.Output.ToArray()));
        Assert.Equal(mode == "Block" ? 502 : 200, fixture.Http.Response.StatusCode);
        fixture.AssertTerminal(mode == "Block" ? "policy_blocked" : "inspected", mode == "Mask" ? 1 : 0, 1, 0, "4111111111111111", "other", "é");
        Assert.Equal((0, 0L), fixture.Budget.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_rules_and_secondary_detection_keep_unselected_scalars(bool secondary)
    {
        const string body = "{\"pan\":\"4111111111111111\",\"email\":\"a@x\",\"extra\":\"5555555555554444\",\"other\":{\"a\":1.00,\"a\":true,\"s\":\"ordinary\"}}";
        var policy = Policy() with { AdditionalRules = [new(["email"], "Email")], SecondaryPanDetection = secondary };
        using var fixture = new Fixture(Encoding.UTF8.GetBytes(body), policy);
        await fixture.Run();
        Assert.Equal("{\"pan\":\"************1111\",\"email\":\"***@x\",\"extra\":\"" + (secondary ? "************4444" : "5555555555554444") + "\",\"other\":{\"a\":1.00,\"a\":true,\"s\":\"ordinary\"}}", Encoding.UTF8.GetString(fixture.Output.ToArray()));
        Assert.Equal(200, fixture.Http.Response.StatusCode);
        fixture.AssertTerminal("inspected", secondary ? 3 : 2, 2, secondary ? 1 : 0, "4111111111111111", "5555555555554444", "a@x", "ordinary", "other");
        Assert.Equal((0, 0L), fixture.Budget.Snapshot);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("value")]
    [InlineData("truncated")]
    [InlineData("overlimit")]
    public async Task Invalid_unselected_data_fails_closed_without_upstream_bytes(string scenario)
    {
        var body = scenario switch
        {
            "name" => Encoding.UTF8.GetBytes("{\"pan\":\"4111111111111111\",\"x\":{\"").Concat(new byte[] { 0xff }).Concat("\":1}}"u8.ToArray()).ToArray(),
            "value" => Encoding.UTF8.GetBytes("{\"pan\":\"4111111111111111\",\"x\":\"").Concat(new byte[] { 0xff }).Concat("\"}"u8.ToArray()).ToArray(),
            "truncated" => "{\"pan\":\"4111111111111111\",\"x\":"u8.ToArray(),
            _ => Encoding.UTF8.GetBytes("{\"pan\":\"4111111111111111\",\"x\":\"" + new string('x', 5000) + "\"}")
        };
        using var fixture = new Fixture(body, Policy());
        await fixture.Run();
        Assert.Equal(502, fixture.Http.Response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", Encoding.UTF8.GetString(fixture.Output.ToArray()));
        Assert.Equal((0, 0L), fixture.Budget.Snapshot);
    }

    [Theory]
    [InlineData("high-name", "Mask")]
    [InlineData("high-name", "Observe")]
    [InlineData("high-name", "Block")]
    [InlineData("low-name", "Mask")]
    [InlineData("low-name", "Observe")]
    [InlineData("low-name", "Block")]
    [InlineData("high-value", "Mask")]
    [InlineData("high-value", "Observe")]
    [InlineData("high-value", "Block")]
    [InlineData("low-value", "Mask")]
    [InlineData("low-value", "Observe")]
    [InlineData("low-value", "Block")]
    public async Task Unpaired_surrogates_in_unselected_subtree_pin_baseline(string scenario, string mode)
    {
        var escape = scenario.StartsWith("high", StringComparison.Ordinal) ? "\\uD800" : "\\uDC00";
        var isName = scenario.EndsWith("name", StringComparison.Ordinal);
        var body = "{\"pan\":\"4111111111111111\",\"other\":{" + (isName ? "\"" + escape + "\":1" : "\"x\":\"" + escape + "\"") + "}}";
        using var fixture = new Fixture(Encoding.UTF8.GetBytes(body), Policy() with { Mode = mode });
        await fixture.Run();
        Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", Encoding.UTF8.GetString(fixture.Output.ToArray()));
        Assert.Equal(502, fixture.Http.Response.StatusCode);
        fixture.AssertTerminal("blocked", 0, 0, 0, "4111111111111111", "other", escape);
        Assert.Equal((0, 0L), fixture.Budget.Snapshot);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public async Task Large_scalar_pair_checks_cancellation_before_and_after_value(bool expire, int checkpoint)
    {
        var name = new string('n', 100_000);
        var value = new string('v', 100_000);
        using var doc = JsonDocument.Parse("{\"" + name + "\":\"" + value + "\"}");
        using var cancellation = new CancellationTokenSource();
        var clock = new PairClock(expire, checkpoint, cancellation);
        await using var deadline = new InspectionDeadline(clock, 0, TimeSpan.FromSeconds(1), new DefaultHttpContext(), cancellation.Token);
        clock.Armed = true;
        using var output = new MemoryStream();
        using var writer = new Utf8JsonWriter(output);
        try { Assert.ThrowsAny<OperationCanceledException>(() => Copy(doc.RootElement, writer, deadline)); }
        finally { writer.Flush(); }
        Assert.Equal(checkpoint, clock.Checks);
        var actual = Encoding.UTF8.GetString(output.ToArray());
        if (checkpoint == 3)
            Assert.True(actual == "{" || actual == "{\"" + name + "\":");
        else
            Assert.Equal("{\"" + name + "\":\"" + value + "\"", actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nested_container_traversal_checks_before_scalar(bool expire)
    {
        using var doc = JsonDocument.Parse("{\"nested\":{\"value\":\"secret\"}}");
        using var cancellation = new CancellationTokenSource();
        var clock = new PairClock(expire, 5, cancellation);
        await using var deadline = new InspectionDeadline(clock, 0, TimeSpan.FromSeconds(1), new DefaultHttpContext(), cancellation.Token);
        clock.Armed = true;
        using var output = new MemoryStream();
        using var writer = new Utf8JsonWriter(output);
        try { Assert.ThrowsAny<OperationCanceledException>(() => Copy(doc.RootElement, writer, deadline)); }
        finally { writer.Flush(); }
        Assert.Equal(5, clock.Checks);
        var actual = Encoding.UTF8.GetString(output.ToArray());
        Assert.True(actual == "{\"nested\":{" || actual == "{\"nested\":{\"value\":");
    }

    private sealed class PairClock(bool expire, int checkpoint, CancellationTokenSource cancellation) : TimeProvider
    {
        public bool Armed;
        public int Checks;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp()
        {
            if (!Armed) return 0;
            var current = ++Checks;
            if (!expire && current == checkpoint) cancellation.Cancel();
            return expire && current >= checkpoint ? 1 : 0;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new SilentTimer();
    }

    private sealed class SilentTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Traversal_cancellation_or_expiry_discards_output_and_releases_lease(bool expire)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new TraversalClock(expire, cancellation);
        var body = "{\"pan\":\"4111111111111111\",\"other\":{" +
            string.Join(',', Enumerable.Range(0, 300).Select(i => $"\"p{i}\":\"value\"")) + "}}";
        using var fixture = new Fixture(Encoding.UTF8.GetBytes(body), Policy() with { MaxBodyBytes = 16384 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run(clock, cancellation.Token));
        Assert.Empty(fixture.Output.ToArray());
        Assert.Equal((0, 0L), fixture.Budget.Snapshot);
        fixture.AssertTerminal(expire ? "timed_out" : "canceled", 0, 0, 0, "4111111111111111", "value");
    }

    private sealed class TraversalClock(bool expire, CancellationTokenSource cancellation) : TimeProvider
    {
        private int observations;
        public override long TimestampFrequency => 100;
        public override long GetTimestamp()
        {
            var current = ++observations;
            if (!expire && current == 100) cancellation.Cancel();
            return expire ? current : 0;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new SilentTimer();
        private sealed class SilentTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static CompiledPanPolicy Policy() => new("scalar", "v1", "protected-payment", ["pan"], 4096);

    private sealed class Fixture : IDisposable
    {
        public readonly MemoryStream Output = new();
        public readonly DefaultHttpContext Http = new();
        public readonly InspectionBudget Budget;
        public readonly PrivacyInspectionEventTests.Logs Logs = new();
        private readonly PrivacyTelemetry telemetry;
        private readonly HttpResponseMessage upstream;
        private readonly CompiledPanPolicy policy;
        public Fixture(byte[] body, CompiledPanPolicy policy)
        {
            this.policy = policy;
            var options = new ConfigurationBuilder().AddInMemoryCollection(PolicyConfiguration.Defaults()).Build()
                .GetSection(PanPolicyOptions.SectionName).Get<PanPolicyOptions>()!;
            options.InspectionBudget!.InspectionTimeoutSeconds = 1;
            Budget = new InspectionBudget(Options.Create(options));
            telemetry = new PrivacyTelemetry(Budget);
            Http.Response.Body = Output;
            upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            upstream.Content.Headers.ContentType = new("application/json");
        }
        public Task Run(TimeProvider? clock = null, CancellationToken token = default) =>
            new PanResponseTransform(Logs, telemetry, clock ?? TimeProvider.System)
            .ApplyAsync(new ResponseTransformContext { HttpContext = Http, ProxyResponse = upstream, CancellationToken = token }, policy).AsTask();
        public void AssertTerminal(string outcome, long masks, long explicitMatches, long secondary, params string[] canaries)
        {
            var item = Assert.Single(Logs.Events);
            Assert.Equal(5, item.Id.Id);
            Assert.Null(item.Exception);
            Assert.Equal(outcome, item.State["Outcome"]);
            Assert.Equal(masks, Assert.IsType<long>(item.State["ValidatedMasks"]));
            Assert.Equal(explicitMatches, Assert.IsType<long>(item.State["ValidatedExplicitSensitiveMatches"]));
            Assert.Equal(secondary, Assert.IsType<long>(item.State["ValidatedSecondaryPanMatches"]));
            var text = item.Rendered + string.Join("|", item.State.Select(pair => pair.Key + "=" + pair.Value));
            foreach (var canary in canaries) Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
        }
        public void Dispose() { upstream.Dispose(); telemetry.Dispose(); Logs.Dispose(); Output.Dispose(); }
    }
}
