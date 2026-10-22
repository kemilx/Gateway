using System.Collections.Concurrent;
using System.Collections.Immutable;
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
using TestApi;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

public sealed class MultiRuleTransformTests
{
    private const string InspectionError = "{\"code\":\"privacy_inspection_failed\"}";
    private const string BlockError = "{\"code\":\"privacy_response_blocked\"}";
    private const string Customer = "{\"payment\":{\"cardNumber\":\"4111111111111111\"},\"contact\":{\"email\":\"user@example.test\",\"phone\":\"+1-809-555-0199\"}}";
    private static CompiledPanPolicy Policy() => new("multi-" + Guid.NewGuid().ToString("N"), "v1", "protected-payment", ["payment", "cardNumber"], 4096)
    { AdditionalRules = [new(["contact", "email"], "Email"), new(["contact", "phone"], "Phone")] };

    [Fact]
    public async Task Build_shares_prefixes_preserves_ordinal_array_branches_and_terminal_types()
    {
        var policy = Policy() with { Segments = ["a", "b"], AdditionalRules = [new(["a", "B"], "Email"), new(["a", "[*]", "b"], "Phone")] };
        var clock = new ManualClock();
        await using var deadline = new InspectionDeadline(clock, 0, TimeSpan.FromSeconds(1), new DefaultHttpContext(), default);
        var trie = MultiRuleSelection.Build(policy, deadline);
        Assert.Null(trie.Transformation);
        Assert.Null(trie.ArrayItem);
        var a = Assert.Single(trie.Properties).Value;
        Assert.Equal(new[] { "B", "b" }, a.Properties.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("PAN", a.Properties["b"].Transformation);
        Assert.Equal("Email", a.Properties["B"].Transformation);
        Assert.Equal("Phone", a.ArrayItem!.Properties["b"].Transformation);
        Assert.Null(a.Transformation);
        Assert.Equal(6, Nodes(trie));
    }

    [Fact]
    public async Task Build_reaches_257_node_bound_and_rebuilds_changed_record_without_stale_rules()
    {
        var paths = Enumerable.Range(0, 16).Select(i => Enumerable.Range(0, 16).Select(j => "p" + i + "s" + j).ToImmutableArray()).ToArray();
        var policy = Policy() with { Segments = paths[0], AdditionalRules = [.. paths.Skip(1).Select(p => new CompiledSensitiveRule(p, "Email"))] };
        var clock = new ManualClock();
        await using var deadline = new InspectionDeadline(clock, 0, TimeSpan.FromSeconds(1), new DefaultHttpContext(), default);
        var original = MultiRuleSelection.Build(policy, deadline);
        Assert.Equal(257, Nodes(original));
        var changed = MultiRuleSelection.Build(policy with { Segments = ["new"], Transformation = "Phone", AdditionalRules = [new(["newer"], "PAN")] }, deadline);
        Assert.Equal(3, Nodes(changed));
        Assert.Equal("Phone", changed.Properties["new"].Transformation);
        Assert.Equal("PAN", changed.Properties["newer"].Transformation);
        Assert.False(changed.Properties.ContainsKey("p0s0"));
        Assert.Equal(257, Nodes(original));
    }

    private static int Nodes(MultiRuleSelection node) => 1 + node.Properties.Values.Sum(Nodes) + (node.ArrayItem is { } array ? Nodes(array) : 0);

    [Fact]
    public async Task Three_types_transform_together_and_derived_record_changes_are_not_cached()
    {
        var policy = Policy();
        using var first = new Fixture(Customer, policy);
        await first.Run();
        AssertCustomer(first.Output.ToArray());
        first.AssertCompleted("inspected", 3, Encoding.UTF8.GetByteCount(Customer), first.Output.Length);
        using var second = new Fixture(Customer, policy with { Segments = ["missing"], AdditionalRules = [new(["contact", "phone"], "Email")] });
        await second.Run();
        using var json = JsonDocument.Parse(second.Output.ToArray());
        Assert.Equal(SyntheticApi.Pan, json.RootElement.GetProperty("payment").GetProperty("cardNumber").GetString());
        Assert.Equal("user@example.test", json.RootElement.GetProperty("contact").GetProperty("email").GetString());
        Assert.Equal("[REDACTED]", json.RootElement.GetProperty("contact").GetProperty("phone").GetString());
        second.AssertCompleted("inspected", 1, Encoding.UTF8.GetByteCount(Customer), second.Output.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_prefix_duplicates_escaped_keys_values_and_rule_order_preserve_all_properties(bool reverse)
    {
        var rules = new[] { new CompiledSensitiveRule(["a", "e"], "Email"), new CompiledSensitiveRule(["a", "p"], "Phone") };
        var policy = Policy() with { Segments = ["a", "n"], AdditionalRules = [.. reverse ? rules.Reverse() : rules] };
        const string body = "{\"a\":{\"n\":\"4111111111111111\",\"\\u0065\":\"u\\u0073er@example.test\",\"e\":\"\",\"p\":\"\\u002b12345\",\"other\":\"é\"},\"a\":{\"e\":\"a@x\"},\"A\":{\"e\":\"untouched\"}}";
        using var fixture = new Fixture(body, policy);
        await fixture.Run();
        using var json = JsonDocument.Parse(fixture.Output.ToArray());
        var root = json.RootElement.EnumerateObject().ToArray();
        Assert.Equal(new[] { "a", "a", "A" }, root.Select(p => p.Name));
        var fields = root[0].Value.EnumerateObject().ToArray();
        Assert.Equal(new[] { "n", "e", "e", "p", "other" }, fields.Select(p => p.Name));
        Assert.Equal(new[] { "************1111", "***@example.test", "[REDACTED]", "+*2345", "é" }, fields.Select(p => p.Value.GetString()));
        Assert.Equal("***@x", root[1].Value.GetProperty("e").GetString());
        Assert.Equal("untouched", root[2].Value.GetProperty("e").GetString());
        fixture.AssertCompleted("inspected", 5, Encoding.UTF8.GetByteCount(body), fixture.Output.Length);
    }

    [Theory]
    [InlineData("root-nested")]
    [InlineData("mixed")]
    [InlineData("ordinal-name-prefix")]
    public async Task Array_and_object_branches_match_only_exact_steps(string scenario)
    {
        var (body, primary, extras, expected, masks) = scenario switch
        {
            "root-nested" => ("[[{\"e\":\"a@x\",\"p\":\"12345\"},null,42],[]]", new[] { "[*]", "[*]", "e" }, new[] { new CompiledSensitiveRule(["[*]", "[*]", "p"], "Phone") }, "[[{\"e\":\"***@x\",\"p\":\"*2345\"},null,42],[]]", 2),
            "mixed" => ("{\"a\":{\"b\":\"a@x\"},\"a\":[{\"b\":\"12345\"},false,[]]}", new[] { "a", "b" }, new[] { new CompiledSensitiveRule(["a", "[*]", "b"], "Phone") }, "{\"a\":{\"b\":\"***@x\"},\"a\":[{\"b\":\"*2345\"},false,[]]}", 2),
            _ => ("{\"a\":\"a@x\",\"ab\":\"12345\",\"A\":\"raw\",\"abc\":\"raw\"}", new[] { "a" }, new[] { new CompiledSensitiveRule(["ab"], "Phone") }, "{\"a\":\"***@x\",\"ab\":\"*2345\",\"A\":\"raw\",\"abc\":\"raw\"}", 2)
        };
        using var fixture = new Fixture(body, Policy() with { Segments = [.. primary], Transformation = "Email", AdditionalRules = [.. extras] });
        await fixture.Run();
        Assert.Equal(expected, Encoding.UTF8.GetString(fixture.Output.ToArray()));
        fixture.AssertCompleted("inspected", masks, Encoding.UTF8.GetByteCount(body), fixture.Output.Length);
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    public async Task No_match_null_and_incompatible_intermediates_preserve_status_and_original_UTF8(string mode)
    {
        foreach (var body in new[] { " { \"other\" : \"é\\u0041\" }\r\n", " {\"payment\":{\"cardNumber\":null},\"contact\":{\"email\":null,\"phone\":null}} \n", "{\"payment\":42,\"contact\":[],\"tail\":true}", " null \n" })
        {
            using var fixture = new Fixture(body, Policy() with { Mode = mode });
            fixture.Context.HttpContext.Response.StatusCode = 422;
            await fixture.Run();
            Assert.Equal(422, fixture.Context.HttpContext.Response.StatusCode);
            Assert.Equal(Encoding.UTF8.GetBytes(body), fixture.Output.ToArray());
            fixture.AssertCompleted("inspected", 0, Encoding.UTF8.GetByteCount(body), Encoding.UTF8.GetByteCount(body));
        }
    }

    [Theory]
    [InlineData("PAN")]
    [InlineData("Email")]
    [InlineData("Phone")]
    public async Task Invalid_formatter_string_is_redacted_and_counted_once(string type)
    {
        const string body = "{\"v\":\"incompatible é\"}";
        using var fixture = new Fixture(body, Policy() with { Segments = ["absent"], AdditionalRules = [new(["v"], type)] });
        await fixture.Run();
        Assert.Equal("{\"v\":\"[REDACTED]\"}", Encoding.UTF8.GetString(fixture.Output.ToArray()));
        fixture.AssertCompleted("inspected", 1, Encoding.UTF8.GetByteCount(body), fixture.Output.Length);
    }

    [Theory]
    [InlineData("Mask", false)]
    [InlineData("Mask", true)]
    [InlineData("Block", false)]
    [InlineData("Block", true)]
    public async Task Late_primary_or_additional_invalid_terminal_discards_partial_masks_and_overrides_Block(string mode, bool primaryLate)
    {
        foreach (var invalid in new[] { "42", "true", "{}", "[]" })
        {
            var body = primaryLate ? "{\"contact\":{\"email\":\"a@x\"},\"payment\":{\"cardNumber\":" + invalid + "}}"
                : "{\"payment\":{\"cardNumber\":\"4111111111111111\"},\"contact\":{\"email\":" + invalid + "}}";
            using var fixture = new Fixture(body, Policy() with { Mode = mode });
            await fixture.Run();
            fixture.AssertError(InspectionError);
            fixture.AssertCompleted("blocked", 0, Encoding.UTF8.GetByteCount(body), Encoding.UTF8.GetByteCount(InspectionError));
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("valid")]
    [InlineData("incompatible")]
    [InlineData("late-parse")]
    public async Task Block_selects_any_additional_string_without_masking_and_parse_error_wins(string scenario)
    {
        var input = scenario == "empty" ? "" : scenario == "valid" ? "a@x" : "incompatible é";
        var body = JsonSerializer.Serialize(new { contact = new { email = input } });
        if (scenario == "late-parse") body = body[..^1] + ",\"tail\":INVALID}";
        using var fixture = new Fixture(body, Policy() with { Mode = "Block" });
        await fixture.Run();
        var error = scenario == "late-parse" ? InspectionError : BlockError;
        fixture.AssertError(error);
        fixture.AssertCompleted(scenario == "late-parse" ? "blocked" : "policy_blocked", 0, Encoding.UTF8.GetByteCount(body), Encoding.UTF8.GetByteCount(error), scenario == "late-parse" ? 0 : 1);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("write-error")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    public async Task Pending_write_retains_single_lease_and_committed_masks_until_terminal_outcome(string outcome)
    {
        using var gate = new WriteGate();
        using var fixture = new Fixture(Customer, Policy(), gate);
        var pending = fixture.Run();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.Equal((1, 4096L), fixture.Budget.Snapshot);
            Assert.Equal(0, fixture.Telemetry.DashboardMetrics.ValidatedMasks);
            Assert.Equal(0, fixture.Telemetry.DashboardMetrics.Duration.Samples);
            Assert.Empty(fixture.Measurements);
            Assert.Null(fixture.Budget.TryAcquire(4096));
            AssertCustomer(gate.Attempted!);
            if (outcome == "cancel") fixture.Caller.Cancel();
            if (outcome == "timeout") fixture.Clock.Advance();
            if (outcome is "cancel" or "timeout")
            {
                Assert.False(pending.IsCompleted); // The write deliberately does not cooperate until released.
                Assert.Equal((1, 4096L), fixture.Budget.Snapshot);
                Assert.Empty(fixture.Measurements);
            }
            if (outcome == "write-error") gate.Release.TrySetException(new IOException("synthetic write failure"));
            else gate.Release.TrySetResult();
            if (outcome == "write-error") await Assert.ThrowsAsync<IOException>(() => pending);
            else if (outcome is "cancel" or "timeout") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            else await pending;
            fixture.AssertCompleted(outcome switch { "success" => "inspected", "cancel" => "canceled", "timeout" => "timed_out", _ => "write_error" }, 3, Encoding.UTF8.GetByteCount(Customer), gate.Attempted!.Length);
            Assert.Equal(1, gate.Calls);
        }
        finally
        {
            gate.Release.TrySetResult();
            try { await pending; }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Saturated_capacity_rejects_before_read_without_masks_or_sizes()
    {
        using var source = new CountingStream(Encoding.UTF8.GetBytes(Customer));
        using var fixture = new Fixture(Customer, Policy(), input: source);
        using var held = fixture.Budget.TryAcquire(4096);
        await fixture.Run();
        Assert.Equal(0, source.Reads);
        Assert.Equal(503, fixture.Context.HttpContext.Response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_capacity_exceeded\"}", Encoding.UTF8.GetString(fixture.Output.ToArray()));
        Assert.Equal((1, 4096L), fixture.Budget.Snapshot);
        Assert.Equal(0, fixture.Telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(0, fixture.Telemetry.DashboardMetrics.Input.Samples);
        Assert.Equal(0, fixture.Telemetry.DashboardMetrics.Output.Samples);
        Assert.Equal(0, fixture.Telemetry.DashboardMetrics.Duration.Samples);
        Assert.Equal("gateway.privacy.inspection.outcomes", Assert.Single(fixture.Measurements).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_checks_each_step_for_cancellation_or_deadline(bool cancel)
    {
        var clock = new ManualClock();
        using var caller = new CancellationTokenSource();
        await using var deadline = new InspectionDeadline(clock, 0, TimeSpan.FromSeconds(1), new DefaultHttpContext(), caller.Token);
        var checks = 0;
        clock.OnTimestamp = () => { if (++checks == 4) { if (cancel) caller.Cancel(); else clock.Advance(); } };
        Assert.ThrowsAny<OperationCanceledException>(() => MultiRuleSelection.Build(Policy(), deadline));
        Assert.Equal(4, checks);
        Assert.Equal(!cancel, deadline.TimedOut);
    }

    [Theory]
    [InlineData("Build", false)]
    [InlineData("Build", true)]
    [InlineData("WriteMultiple", false)]
    [InlineData("WriteMultiple", true)]
    public async Task Clock_checkpoint_before_validation_discards_provisional_masks_and_never_prepares_output(string phase, bool cancel)
    {
        const string body = "{\"p\":\"4111111111111111\",\"e\":\"a@x\"}";
        using var fixture = new Fixture(body, Policy() with { Segments = ["p"], AdditionalRules = [new(["e"], "Email")] });
        var checkpoints = 0;
        var triggered = false;
        // Observe the existing TimeProvider seam only. With this flat object, writer checks
        // 1=root, 2=property, 3=terminal entry, 4=after first provisional substitution.
        // No sleep, production hook, or callback changes production data.
        fixture.Clock.OnTimestamp = () =>
        {
            if (triggered) return;
            var frames = new System.Diagnostics.StackTrace().GetFrames();
            if (!frames.Any(f => f.GetMethod()?.Name == phase)) return;
            if (++checkpoints != (phase == "Build" ? 2 : 4)) return;
            triggered = true;
            if (cancel) fixture.Caller.Cancel(); else fixture.Clock.Advance();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(fixture.Run);
        Assert.True(triggered);
        Assert.Equal(phase == "Build" ? 2 : 4, checkpoints);
        Assert.Empty(fixture.Output.ToArray());
        Assert.Equal(200, fixture.Context.HttpContext.Response.StatusCode);
        fixture.AssertCompleted(cancel ? "canceled" : "timed_out", 0, Encoding.UTF8.GetByteCount(body), null);
    }

    [Theory]
    [InlineData("array")]
    [InlineData("indexed")]
    public async Task Real_customer_YARP_three_rules_payment_legacy_passthrough_and_redacted_metadata(string representation)
    {
        await using var hosts = await Hosts.Start(representation);
        using var customer = await hosts.Client.GetAsync("/protected/customer");
        var bytes = await customer.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, customer.StatusCode);
        AssertCustomer(bytes);
        Assert.Equal(bytes.Length, customer.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", customer.Content.Headers.ContentType!.ToString());
        Assert.True(customer.Headers.CacheControl!.NoStore);
        Assert.False(customer.Headers.Contains("ETag"));
        await hosts.WaitForTerminalAsync(1);
        var metrics = hosts.Telemetry.DashboardMetrics;
        Assert.Equal(3, metrics.ValidatedMasks);
        Assert.Equal(1, metrics.Outcomes.Inspected);
        Assert.Equal(Encoding.UTF8.GetByteCount(Customer), metrics.Input.TotalBytes);
        Assert.Equal(bytes.Length, metrics.Output.TotalBytes);
        Assert.Equal(1, metrics.Input.Samples);
        Assert.Equal(1, metrics.Output.Samples);
        Assert.Equal((0, 0L), hosts.Budget.Snapshot);
        using var passthrough = await hosts.Client.GetAsync("/passthrough/customer");
        Assert.Equal(Encoding.UTF8.GetBytes(Customer), await passthrough.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"raw-customer-v1\"", passthrough.Headers.ETag!.ToString());
        Assert.Equal(3, hosts.Telemetry.DashboardMetrics.ValidatedMasks);
        using var payment = await hosts.Client.GetAsync("/protected/payment");
        using var expectedPayment = JsonDocument.Parse(SyntheticApi.Sample.Replace(SyntheticApi.Pan, "************1111"));
        using var actualPayment = JsonDocument.Parse(await payment.Content.ReadAsByteArrayAsync());
        Assert.True(JsonElement.DeepEquals(expectedPayment.RootElement, actualPayment.RootElement));
        Assert.False(payment.Headers.Contains("Content-Digest"));
        await hosts.WaitForTerminalAsync(2);
        using var rawPayment = await hosts.Client.GetAsync("/passthrough/payment");
        Assert.Equal(Encoding.UTF8.GetBytes(SyntheticApi.Sample), await rawPayment.Content.ReadAsByteArrayAsync());
        var summaryText = await hosts.Client.GetStringAsync("/dashboard/api/summary");
        using var summary = JsonDocument.Parse(summaryText);
        var metadata = summary.RootElement.GetProperty("policies")[0];
        Assert.Equal(3, metadata.GetProperty("ruleCount").GetInt32());
        Assert.Equal(new[] { "PAN", "Email", "Phone" }, metadata.GetProperty("transformations").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("protected-payment", metadata.GetProperty("routeId").GetString());
        Assert.Equal(4, hosts.Telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(2, hosts.Telemetry.DashboardMetrics.Outcomes.Inspected);
        var logs = string.Join('\n', hosts.Logs.Messages);
        Assert.Contains("Protected response inspected", logs);
        foreach (var secret in new[] { SyntheticApi.Pan, "user@example.test", "+1-809-555-0199", "$.contact", Customer, "***@example.test" })
        { Assert.DoesNotContain(secret, logs); Assert.DoesNotContain(secret, summaryText); }
    }

    [Fact]
    public async Task Two_live_hosts_keep_metrics_isolated_even_with_identical_policy_identity()
    {
        await using var first = await Hosts.Start("array");
        await using var second = await Hosts.Start("indexed");
        using var response = await first.Client.GetAsync("/protected/customer");
        AssertCustomer(await response.Content.ReadAsByteArrayAsync());
        await first.WaitForTerminalAsync(1);
        Assert.Equal(3, first.Telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(0, second.Telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(0, second.Telemetry.DashboardMetrics.Input.Samples);
        using var other = await second.Client.GetAsync("/protected/payment");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        await second.WaitForTerminalAsync(1);
        Assert.Equal(1, second.Telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(3, first.Telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(1, first.Telemetry.DashboardMetrics.Outcomes.Inspected);
        Assert.Equal(1, second.Telemetry.DashboardMetrics.Outcomes.Inspected);
    }

    [Fact]
    public async Task Real_default_policy_keeps_customer_contact_and_pre25_payment_contract_unchanged()
    {
        await using var hosts = await Hosts.Start("legacy");
        using var customer = await hosts.Client.GetAsync("/protected/customer");
        using var json = JsonDocument.Parse(await customer.Content.ReadAsByteArrayAsync());
        Assert.Equal("************1111", json.RootElement.GetProperty("payment").GetProperty("cardNumber").GetString());
        Assert.Equal("user@example.test", json.RootElement.GetProperty("contact").GetProperty("email").GetString());
        Assert.Equal("+1-809-555-0199", json.RootElement.GetProperty("contact").GetProperty("phone").GetString());
        await hosts.WaitForTerminalAsync(1);
        Assert.Equal(1, hosts.Telemetry.DashboardMetrics.ValidatedMasks);
        using var payment = await hosts.Client.GetAsync("/protected/payment");
        using var actual = JsonDocument.Parse(await payment.Content.ReadAsByteArrayAsync());
        using var expected = JsonDocument.Parse(SyntheticApi.Sample.Replace(SyntheticApi.Pan, "************1111"));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        await hosts.WaitForTerminalAsync(2);
        Assert.Equal(2, hosts.Telemetry.DashboardMetrics.ValidatedMasks);
    }

    [Theory]
    [InlineData("Mask", false)]
    [InlineData("Mask", true)]
    [InlineData("Block", false)]
    [InlineData("Block", true)]
    public async Task Real_HTTP_fixed_error_cleans_upstream_headers_and_late_last_rule_wins(string mode, bool lateError)
    {
        var body = lateError ? "{\"payment\":{\"cardNumber\":\"4111111111111111\"},\"contact\":{\"email\":\"a@x\",\"phone\":42}}" : Customer;
        await using var hosts = await Hosts.Start("array", body, mode);
        using var response = await hosts.Client.GetAsync("/protected/test");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(lateError || mode == "Block" ? HttpStatusCode.BadGateway : HttpStatusCode.UnprocessableEntity, response.StatusCode);
        if (lateError || mode == "Block")
        {
            Assert.Equal(lateError ? InspectionError : BlockError, Encoding.UTF8.GetString(bytes));
            Assert.False(response.Headers.Contains("X-Upstream-Marker"));
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        else AssertCustomer(bytes);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.Contains("ETag"));
        Assert.False(response.Headers.Contains("Digest"));
        Assert.False(response.Content.Headers.Contains("Last-Modified"));
        await hosts.WaitForTerminalAsync(1);
        Assert.Equal(lateError || mode == "Block" ? 0 : 3, hosts.Telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(lateError ? 1 : 0, hosts.Telemetry.DashboardMetrics.Outcomes.Blocked);
        Assert.Equal(!lateError && mode == "Block" ? 1 : 0, hosts.Telemetry.DashboardMetrics.Outcomes.PolicyBlocked);
        Assert.Equal((0, 0L), hosts.Budget.Snapshot);
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    public async Task Real_HTTP_null_no_match_preserves_exact_UTF8_status_and_zero_masks(string mode)
    {
        const string body = " { \"payment\" : { \"cardNumber\":null }, \"contact\":{\"email\":null,\"phone\":null}, \"other\":\"é\\u0041\" }\r\n";
        await using var hosts = await Hosts.Start("indexed", body, mode);
        using var response = await hosts.Client.GetAsync("/protected/test");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(body), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(Encoding.UTF8.GetByteCount(body), response.Content.Headers.ContentLength);
        await hosts.WaitForTerminalAsync(1);
        Assert.Equal(0, hosts.Telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(1, hosts.Telemetry.DashboardMetrics.Outcomes.Inspected);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), hosts.Telemetry.DashboardMetrics.Input.TotalBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), hosts.Telemetry.DashboardMetrics.Output.TotalBytes);
    }

    [Fact]
    public async Task Terminal_barrier_waits_for_lease_cleanup_even_after_totals_are_published()
    {
        var policyId = "gate-" + Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, subscription) =>
        {
            if (instrument.Meter.Name == PrivacyTelemetry.MeterName
                && instrument.Name == "gateway.privacy.inspection.outcomes"
                && instrument is Counter<long>) subscription.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!tags.ToArray().Any(tag => tag.Key == "gateway.policy.id" && Equals(tag.Value, policyId))) return;
            entered.TrySetResult();
            // The synchronous listener holds publication/cleanup, not the HTTP body flush.
            release.Task.GetAwaiter().GetResult();
        });
        listener.Start();
        await using var hosts = await Hosts.Start("array", policyId: policyId);
        Task<HttpResponseMessage>? request = null;
        Task? terminal = null;
        try
        {
            request = hosts.Client.GetAsync("/protected/customer");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, hosts.Telemetry.DashboardMetrics.Duration.Samples);
            Assert.Equal(1, hosts.Budget.Snapshot.ActiveInspections);
            terminal = hosts.WaitForTerminalAsync(1);
            Assert.False(terminal.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                if (request is not null) { using var response = await request; }
            }
            finally
            {
                if (terminal is not null) await terminal;
            }
        }
        Assert.Equal((0, 0L), hosts.Budget.Snapshot);
        Assert.Equal(3, hosts.Telemetry.DashboardMetrics.ValidatedMasks);
    }

    private static void AssertCustomer(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal("************1111", json.RootElement.GetProperty("payment").GetProperty("cardNumber").GetString());
        Assert.Equal("***@example.test", json.RootElement.GetProperty("contact").GetProperty("email").GetString());
        Assert.Equal("+*-***-***-0199", json.RootElement.GetProperty("contact").GetProperty("phone").GetString());
    }

    private sealed record Measurement(string Name, double Value, KeyValuePair<string, object?>[] Tags, Instrument Source);
    private sealed class Fixture : IDisposable
    {
        public readonly MemoryStream Output = new();
        public readonly CancellationTokenSource Caller = new();
        public readonly ManualClock Clock = new();
        public readonly InspectionBudget Budget = new(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = 4096, InspectionTimeoutSeconds = 1 } }));
        public readonly PrivacyTelemetry Telemetry;
        public readonly ResponseTransformContext Context;
        public readonly ConcurrentQueue<Measurement> Measurements = new();
        private readonly MeterListener listener = new();
        private readonly CompiledPanPolicy policy;
        public Fixture(string body, CompiledPanPolicy policy, Stream? output = null, Stream? input = null)
        {
            this.policy = policy;
            listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == PrivacyTelemetry.MeterName) l.EnableMeasurementEvents(i); };
            listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Capture(i, v, tags));
            listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Capture(i, v, tags));
            listener.Start();
            Telemetry = new(Budget);
            var http = new DefaultHttpContext();
            http.Request.Method = "GET";
            http.Response.Body = output ?? Output;
            http.Response.Headers.ETag = "raw";
            http.Response.Headers["Digest"] = "raw";
            http.Response.Headers["X-Upstream-Marker"] = "raw";
            http.Response.Headers.SetCookie = "raw=1";
            HttpContent content = input is null ? new StringContent(body, Encoding.UTF8, "application/json") : new StreamContent(input);
            content.Headers.ContentType = new("application/json");
            Context = new() { HttpContext = http, ProxyResponse = new(HttpStatusCode.OK) { Content = content }, HeadersCopied = true, CancellationToken = Caller.Token };
        }
        private void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (tags.ToArray().Any(t => t.Key == "gateway.policy.id" && Equals(t.Value, policy.PolicyId)))
                Measurements.Enqueue(new(instrument.Name, value, tags.ToArray(), instrument));
        }
        public Task Run() => new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, Telemetry, Clock).ApplyAsync(Context, policy).AsTask();
        public void AssertError(string expected)
        {
            Assert.Equal(502, Context.HttpContext.Response.StatusCode);
            Assert.Equal(expected, Encoding.UTF8.GetString(Output.ToArray()));
            Assert.False(Context.HttpContext.Response.Headers.ContainsKey("X-Upstream-Marker"));
            Assert.False(Context.HttpContext.Response.Headers.ContainsKey("Set-Cookie"));
        }
        public void AssertCompleted(string outcome, long masks, long? input, long? output, long? explicitMatches = null)
        {
            var matches = explicitMatches ?? masks;
            Assert.True(Context.SuppressResponseBody);
            Assert.Equal((0, 0L), Budget.Snapshot);
            Assert.Equal(masks, Telemetry.DashboardMetrics.ValidatedMasks);
            Assert.Equal(input.HasValue ? 1 : 0, Telemetry.DashboardMetrics.Input.Samples);
            Assert.Equal(input ?? 0, Telemetry.DashboardMetrics.Input.TotalBytes);
            Assert.Equal(output.HasValue ? 1 : 0, Telemetry.DashboardMetrics.Output.Samples);
            Assert.Equal(output ?? 0, Telemetry.DashboardMetrics.Output.TotalBytes);
            Assert.Equal(1, Telemetry.DashboardMetrics.Duration.Samples);
            Assert.Equal(2 + (input.HasValue ? 1 : 0) + (output.HasValue ? 1 : 0) + (masks > 0 ? 1 : 0) + (matches > 0 ? 1 : 0), Measurements.Count);
            var explicitMetrics = Measurements.Where(m => m.Name == "gateway.privacy.inspection.explicit_sensitive_matches");
            if (matches == 0) Assert.Empty(explicitMetrics);
            else
            {
                var metric = Assert.Single(explicitMetrics);
                Assert.Equal(matches, metric.Value);
                Assert.IsType<Counter<long>>(metric.Source);
                Assert.Equal("{match}", metric.Source.Unit);
            }
            Assert.Equal(1, Assert.Single(Measurements, m => m.Name == "gateway.privacy.inspection.outcomes").Value);
            Assert.Single(Measurements, m => m.Name == "gateway.privacy.inspection.duration");
            AssertSize("gateway.privacy.inspection.input_body_bytes", input);
            AssertSize("gateway.privacy.inspection.output_body_bytes", output);
            var maskMetrics = Measurements.Where(m => m.Name == "gateway.privacy.inspection.masks");
            if (masks == 0) Assert.Empty(maskMetrics);
            else
            {
                var metric = Assert.Single(maskMetrics);
                Assert.Equal(masks, metric.Value);
                Assert.IsType<Counter<long>>(metric.Source);
                Assert.Equal("{mask}", metric.Source.Unit);
            }
            var expectedTags = new Dictionary<string, object?> { ["gateway.policy.id"] = policy.PolicyId, ["gateway.policy.version"] = "v1", ["gateway.route.id"] = "protected-payment", ["gateway.privacy.outcome"] = outcome };
            foreach (var metric in Measurements)
            {
                Assert.Equal(expectedTags.OrderBy(t => t.Key), metric.Tags.OrderBy(t => t.Key));
                Assert.Equal("Gateway.Privacy", metric.Source.Meter.Name);
                Assert.Equal("1.0.0", metric.Source.Meter.Version);
                Assert.Same(Measurements.First().Source.Meter, metric.Source.Meter);
            }
            Assert.Equal(output, Context.HttpContext.Response.ContentLength);
            Assert.Equal("no-store", Context.HttpContext.Response.Headers.CacheControl.ToString());
            if (output.HasValue)
            {
                Assert.Equal("application/json; charset=utf-8", Context.HttpContext.Response.ContentType);
                Assert.False(Context.HttpContext.Response.Headers.ContainsKey("ETag"));
                Assert.False(Context.HttpContext.Response.Headers.ContainsKey("Digest"));
            }
            void AssertSize(string name, long? expected)
            {
                var found = Measurements.Where(m => m.Name == name);
                if (expected is null) Assert.Empty(found);
                else { var metric = Assert.Single(found); Assert.Equal(expected.Value, metric.Value); Assert.IsType<Histogram<long>>(metric.Source); Assert.Equal("By", metric.Source.Unit); }
            }
        }
        public void Dispose() { Context.ProxyResponse!.Dispose(); Telemetry.Dispose(); listener.Dispose(); Caller.Dispose(); Output.Dispose(); }
    }

    private sealed class WriteGate : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Attempted;
        public int Calls;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Calls++;
            Attempted = buffer.ToArray();
            Entered.TrySetResult();
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            base.Write(buffer.Span);
        }
    }
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int Reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Reads++; return base.ReadAsync(buffer, cancellationToken); }
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public Action? OnTimestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { OnTimestamp?.Invoke(); return ticks; }
        public void Advance() => ticks += TimeSpan.TicksPerSecond;
        // These tests exercise monotonic Check, deliberately without timer delivery.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private sealed class Hosts(WebApplication api, WebApplication gateway, CapturedLogs logs) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(gateway)), Timeout = TimeSpan.FromSeconds(10) };
        public CapturedLogs Logs => logs;
        public PrivacyTelemetry Telemetry => gateway.Services.GetRequiredService<PrivacyTelemetry>();
        public InspectionBudget Budget => gateway.Services.GetRequiredService<InspectionBudget>();
        public async Task WaitForTerminalAsync(long expectedAdmitted)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var backoffMilliseconds = 1;
            // HTTP delivery can precede Complete and its finally-released lease.
            while (Telemetry.DashboardMetrics.Duration.Samples < expectedAdmitted || Budget.Snapshot != (0, 0L))
            {
                await Task.Delay(backoffMilliseconds, timeout.Token);
                backoffMilliseconds = Math.Min(backoffMilliseconds * 2, 50);
            }
        }
        public static async Task<Hosts> Start(string representation, string? body = null, string mode = "Mask", string? policyId = null)
        {
            var api = SyntheticApi.Build([], b => b.WebHost.UseUrls("http://127.0.0.1:0"), app =>
            {
                if (body is null) return;
                app.MapGet("/test", async context =>
                {
                    context.Response.StatusCode = 422;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    context.Response.Headers["X-Upstream-Marker"] = "synthetic";
                    context.Response.Headers.SetCookie = "synthetic=1";
                    context.Response.Headers.ETag = "\"synthetic\"";
                    context.Response.Headers["Digest"] = "synthetic";
                    context.Response.Headers.LastModified = "Wed, 01 Jan 2025 00:00:00 GMT";
                    await context.Response.WriteAsync(body, context.RequestAborted);
                });
            });
            WebApplication? gateway = null;
            try
            {
                await api.StartAsync();
                var logs = new CapturedLogs();
                const string rules = "{\"Selector\":\"$.contact.email\",\"Transformation\":\"Email\"},{\"Selector\":\"$.contact.phone\",\"Transformation\":\"Phone\"}";
                var additional = representation == "array" ? "[" + rules + "]" : "{\"1\":{\"Selector\":\"$.contact.phone\",\"Transformation\":\"Phone\"},\"0\":{\"Selector\":\"$.contact.email\",\"Transformation\":\"Email\"}}";
                var values = PolicyConfiguration.Defaults();
                values["PanPolicies:Policies:0:Mode"] = mode;
                if (policyId is not null) values["PanPolicies:Policies:0:PolicyId"] = policyId;
                gateway = GatewayHost.Build([], Address(api) + "/", b =>
                {
                    b.Configuration.Sources.Clear();
                    b.Configuration.AddInMemoryCollection(values);
                    if (representation != "legacy") b.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("{\"PanPolicies\":{\"Policies\":[{\"AdditionalRules\":" + additional + "}]}}")));
                    b.WebHost.UseUrls("http://127.0.0.1:0");
                    b.Logging.ClearProviders();
                    b.Logging.AddProvider(logs);
                });
                await gateway.StartAsync();
                return new(api, gateway, logs);
            }
            catch { if (gateway is not null) await gateway.DisposeAsync(); await api.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await gateway.DisposeAsync(); await api.DisposeAsync(); }
    }
}
