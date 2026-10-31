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
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

[Collection("Secondary PAN telemetry isolation")]
public sealed class ObservePolicyTests
{
    private const string Prefix = "gateway.privacy.inspection.";
    private const string Pan = "4111111111111111";
    private const string Body = " { \"explicit\" : \"4111111111111111\", \"other\" : \"4111111111111111\" }\r\n";
    private const string Error = "{\"code\":\"privacy_inspection_failed\"}";
    private static CompiledPanPolicy Policy() => new("observe-" + Guid.NewGuid().ToString("N"), "v1", "protected-payment", ["explicit"], 4096) { Mode = "Observe", SecondaryPanDetection = true };

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("observe")] [InlineData("OBSERVE")]
    [InlineData(" Observe")] [InlineData("Observe ")] [InlineData("Observe\n")]
    [InlineData("0")] [InlineData("3")] [InlineData("Unknown")]
    public void Mode_requires_exact_Observe_string(string? mode)
    {
        var values = PolicyConfiguration.Defaults(); values["PanPolicies:Policies:0:Mode"] = mode;
        var ex = Assert.Throws<InvalidOperationException>(() => GatewayHost.Build([], configure: b =>
        { b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(values); }));
        Assert.IsType<OptionsValidationException>(ex.InnerException);
    }

    [Theory]
    [InlineData("PAN", false, false)] [InlineData("Email", false, false)] [InlineData("Phone", false, false)]
    [InlineData("PAN", true, false)] [InlineData("Email", true, false)] [InlineData("Phone", true, false)]
    [InlineData("PAN", false, true)] [InlineData("Email", false, true)] [InlineData("Phone", false, true)]
    [InlineData("PAN", true, true)] [InlineData("Email", true, true)] [InlineData("Phone", true, true)]
    public async Task Declared_strings_preserve_original_bytes_and_partition_secondary_without_masks(string type, bool additional, bool secondary)
    {
        const string body = " \r\n{ \"explicit\" : [\"\",\"incompatible é ☃\",null,\"\\u0034111111111111111\",\"a@x.test\",\"+1-202-555-0199\"], \"explicit\":[\"4111111111111111\"], \"other\":\"4111111111111111\" }\t";
        var policy = Policy() with { Segments = additional ? ["absent"] : ["explicit", "[*]"], Transformation = type,
            AdditionalRules = additional ? [new(["explicit", "[*]"], type)] : [], SecondaryPanDetection = secondary };
        using var metrics = new Collector(); using var f = new Fixture(body, policy);
        await f.Run(); f.Original(body); metrics.Terminal(policy, "inspected", 6, secondary ? 1 : 0);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(body), Assert.Single(metrics.For(policy, "output_body_bytes")).Value);
    }

    [Theory]
    [InlineData("root-string", " \"\\u0034111111111111111\" \r\n", true, 0, 1)]
    [InlineData("root-array", " [null,\"\",\"é\",\"4111111111111111\"] ", true, 3, 0)]
    [InlineData("root-array", " [null,\"\",\"é\",\"4111111111111111\"] ", false, 3, 0)]
    [InlineData("root-null", " null \n", true, 0, 0)]
    [InlineData("object", " {\"explicit\":null,\"other\":\"no match\"} \n", true, 0, 0)]
    [InlineData("object", " {\"missing\":[],\"other\":\"4111111111111111\"} \n", false, 0, 0)]
    [InlineData("object", " {\"explicit\":null,\"other\":\"4111111111111111\"} \n", true, 0, 1)]
    [InlineData("cross", " {\"a\":{\"b\":\"\"},\"a\":[{\"b\":\"4111111111111111\"}],\"other\":\"4111111111111111\"} ", true, 2, 1)]
    public async Task Roots_null_no_matches_and_cross_shape_rules_are_byte_exact(string shape, string body, bool secondary, long declared, long detected)
    {
        var policy = Policy() with { SecondaryPanDetection = secondary };
        if (shape == "root-array") policy = policy with { Segments = ["[*]"] };
        if (shape == "cross") policy = policy with { Segments = ["a", "b"], AdditionalRules = [new(["a", "[*]", "b"], "Email")] };
        using var metrics = new Collector(); using var f = new Fixture(body, policy);
        await f.Run(); f.Original(body); metrics.Terminal(policy, "inspected", declared, detected);
    }

    [Theory]
    [InlineData("number")] [InlineData("bool")] [InlineData("object")] [InlineData("array")]
    [InlineData("json")] [InlineData("utf8")] [InlineData("depth")] [InlineData("limit")]
    [InlineData("declared-limit")] [InlineData("encoding")] [InlineData("charset")]
    [InlineData("partial")] [InlineData("read")]
    public async Task Precommit_validation_errors_fail_closed_without_original_or_counts(string failure)
    {
        var tail = failure switch { "number" => "42", "bool" => "true", "object" => "{}", "array" => "[]", "json" => "INVALID", _ => "null" };
        var body = "{\"explicit\":\"\",\"other\":\"4111111111111111\",\"explicit\":" + tail + "}";
        if (failure == "depth") body = "{\"explicit\":\"\",\"other\":" + new string('[', 65) + "null" + new string(']', 65) + "}";
        if (failure == "limit") body = "{\"explicit\":\"" + new string('x', 4096) + "\"}";
        using var metrics = new Collector(); using var f = new Fixture(body, Policy());
        if (failure == "utf8") f.Content(new ByteArrayContent(Encoding.UTF8.GetBytes("{\"explicit\":\"\",\"other\":\"").Concat(new byte[] { 0xff }).Concat("\"}"u8.ToArray()).ToArray()));
        if (failure == "read") f.Content(new StreamContent(new ReadFailure()));
        if (failure == "declared-limit") f.Context.ProxyResponse!.Content.Headers.ContentLength = 4097;
        if (failure == "encoding") f.Context.ProxyResponse!.Content.Headers.ContentEncoding.Add("gzip");
        if (failure == "charset") f.Context.ProxyResponse!.Content.Headers.ContentType!.CharSet = "utf-16";
        if (failure == "partial") f.Context.HttpContext.Response.StatusCode = 206;
        await f.Run(); Assert.Equal(Error, f.Text); Assert.Equal(502, f.Context.HttpContext.Response.StatusCode);
        Assert.DoesNotContain(Pan, f.Text); metrics.Terminal(f.Policy, "blocked", 0, 0); f.Terminal();
    }

    [Theory]
    [InlineData(4095)] [InlineData(4096)] [InlineData(4097)]
    public async Task Actual_body_limit_is_inclusive_and_read_is_bounded_without_length(int bytes)
    {
        var body = "{\"explicit\":\"\"}".PadRight(bytes);
        using var metrics = new Collector(); using var f = new Fixture(body, Policy());
        using var source = new UnseekableRead(Encoding.UTF8.GetBytes(body)); f.Content(new StreamContent(source));
        Assert.Null(f.Context.ProxyResponse!.Content.Headers.ContentLength);
        await f.Run(); Assert.InRange(source.BytesRead, bytes, 4097);
        if (bytes <= 4096) { f.Original(body); metrics.Terminal(f.Policy, "inspected", 1, 0); }
        else { Assert.Equal(Error, f.Text); Assert.Equal(502, f.Context.HttpContext.Response.StatusCode); metrics.Terminal(f.Policy, "blocked", 0, 0); Assert.Empty(metrics.For(f.Policy, "input_body_bytes")); f.Terminal(); }
    }

    [Theory]
    [InlineData(63)] [InlineData(64)] [InlineData(65)]
    public async Task JSON_max_depth_remains_64_inclusive(int depth)
    {
        var body = "{\"explicit\":\"\",\"other\":" + new string('[', depth - 1) + "null" + new string(']', depth - 1) + "}";
        using var metrics = new Collector(); using var f = new Fixture(body, Policy()); await f.Run();
        if (depth <= 64) { f.Original(body); metrics.Terminal(f.Policy, "inspected", 1, 0); }
        else { Assert.Equal(Error, f.Text); Assert.Equal(502, f.Context.HttpContext.Response.StatusCode); metrics.Terminal(f.Policy, "blocked", 0, 0); f.Terminal(); }
    }

    // Existing TimeProvider/StackTrace observation technique: JIT sensitive, not a real timer-delivery assertion.
    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Traversal_or_writer_boundary_cancel_deadline_discards_uncommitted_counts(bool cancel, bool afterWriter, bool multiple)
    {
        using var metrics = new Collector(); using var f = new Fixture(Body, Policy() with { SecondaryPanDetection = multiple });
        var traversed = false; var checks = 0; var triggered = false;
        f.Clock.OnTimestamp = () =>
        {
            var inside = new System.Diagnostics.StackTrace().GetFrames().Any(x => x.GetMethod()?.Name == (multiple ? "WriteMultiple" : "WriteSelected"));
            if (inside) { traversed = true; checks++; }
            if (triggered || (afterWriter ? !traversed || inside : !inside || checks != 4)) return;
            triggered = true; f.Clock.OnTimestamp = null;
            if (cancel) f.Caller.Cancel(); else f.Clock.Advance();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run);
        Assert.True(triggered); Assert.Empty(f.Text); metrics.Terminal(f.Policy, cancel ? "canceled" : "timed_out", 0, 0); f.Terminal();
    }

    [Theory]
    [InlineData("checkpoint")] [InlineData("headers")] [InlineData("cancel")] [InlineData("deadline")]
    public async Task Postcommit_checkpoint_headers_cancel_deadline_retain_validated_counts(string failure)
    {
        using var metrics = new Collector(); using var f = new Fixture(Body, Policy());
        f.Checkpoint = _ =>
        {
            Assert.Empty(metrics.For(f.Policy, "outcomes")); Assert.Equal((1, 4096L), f.Budget.Snapshot);
            if (failure == "checkpoint") throw new InvalidOperationException("Synthetic checkpoint failure.");
            if (failure == "headers") ((HeaderDictionary)f.Context.HttpContext.Response.Headers).IsReadOnly = true;
            if (failure == "cancel") f.Caller.Cancel();
            if (failure == "deadline") f.Clock.Advance();
            return ValueTask.CompletedTask;
        };
        if (failure is "cancel" or "deadline") await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run);
        else await Assert.ThrowsAsync<InvalidOperationException>(f.Run);
        Assert.Empty(f.Text); Assert.Empty(metrics.For(f.Policy, "output_body_bytes"));
        metrics.Terminal(f.Policy, failure == "cancel" ? "canceled" : failure == "deadline" ? "timed_out" : "faulted", 1, 1); f.Terminal();
    }

    [Theory]
    [InlineData("io")] [InlineData("cancel")] [InlineData("deadline")]
    public async Task Pending_write_retains_lease_original_prepared_bytes_and_counts_until_completion(string failure)
    {
        using var metrics = new Collector(); using var gate = new WriteGate(); using var f = new Fixture(Body, Policy(), gate);
        var pending = f.Run();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Encoding.UTF8.GetBytes(Body), gate.Attempted); Assert.Equal((1, 4096L), f.Budget.Snapshot);
            Assert.Empty(metrics.For(f.Policy, "outcomes")); Assert.Empty(metrics.For(f.Policy, "explicit_sensitive_matches"));
            if (failure == "io") gate.Release.TrySetException(new IOException("Synthetic write failure."));
            if (failure == "cancel") f.Caller.Cancel();
            if (failure == "deadline") f.Clock.Advance();
            if (failure != "io") { Assert.False(pending.IsCompleted); Assert.Equal((1, 4096L), f.Budget.Snapshot); gate.Release.TrySetResult(); }
            if (failure == "io") await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            metrics.Terminal(f.Policy, failure == "io" ? "write_error" : failure == "cancel" ? "canceled" : "timed_out", 1, 1);
            Assert.Equal((long)Encoding.UTF8.GetByteCount(Body), Assert.Single(metrics.For(f.Policy, "output_body_bytes")).Value); f.Terminal();
        }
        finally { gate.Release.TrySetResult(); try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is IOException or OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Pending_read_retains_lease_after_deadline_or_cancel_until_IO_returns(bool cancel)
    {
        using var metrics = new Collector(); using var gate = new ReadGate(); using var f = new Fixture(Body, Policy()); f.Content(new StreamContent(gate));
        var pending = f.Run();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) f.Caller.Cancel(); else f.Clock.Advance();
            Assert.False(pending.IsCompleted); Assert.Equal((1, 4096L), f.Budget.Snapshot); Assert.Empty(metrics.For(f.Policy, "outcomes"));
            gate.Release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(f.Text); Assert.Empty(metrics.For(f.Policy, "input_body_bytes")); Assert.Empty(metrics.For(f.Policy, "output_body_bytes"));
            metrics.Terminal(f.Policy, cancel ? "canceled" : "timed_out", 0, 0); f.Terminal();
        }
        finally { gate.Release.TrySetResult(); try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData("head")] [InlineData("204")] [InlineData("304")] [InlineData("media")]
    [InlineData("null-upstream")] [InlineData("pre-cancel")] [InlineData("capacity")]
    public async Task Exclusions_precancel_and_capacity_do_not_read_or_leak(string scenario)
    {
        using var metrics = new Collector(); using var f = new Fixture(Body, Policy(), noUpstream: scenario == "null-upstream"); using var source = new ReadFailure();
        if (scenario != "null-upstream") f.Content(new StreamContent(source));
        if (scenario == "head") f.Context.HttpContext.Request.Method = "HEAD";
        if (scenario == "204") f.Context.ProxyResponse!.StatusCode = HttpStatusCode.NoContent;
        if (scenario == "304") f.Context.ProxyResponse!.StatusCode = HttpStatusCode.NotModified;
        if (scenario == "media") f.Context.ProxyResponse!.Content.Headers.ContentType = new("text/plain");
        using (var held = scenario == "capacity" ? f.Budget.TryAcquire(4096) : null)
        {
            if (scenario == "pre-cancel") { f.Caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run); }
            else await f.Run();
            Assert.Equal(0, source.Reads); Assert.Empty(metrics.For(f.Policy, "duration"));
            Assert.Empty(metrics.For(f.Policy, "explicit_sensitive_matches")); Assert.Empty(metrics.For(f.Policy, "secondary_pan_matches"));
            if (scenario == "capacity")
            {
                Assert.Equal(503, f.Context.HttpContext.Response.StatusCode); Assert.Equal("{\"code\":\"privacy_capacity_exceeded\"}", f.Text);
                Assert.Equal("capacity_exceeded", Assert.Single(metrics.For(f.Policy, "outcomes")).Tags["gateway.privacy.outcome"]); Assert.Equal((1, 4096L), f.Budget.Snapshot);
            }
            else
            {
                if (scenario is "head" or "204" or "304" or "media")
                {
                    var outcome = Assert.Single(metrics.For(f.Policy, "outcomes"));
                    Assert.Equal(1L, Assert.IsType<long>(outcome.Value));
                    Assert.Equal(scenario == "304" ? "blocked" : scenario == "head" ? "excluded_head" : scenario == "media" ? "excluded_media_type" : "excluded_bodyless",
                        outcome.Tags["gateway.privacy.outcome"]);
                }
                else Assert.Empty(metrics.For(f.Policy, "outcomes"));
                if (scenario == "304") { Assert.Equal(502, f.Context.HttpContext.Response.StatusCode); Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", f.Text); }
                else Assert.Empty(f.Text);
            }
        }
        Assert.Equal((0, 0L), f.Budget.Snapshot);
    }

    [Theory]
    [InlineData("outcomes")] [InlineData("duration")] [InlineData("input_body_bytes")]
    [InlineData("output_body_bytes")] [InlineData("masks")] [InlineData("secondary_pan_matches")] [InlineData("explicit_sensitive_matches")]
    public async Task Seven_instruments_keep_exact_contract_and_listener_exceptions_do_not_change_original(string failing)
    {
        using var metrics = new Collector(); using var f = new Fixture(Body, Policy());
        metrics.OnSample = s => { if (s.Name == Prefix + failing) throw new InvalidOperationException("Synthetic listener failure."); };
        await f.Run(); f.Original(Body); metrics.Terminal(f.Policy, "inspected", 1, 1);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(Body), Assert.Single(metrics.For(f.Policy, "input_body_bytes")).Value);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(Body), Assert.Single(metrics.For(f.Policy, "output_body_bytes")).Value);
        // Observe has no masks publication, even when a masks listener is installed.
        Assert.Empty(metrics.For(f.Policy, "masks"));
    }

    [Fact]
    public async Task Blocking_callback_holds_lease_and_capacity_until_callback_returns()
    {
        using var metrics = new Collector(); using var f = new Fixture(Body, Policy());
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        metrics.OnSample = s => { if (s.Name == Prefix + "explicit_sensitive_matches") { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        var pending = Task.Run(f.Run);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted); Assert.Equal((1, 4096L), f.Budget.Snapshot);
            Assert.Null(f.Budget.TryAcquire(4096)); Assert.Equal(Body, f.Text);
        }
        finally { release.Set(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        metrics.Terminal(f.Policy, "inspected", 1, 1); f.Terminal();
    }

    [Theory]
    [InlineData(200)] [InlineData(422)] [InlineData(500)]
    public async Task Real_HTTP_Observe_returns_original_status_body_and_sanitized_headers_with_terminal_barrier(int status)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        const string body = " { \"payment\":{\"cardNumber\":\"\\u0034111111111111111\"},\"email\":\"a@x.test\",\"phone\":\"+1-202-555-0199\",\"other\":\"4111111111111111\",\"other\":\"é ☃\" }\r\n";
        api.MapGet("/test", async c =>
        {
            c.Response.StatusCode = status; c.Response.ContentType = "application/json";
            foreach (var h in new[] { "ETag", "Digest", "Content-Digest", "Repr-Digest", "Signature", "Signature-Input", "Content-MD5" }) c.Response.Headers[h] = "synthetic";
            c.Response.Headers.LastModified = "Wed, 01 Jan 2025 00:00:00 GMT"; c.Response.Headers["X-Synthetic-Hop"] = "yes";
            await c.Response.WriteAsync(body, c.RequestAborted);
        });
        await api.StartAsync();
        var values = PolicyConfiguration.Defaults(); values["PanPolicies:Policies:0:Mode"] = "Observe";
        values["PanPolicies:Policies:0:PolicyId"] = "http-observe-" + Guid.NewGuid().ToString("N");
        values["PanPolicies:Policies:0:SecondaryPanDetection"] = "true";
        values["PanPolicies:Policies:0:AdditionalRules:0:Selector"] = "$.email"; values["PanPolicies:Policies:0:AdditionalRules:0:Transformation"] = "Email";
        values["PanPolicies:Policies:0:AdditionalRules:1:Selector"] = "$.phone"; values["PanPolicies:Policies:0:AdditionalRules:1:Transformation"] = "Phone";
        await using var app = GatewayHost.Build([], Address(api) + "/", b => { b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(values); b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0"); });
        var policy = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        var telemetry = app.Services.GetRequiredService<PrivacyTelemetry>(); var budget = app.Services.GetRequiredService<InspectionBudget>();
        using var metrics = new Collector(); await app.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(app)), Timeout = TimeSpan.FromSeconds(10) };
        for (var n = 1; n <= 2; n++)
        {
            using var response = await client.GetAsync("/protected/test");
            Assert.Equal(status, (int)response.StatusCode); Assert.Equal(Encoding.UTF8.GetBytes(body), await response.Content.ReadAsByteArrayAsync());
            Assert.True(response.Headers.CacheControl!.NoStore); Assert.Equal("yes", Assert.Single(response.Headers.GetValues("X-Synthetic-Hop")));
            foreach (var h in new[] { "ETag", "Last-Modified", "Digest", "Content-Digest", "Repr-Digest", "Signature", "Signature-Input", "Content-MD5" }) Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers), x => x.Key.Equals(h, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString()); Assert.Equal(Encoding.UTF8.GetByteCount(body), response.Content.Headers.ContentLength);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (telemetry.DashboardMetrics.Duration.Samples < n || budget.Snapshot != (0, 0L)) await Task.Delay(10, timeout.Token);
            Assert.Equal(n, metrics.For(policy, "outcomes").Length); Assert.All(metrics.For(policy, "outcomes"), s => Assert.Equal("inspected", s.Tags["gateway.privacy.outcome"]));
            Assert.Equal(n, metrics.For(policy, "explicit_sensitive_matches").Length); Assert.All(metrics.For(policy, "explicit_sensitive_matches"), s => Assert.Equal(3L, s.Value));
            Assert.Equal(n, metrics.For(policy, "secondary_pan_matches").Length); Assert.All(metrics.For(policy, "secondary_pan_matches"), s => Assert.Equal(1L, s.Value));
            Assert.Empty(metrics.For(policy, "masks")); Assert.Equal(0, telemetry.DashboardMetrics.ValidatedMasks);
        }
        using var summary = await client.GetAsync("/dashboard/api/summary"); using var json = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
        Assert.Equal("Observe", json.RootElement.GetProperty("policies")[0].GetProperty("mode").GetString());
        using var html = await client.GetAsync("/dashboard/"); var text = await html.Content.ReadAsStringAsync();
        Assert.Contains("<strong>Observe entrega los datos originales.</strong>", text);
        Assert.Contains("CoverageContract acredita reglas configuradas, no protección efectiva", text);
        Assert.Contains("solo datos sintéticos locales", text); Assert.Contains("No garantiza privacidad", text);
        Assert.DoesNotContain("demo-observe", text);
        using var raw = await client.GetAsync("/passthrough/test"); Assert.Equal(Encoding.UTF8.GetBytes(body), await raw.Content.ReadAsByteArrayAsync()); Assert.True(raw.Headers.Contains("Digest"));
        Assert.Equal(2, metrics.For(policy, "outcomes").Length);
    }

    [Theory]
    [InlineData("INVALID}")] [InlineData("42}")]
    public async Task Real_HTTP_waits_for_complete_capture_and_never_exposes_original_before_late_failure(string tail)
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        api.MapGet("/test", async c =>
        {
            c.Response.ContentType = "application/json"; c.Response.Headers["X-Sensitive-Upstream"] = Pan;
            await c.Response.WriteAsync("{\"payment\":{\"cardNumber\":\"4111111111111111\"},\"payment\":{\"cardNumber\":", c.RequestAborted);
            await c.Response.Body.FlushAsync(c.RequestAborted); flushed.TrySetResult(); await release.Task.WaitAsync(c.RequestAborted);
            await c.Response.WriteAsync(tail + "}", c.RequestAborted);
        });
        await api.StartAsync(); var values = PolicyConfiguration.Defaults(); values["PanPolicies:Policies:0:Mode"] = "Observe";
        await using var app = GatewayHost.Build([], Address(api) + "/", b => { b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(values); b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0"); });
        await app.StartAsync(); var budget = app.Services.GetRequiredService<InspectionBudget>(); var telemetry = app.Services.GetRequiredService<PrivacyTelemetry>();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(app)), Timeout = TimeSpan.FromSeconds(10) };
        var pending = client.GetAsync("/protected/test", HttpCompletionOption.ResponseHeadersRead);
        try
        {
            await flushed.Task.WaitAsync(TimeSpan.FromSeconds(5)); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (budget.Snapshot.ActiveInspections != 1) await Task.Delay(10, timeout.Token);
            Assert.False(pending.IsCompleted); Assert.Equal(0, telemetry.DashboardMetrics.Duration.Samples);
        }
        finally { release.TrySetResult(); }
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); Assert.Equal(Error, await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("X-Sensitive-Upstream")); Assert.True(response.Headers.CacheControl!.NoStore);
        using var terminal = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (telemetry.DashboardMetrics.Duration.Samples < 1 || budget.Snapshot != (0, 0L)) await Task.Delay(10, terminal.Token);
        Assert.Equal(0, telemetry.DashboardMetrics.ValidatedMasks);
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private sealed class Fixture : IDisposable
    {
        public CompiledPanPolicy Policy { get; }
        public readonly CancellationTokenSource Caller = new(); public readonly ManualClock Clock = new();
        public readonly InspectionBudget Budget = new(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = 4096, InspectionTimeoutSeconds = 1 } }));
        public readonly PrivacyTelemetry Telemetry; public readonly ResponseTransformContext Context;
        private readonly MemoryStream output = new(); public Func<CancellationToken, ValueTask>? Checkpoint;
        public Fixture(string body, CompiledPanPolicy policy, Stream? downstream = null, bool noUpstream = false)
        {
            Policy = policy; Telemetry = new(Budget); var http = new DefaultHttpContext(); http.Request.Method = "GET"; http.Response.Body = downstream ?? output;
            Context = new() { HttpContext = http, ProxyResponse = noUpstream ? null : new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, HeadersCopied = true, CancellationToken = Caller.Token };
        }
        public void Content(HttpContent content) { Context.ProxyResponse!.Content.Dispose(); Context.ProxyResponse.Content = content; content.Headers.ContentType = new("application/json"); }
        public string Text => Encoding.UTF8.GetString(output.ToArray());
        public Task Run() => new PanResponseTransform(new SafeLogger(), Telemetry, Checkpoint, Clock).ApplyAsync(Context, Policy).AsTask();
        public void Original(string body) { Assert.Equal(Encoding.UTF8.GetBytes(body), output.ToArray()); Assert.Equal(200, Context.HttpContext.Response.StatusCode); Assert.True(Context.SuppressResponseBody); Terminal(); }
        public void Terminal() { Assert.Equal(1, Telemetry.DashboardMetrics.Duration.Samples); Assert.Equal(0, Telemetry.DashboardMetrics.ValidatedMasks); Assert.Equal((0, 0L), Budget.Snapshot); using var next = Budget.TryAcquire(4096); Assert.NotNull(next); }
        public void Dispose() { Context.ProxyResponse?.Dispose(); Telemetry.Dispose(); Caller.Dispose(); output.Dispose(); }
    }
    private sealed class SafeLogger : ILogger<PanResponseTransform>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception); var text = formatter(state, exception);
            foreach (var value in new[] { Pan, "incompatible", "é", "a@x.test", "+1-202-555-0199", "$.explicit" }) Assert.DoesNotContain(value, text);
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks; public Action? OnTimestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { OnTimestamp?.Invoke(); return Interlocked.Read(ref ticks); }
        public void Advance() => Interlocked.Add(ref ticks, TimeSpan.TicksPerSecond);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class WriteGate : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Attempted;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Attempted = buffer.ToArray(); Entered.TrySetResult(); await Release.Task; cancellationToken.ThrowIfCancellationRequested(); }
    }
    private sealed class ReadFailure : MemoryStream
    {
        public int Reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Reads++; return ValueTask.FromException<int>(new IOException("Synthetic read failure.")); }
    }
    private sealed class UnseekableRead(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead;
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var count = await base.ReadAsync(buffer, cancellationToken); BytesRead += count; return count; }
    }
    private sealed class ReadGate : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Entered.TrySetResult(); await Release.Task; return 0; }
    }
    private sealed record Sample(Instrument Instrument, object Value, Dictionary<string, string?> Tags) { public string Name => Instrument.Name; }
    private sealed class Collector : IDisposable
    {
        private readonly ConcurrentQueue<Sample> samples = new(); public Action<Sample>? OnSample; private readonly MeterListener listener = new();
        public Collector()
        {
            listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == PrivacyTelemetry.MeterName) l.EnableMeasurementEvents(i); };
            listener.SetMeasurementEventCallback<long>((i, v, t, _) => Capture(i, v, t)); listener.SetMeasurementEventCallback<double>((i, v, t, _) => Capture(i, v, t)); listener.Start();
        }
        private void Capture(Instrument i, object value, ReadOnlySpan<KeyValuePair<string, object?>> tags) { var s = new Sample(i, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString())); samples.Enqueue(s); OnSample?.Invoke(s); }
        public Sample[] For(CompiledPanPolicy p, string name) => samples.Where(s => s.Name == Prefix + name && s.Tags.GetValueOrDefault("gateway.policy.id") == p.PolicyId).ToArray();
        public void Terminal(CompiledPanPolicy p, string outcome, long declared, long secondary)
        {
            Assert.Equal(1L, Assert.Single(For(p, "outcomes")).Value); Assert.Single(For(p, "duration")); Assert.Empty(For(p, "masks"));
            foreach (var (name, count) in new[] { ("explicit_sensitive_matches", declared), ("secondary_pan_matches", secondary) })
            { if (count == 0) Assert.Empty(For(p, name)); else Assert.Equal(count, Assert.Single(For(p, name)).Value); }
            var expected = new Dictionary<string, string?> { ["gateway.route.id"] = p.RouteId, ["gateway.policy.id"] = p.PolicyId, ["gateway.policy.version"] = p.Version, ["gateway.privacy.outcome"] = outcome };
            foreach (var s in samples.Where(s => s.Tags.GetValueOrDefault("gateway.policy.id") == p.PolicyId))
            {
                Assert.Equal(expected.OrderBy(x => x.Key), s.Tags.OrderBy(x => x.Key)); Assert.Equal("1.0.0", s.Instrument.Meter.Version);
                if (s.Name.EndsWith("duration")) { Assert.IsType<Histogram<double>>(s.Instrument); Assert.Equal("ms", s.Instrument.Unit); }
                else if (s.Name.EndsWith("body_bytes")) { Assert.IsType<Histogram<long>>(s.Instrument); Assert.Equal("By", s.Instrument.Unit); }
                else { Assert.IsType<Counter<long>>(s.Instrument); Assert.Equal(s.Name.EndsWith("outcomes") ? "{inspection}" : "{match}", s.Instrument.Unit); }
                var tags = string.Join('\n', s.Tags.Values); foreach (var value in new[] { Pan, "é", "a@x.test", "$.explicit", "Observe" }) Assert.DoesNotContain(value, tags);
            }
        }
        public void Dispose() => listener.Dispose();
    }
}
