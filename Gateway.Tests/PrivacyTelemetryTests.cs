using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TestApi;
using Yarp.ReverseProxy.Transforms;
using Xunit;

namespace Gateway.Tests;

[Collection("Secondary PAN telemetry isolation")]
public sealed class PrivacyTelemetryTests
{
    private const int ReservedBodyBytes = 1024;
    private const string JsonBody = "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}";
    private const string Outcomes = "gateway.privacy.inspection.outcomes";
    private const string Duration = "gateway.privacy.inspection.duration";
    private const string InputSize = "gateway.privacy.inspection.input_body_bytes";
    private const string OutputSize = "gateway.privacy.inspection.output_body_bytes";
    private const string Masks = "gateway.privacy.inspection.masks";
    private const string Active = "gateway.privacy.inspection.active";
    private const string Reserved = "gateway.privacy.inspection.reserved_body_bytes";

    [Theory]
    [InlineData("strings", "Mask", 4)]
    [InlineData("strings", "Block", 0)]
    [InlineData("null", "Mask", 0)]
    [InlineData("missing", "Mask", 0)]
    [InlineData("late-invalid", "Mask", 0)]
    public async Task Masks_count_all_selected_strings_only_after_valid_traversal(string scenario, string mode, long expected)
    {
        var body = scenario switch
        {
            "strings" => "{\"cards\":[{\"number\":\"4111111111111111\",\"number\":\"\"},{\"number\":\"not-a-pan\"},{\"number\":null},{\"number\":\"1234\"}]}",
            "null" => "{\"cards\":[{\"number\":null}]}",
            "missing" => "{ \"cards\":[{},null,42,[]],\"other\":\"4111111111111111\" }",
            "late-invalid" => "{\"cards\":[{\"number\":\"4111111111111111\"},{\"number\":\"valid\",\"number\":42}]}",
            _ => throw new ArgumentException(nameof(scenario))
        };
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = new CompiledPanPolicy("MasksTraversal", "v1", "protected-payment", ["cards", "[*]", "number"], ReservedBodyBytes) { Mode = mode };
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(body), downstream);
        await invocation.ApplyAsync();
        var outcome = scenario == "late-invalid" ? "blocked" : scenario == "strings" && mode == "Block" ? "policy_blocked" : "inspected";
        AssertOutcomeAndDuration(metrics, policy, outcome);
        AssertMasks(metrics, policy, outcome, expected);
        Assert.Equal(outcome == "inspected" ? 200 : 502, invocation.Context.HttpContext.Response.StatusCode);
        if (scenario is "null" or "missing") Assert.Equal(body, Encoding.UTF8.GetString(downstream.ToArray()));
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Masks_require_post_traversal_deadline_check(bool clientCanceled)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("MasksTraversalDeadline");
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualBlockClock();
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream, cancellation.Token, clock: clock);
        var traversing = false;
        var triggered = false;
        clock.TimestampObserved = () =>
        {
            // Observe the real traversal boundary without a production test hook or wall-clock race.
            var inTraversal = new System.Diagnostics.StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == "WriteSelected");
            if (inTraversal) { traversing = true; return; }
            if (!traversing) return;
            clock.TimestampObserved = null;
            triggered = true;
            if (clientCanceled) cancellation.Cancel();
            else clock.Advance(TimeSpan.FromSeconds(10));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.ApplyAsync());
        Assert.True(triggered);
        AssertOutcomeAndDuration(metrics, policy, clientCanceled ? "canceled" : "timed_out");
        AssertMasks(metrics, policy, clientCanceled ? "canceled" : "timed_out", 0);
        Assert.Empty(downstream.ToArray());
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Fact]
    public async Task Masks_survive_header_preparation_failure_after_validation()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("MasksHeaderFailure");
        using var downstream = new ControlledWriteStream();
        TransformInvocation? invocation = null;
        using (invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream,
            diagnosticCheckpoint: _ =>
            {
                ((HeaderDictionary)invocation!.Context.HttpContext.Response.Headers).IsReadOnly = true;
                return ValueTask.CompletedTask;
            }))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => invocation.ApplyAsync());
            Assert.False(downstream.WriteStarted.IsCompleted);
            AssertOutcomeAndDuration(metrics, policy, "faulted");
            AssertMasks(metrics, policy, "faulted", 1);
            Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
            Assert.Equal((0, 0L), budget.Snapshot);
        }
    }

    [Fact]
    public async Task Cancellation_before_inspection_emits_no_masks_or_terminal_metrics()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("MasksPreCanceled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.ApplyAsync());
        Assert.Empty(metrics.ForPolicy(Masks, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(Outcomes, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(Duration, policy.PolicyId));
        Assert.Empty(downstream.ToArray());
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData("escaped-mask")]
    [InlineData("original")]
    [InlineData("null")]
    [InlineData("block")]
    [InlineData("invalid-json")]
    [InlineData("invalid-terminal")]
    public async Task Prepared_output_records_exact_final_UTF8_bytes_not_input_or_discarded_buffer(string scenario)
    {
        const string unicodeBody = "{ \"payment\" : {\"cardNumber\":\"4111-1111-1111-1111\"}, \"note\":\"é😀\\n\" }\n";
        var body = scenario switch
        {
            "original" => "{ \"other\" : \"é😀\\n\" }\n",
            "null" => "{ \"payment\" : {\"cardNumber\":null}, \"note\":\"é😀\" }\n",
            "invalid-json" => "{INVALID",
            "invalid-terminal" => "{\"payment\":{\"cardNumber\":\"selected\",\"cardNumber\":42}}",
            _ => unicodeBody
        };
        var expected = scenario switch
        {
            "escaped-mask" => """{"payment":{"cardNumber":"****-****-****-1111"},"note":"\u00E9\uD83D\uDE00\n"}""",
            "block" => "{\"code\":\"privacy_response_blocked\"}",
            "invalid-json" or "invalid-terminal" => "{\"code\":\"privacy_inspection_failed\"}",
            _ => body
        };
        var outcome = scenario == "block" ? "policy_blocked"
            : scenario is "invalid-json" or "invalid-terminal" ? "blocked" : "inspected";
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("OutputSizeExact") with { Mode = scenario == "block" ? "Block" : "Mask" };
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(body), downstream);

        await invocation.ApplyAsync();

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        Assert.Equal(expectedBytes, downstream.ToArray());
        Assert.Equal((long)expectedBytes.Length, invocation.Context.HttpContext.Response.ContentLength);
        Assert.Equal(outcome == "inspected" ? 200 : 502, invocation.Context.HttpContext.Response.StatusCode);
        AssertOutcomeAndDuration(metrics, policy, outcome);
        AssertInputSize(metrics, policy, outcome, Encoding.UTF8.GetByteCount(body));
        AssertOutputSize(metrics, policy, outcome, expectedBytes.Length);
        AssertMasks(metrics, policy, outcome, scenario == "escaped-mask" ? 1 : 0);
        if (scenario == "original") Assert.NotEqual(expected.Length, expectedBytes.Length);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_check_after_headers_prevents_output_sample_when_deadline_or_client_wins(bool clientCanceled)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("OutputSizeFinalCheck");
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualBlockClock();
        using var downstream = new ControlledWriteStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream,
            cancellation.Token, clock: clock);
        var checksWithHeaders = 0;
        clock.TimestampObserved = () =>
        {
            if (invocation.Context.HttpContext.Response.ContentLength is null || ++checksWithHeaders != 2) return;
            clock.TimestampObserved = null;
            if (clientCanceled) cancellation.Cancel();
            else clock.Advance(TimeSpan.FromSeconds(10));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.ApplyAsync());

        var outcome = clientCanceled ? "canceled" : "timed_out";
        Assert.Equal(2, checksWithHeaders);
        Assert.False(downstream.WriteStarted.IsCompleted);
        Assert.NotNull(invocation.Context.HttpContext.Response.ContentLength);
        AssertOutcomeAndDuration(metrics, policy, outcome);
        AssertInputSize(metrics, policy, outcome, Encoding.UTF8.GetByteCount(JsonBody));
        Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
        AssertMasks(metrics, policy, outcome, 1);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData("Mask", "{\"payment\":{\"cardNumber\":\"é😀\"}}", "inspected")]
    [InlineData("Block", "{\"payment\":{\"cardNumber\":\"é😀\"}}", "policy_blocked")]
    [InlineData("Mask", "{\"other\":\"é😀\"}", "inspected")]
    [InlineData("Block", "{\"other\":\"é😀\"}", "inspected")]
    [InlineData("Mask", "", "blocked")]
    [InlineData("Block", "", "blocked")]
    [InlineData("Mask", "{INVALID", "blocked")]
    [InlineData("Mask", "{\"payment\":{\"cardNumber\":\"selected\",\"cardNumber\":42}}", "blocked")]
    public async Task Complete_capture_records_real_UTF8_bytes_including_empty_with_final_outcome(string mode, string body, string outcome)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("InputSizeComplete") with { Mode = mode };
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(body), downstream);
        await invocation.ApplyAsync();
        Assert.Equal(outcome == "inspected" ? 200 : 502, invocation.Context.HttpContext.Response.StatusCode);
        AssertOutcomeAndDuration(metrics, policy, outcome);
        AssertInputSize(metrics, policy, outcome, Encoding.UTF8.GetByteCount(body));
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData("charset")]
    [InlineData("encoding")]
    [InlineData("declared-size")]
    [InlineData("partial-status")]
    [InlineData("oversize")]
    [InlineData("read-error")]
    [InlineData("pre-read-error")]
    public async Task Incomplete_capture_and_preflight_rejection_emit_no_input_size_but_prepare_error_output(string scenario)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("InputSizeIncomplete");
        using var source = new FragmentedInputStream(
            Encoding.UTF8.GetBytes(scenario == "oversize" ? new string('x', ReservedBodyBytes + 1) : JsonBody),
            scenario == "read-error" ? 1 : scenario == "pre-read-error" ? 0 : null);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (scenario == "charset") content.Headers.ContentType.CharSet = "utf-16";
        if (scenario == "encoding") content.Headers.ContentEncoding.Add("gzip");
        if (scenario == "declared-size") content.Headers.ContentLength = ReservedBodyBytes + 1;
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, content, downstream,
            statusCode: scenario == "partial-status" ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
        await invocation.ApplyAsync();
        Assert.Equal(502, invocation.Context.HttpContext.Response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", Encoding.UTF8.GetString(downstream.ToArray()));
        AssertOutcomeAndDuration(metrics, policy, "blocked");
        Assert.Empty(metrics.ForPolicy(InputSize, policy.PolicyId));
        AssertOutputSize(metrics, policy, "blocked", 36);
        AssertMasks(metrics, policy, "blocked", 0);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Fact]
    public async Task Complete_capture_uses_EOF_bytes_not_declared_length_and_invalid_UTF8_still_records_size()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("InputSizeInvalidUtf8");
        using var source = new FragmentedInputStream([0xff, 0xfe, 0xfd]);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = 1;
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, content, downstream);
        await invocation.ApplyAsync();
        Assert.Equal(502, invocation.Context.HttpContext.Response.StatusCode);
        AssertInputSize(metrics, policy, "blocked", 3);
        AssertOutputSize(metrics, policy, "blocked", 36);
        AssertMasks(metrics, policy, "blocked", 0);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Size_boundary_records_only_complete_capture_at_or_below_limit(int offset)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("InputSizeBoundary");
        var body = "{\"other\":\"" + new string('x', ReservedBodyBytes + offset - 12) + "\"}";
        using var source = new FragmentedInputStream(Encoding.UTF8.GetBytes(body));
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, content, downstream);
        await invocation.ApplyAsync();
        Assert.Equal(offset > 0 ? 502 : 200, invocation.Context.HttpContext.Response.StatusCode);
        if (offset > 0) Assert.Empty(metrics.ForPolicy(InputSize, policy.PolicyId));
        else AssertInputSize(metrics, policy, "inspected", ReservedBodyBytes + offset);
        AssertOutputSize(metrics, policy, offset > 0 ? "blocked" : "inspected", offset > 0 ? 36 : ReservedBodyBytes + offset);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_or_cancellation_at_EOF_checkpoint_does_not_mark_capture_complete(bool clientCanceled)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("InputSizeEofDeadline");
        var clock = new ManualBlockClock();
        using var cancellation = new CancellationTokenSource();
        using var source = new FragmentedInputStream(Encoding.UTF8.GetBytes(JsonBody), onEof: () =>
        {
            if (clientCanceled) cancellation.Cancel();
            else clock.Advance(TimeSpan.FromSeconds(10));
        });
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, content, downstream, cancellation.Token, clock: clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.ApplyAsync());
        AssertOutcomeAndDuration(metrics, policy, clientCanceled ? "canceled" : "timed_out");
        Assert.Empty(metrics.ForPolicy(InputSize, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
        Assert.Empty(downstream.ToArray());
        AssertMasks(metrics, policy, clientCanceled ? "canceled" : "timed_out", 0);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Fact]
    public async Task Successful_and_blocked_inspections_record_exact_outcomes_and_durations()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var inspected = Policy("TelemetryInspected");
        var blocked = Policy("TelemetryBlocked");

        using (var downstream = new MemoryStream())
        using (var invocation = Invocation(telemetry, inspected, JsonContent(JsonBody), downstream))
        {
            await invocation.ApplyAsync();
            Assert.Equal(StatusCodes.Status200OK, invocation.Context.HttpContext.Response.StatusCode);
            Assert.Contains("************1111", Encoding.UTF8.GetString(downstream.ToArray()));
        }

        using (var downstream = new MemoryStream())
        using (var invocation = Invocation(telemetry, blocked, JsonContent("{INVALID"), downstream))
        {
            await invocation.ApplyAsync();
            Assert.Equal(StatusCodes.Status502BadGateway, invocation.Context.HttpContext.Response.StatusCode);
            Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", Encoding.UTF8.GetString(downstream.ToArray()));
        }

        AssertOutcomeAndDuration(metrics, inspected, "inspected");
        AssertOutcomeAndDuration(metrics, blocked, "blocked");
        AssertInputSize(metrics, inspected, "inspected", Encoding.UTF8.GetByteCount(JsonBody));
        AssertInputSize(metrics, blocked, "blocked", 8);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Fact]
    public async Task Capacity_denial_records_exact_outcome_without_acquiring_a_lease()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("TelemetryCapacity");
        using var held = budget.TryAcquire(1);
        Assert.NotNull(held);

        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream);
        await invocation.ApplyAsync();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, invocation.Context.HttpContext.Response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_capacity_exceeded\"}", Encoding.UTF8.GetString(downstream.ToArray()));
        AssertOutcome(metrics, policy, "capacity_exceeded");
        Assert.Empty(metrics.ForPolicy(Duration, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(InputSize, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
        Assert.Equal((1, 1L), budget.Snapshot);
        AssertMasks(metrics, policy, "capacity_exceeded", 0);
    }

    [Fact]
    public async Task Successful_outcome_and_duration_wait_for_downstream_write()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("TelemetryPendingWrite");
        using var downstream = new ControlledWriteStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream);

        var pending = invocation.ApplyAsync();
        await downstream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);
        Assert.Empty(metrics.ForPolicy(Outcomes, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(Duration, policy.PolicyId));

        Assert.Empty(metrics.ForPolicy(InputSize, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(Masks, policy.PolicyId));
        downstream.Complete();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        AssertOutcomeAndDuration(metrics, policy, "inspected");
        AssertInputSize(metrics, policy, "inspected", Encoding.UTF8.GetByteCount(JsonBody));
        AssertOutputSize(metrics, policy, "inspected", 45);
        AssertMasks(metrics, policy, "inspected", 1);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Fact]
    public async Task Diagnostic_checkpoint_precedes_downstream_write_and_keeps_lease()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("TelemetryDiagnosticCheckpoint");
        var checkpointEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpointRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var downstream = new ControlledWriteStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream,
            diagnosticCheckpoint: async cancellationToken =>
            {
                checkpointEntered.TrySetResult();
                await checkpointRelease.Task.WaitAsync(cancellationToken);
            });

        var pending = invocation.ApplyAsync();
        await checkpointEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);
        Assert.False(downstream.WriteStarted.IsCompleted);
        Assert.Empty(metrics.ForPolicy(Outcomes, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(Duration, policy.PolicyId));

        checkpointRelease.TrySetResult();
        await downstream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);

        downstream.Complete();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        AssertOutcomeAndDuration(metrics, policy, "inspected");
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    public async Task Diagnostic_checkpoint_cancellation_records_canceled_and_releases_lease(string mode)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("TelemetryCheckpointCanceled") with { Mode = mode };
        using var cancellation = new CancellationTokenSource();
        var checkpointEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var downstream = new ControlledWriteStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream,
            cancellation.Token, diagnosticCheckpoint: async cancellationToken =>
            {
                checkpointEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        var pending = invocation.ApplyAsync();
        await checkpointEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.False(downstream.WriteStarted.IsCompleted);
        AssertOutcomeAndDuration(metrics, policy, "canceled");
        AssertInputSize(metrics, policy, "canceled", Encoding.UTF8.GetByteCount(JsonBody));
        AssertMasks(metrics, policy, "canceled", mode == "Mask" ? 1 : 0);
        Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    public async Task Diagnostic_checkpoint_exception_records_faulted_and_releases_lease(string mode)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("TelemetryCheckpointFaulted") with { Mode = mode };
        var failure = new InvalidOperationException("Synthetic diagnostic checkpoint failure.");
        using var downstream = new ControlledWriteStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream,
            diagnosticCheckpoint: _ => ValueTask.FromException(failure));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => invocation.ApplyAsync());

        Assert.Same(failure, actual);
        Assert.False(downstream.WriteStarted.IsCompleted);
        AssertOutcomeAndDuration(metrics, policy, "faulted");
        AssertInputSize(metrics, policy, "faulted", Encoding.UTF8.GetByteCount(JsonBody));
        AssertMasks(metrics, policy, "faulted", mode == "Mask" ? 1 : 0);
        Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Fact]
    public async Task Cancellation_and_downstream_write_failure_record_exact_outcomes()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var canceled = Policy("TelemetryWriteCanceled");
        var writeError = Policy("TelemetryWriteError");

        using (var cancellation = new CancellationTokenSource())
        using (var downstream = new ControlledWriteStream())
        using (var invocation = Invocation(telemetry, canceled, JsonContent(JsonBody), downstream,
                   cancellation.Token))
        {
            var pending = invocation.ApplyAsync();
            await downstream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal((0, 0L), budget.Snapshot);
        }

        using (var downstream = new ControlledWriteStream())
        using (var invocation = Invocation(telemetry, writeError, JsonContent(JsonBody), downstream))
        {
            var pending = invocation.ApplyAsync();
            await downstream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);

            var failure = new IOException("Synthetic downstream write failure.");
            downstream.Fail(failure);
            var actual = await Assert.ThrowsAsync<IOException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, actual);
            Assert.Equal((0, 0L), budget.Snapshot);
        }

        AssertOutcomeAndDuration(metrics, canceled, "canceled");
        AssertOutcomeAndDuration(metrics, writeError, "write_error");
        AssertInputSize(metrics, canceled, "canceled", Encoding.UTF8.GetByteCount(JsonBody));
        AssertInputSize(metrics, writeError, "write_error", Encoding.UTF8.GetByteCount(JsonBody));
        AssertOutputSize(metrics, canceled, "canceled", 45);
        AssertOutputSize(metrics, writeError, "write_error", 45);
        AssertMasks(metrics, canceled, "canceled", 1);
        AssertMasks(metrics, writeError, "write_error", 1);
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    public async Task Cancellation_during_upstream_read_records_canceled_outcome(string mode)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("TelemetryReadCanceled") with { Mode = mode };
        using var cancellation = new CancellationTokenSource();
        using var source = new BlockingReadStream();
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, content, downstream, cancellation.Token);

        var pending = invocation.ApplyAsync();
        await source.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertOutcomeAndDuration(metrics, policy, "canceled");
        Assert.Equal((0, 0L), budget.Snapshot);
        Assert.Empty(downstream.ToArray());
        AssertMasks(metrics, policy, "canceled", 0);
        Assert.Empty(metrics.ForPolicy(InputSize, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
    }

    [Fact]
    public void Measurement_completion_is_idempotent_and_dispose_records_faulted_once()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("TelemetryFaulted");
        var measurement = telemetry.TryStartInspection(policy);
        Assert.NotNull(measurement);
        Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);

        measurement.Dispose();
        measurement.Dispose();
        measurement.Complete(PrivacyInspectionOutcome.Inspected);

        AssertOutcomeAndDuration(metrics, policy, "faulted");
        Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Fact]
    public void Concurrent_measurement_completion_records_one_outcome_and_releases_once()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = Policy("TelemetryConcurrent");
        var measurement = telemetry.TryStartInspection(policy);
        Assert.NotNull(measurement);

        measurement.CapturedInputBodyBytes = 17;
        measurement.PreparedOutputBodyBytes = 23;
        Parallel.For(0, 100, index => measurement.Complete(index % 2 == 0
            ? PrivacyInspectionOutcome.Inspected
            : PrivacyInspectionOutcome.Canceled));

        var outcome = Assert.Single(metrics.ForPolicy(Outcomes, policy.PolicyId));
        Assert.Contains(outcome.Tags["gateway.privacy.outcome"], new[] { "inspected", "canceled" });
        Assert.Single(metrics.ForPolicy(Duration, policy.PolicyId));
        measurement.Dispose();
        AssertInputSize(metrics, policy, outcome.Tags["gateway.privacy.outcome"]!, 17);
        AssertOutputSize(metrics, policy, outcome.Tags["gateway.privacy.outcome"]!, 23);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Fact]
    public void Observable_capacity_gauges_match_budget_snapshot()
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: 777);
        using var telemetry = new PrivacyTelemetry(budget);
        var measurement = telemetry.TryStartInspection(Policy("TelemetryGauges", maxBodyBytes: 777));
        Assert.NotNull(measurement);

        metrics.Clear();
        metrics.RecordObservableInstruments();
        var reserved = Assert.Single(metrics.ForInstrument(Reserved), item => item.Value == 777);
        Assert.Equal("By", reserved.Unit);
        var active = Assert.Single(metrics.ForInstrument(Active), item =>
            ReferenceEquals(item.Meter, reserved.Meter) && item.Value == 1);
        Assert.Equal("{inspection}", active.Unit);
        Assert.Equal((1, 777L), budget.Snapshot);

        measurement.Complete(PrivacyInspectionOutcome.Inspected);
        metrics.Clear();
        metrics.RecordObservableInstruments();
        Assert.Contains(metrics.ForInstrument(Reserved), item =>
            ReferenceEquals(item.Meter, reserved.Meter) && item.Value == 0);
        Assert.Contains(metrics.ForInstrument(Active), item =>
            ReferenceEquals(item.Meter, reserved.Meter) && item.Value == 0);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    [InlineData("Observe")]
    public async Task Exclusions_publish_once_with_precedence_without_reading_or_using_budget(string mode)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var scenarios = new[]
        {
            ("head", HttpMethods.Head, HttpStatusCode.OK, "application/json", "excluded_head"),
            ("head204", HttpMethods.Head, HttpStatusCode.NoContent, "text/plain", "excluded_head"),
            ("head304", HttpMethods.Head, HttpStatusCode.NotModified, "invalid", "excluded_head"),
            ("204", HttpMethods.Get, HttpStatusCode.NoContent, "text/plain", "excluded_bodyless"),
            ("304", HttpMethods.Get, HttpStatusCode.NotModified, (string?)null, "excluded_bodyless"),
            ("plain", HttpMethods.Get, HttpStatusCode.OK, "text/plain", "excluded_media_type"),
            ("missing", HttpMethods.Get, HttpStatusCode.OK, (string?)null, "excluded_media_type"),
            ("invalid", HttpMethods.Get, HttpStatusCode.OK, "invalid", "excluded_media_type"),
            ("suffix", HttpMethods.Get, HttpStatusCode.OK, "application/problem+json", "excluded_media_type"),
            ("textjson", HttpMethods.Get, HttpStatusCode.OK, "text/json", "excluded_media_type")
        };
        using var held = budget.TryAcquire(1);
        Assert.NotNull(held);
        var before = telemetry.DashboardMetrics;
        foreach (var (id, method, status, mediaType, expected) in scenarios)
        {
            var policy = Policy("Exclusion" + id) with { Mode = mode };
            using var content = new ForbiddenReadContent();
            if (mediaType is not null) content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
            using var downstream = new MemoryStream();
            using var invocation = Invocation(telemetry, policy, content, downstream,
                method: method, statusCode: status);
            await invocation.ApplyAsync();
            var sample = AssertOutcome(metrics, policy, expected);
            Assert.IsType<Counter<long>>(sample.Instrument);
            Assert.Single(metrics.ForPolicyAll(policy.PolicyId));
            Assert.Equal(method == HttpMethods.Head || (int)status is 204 or 304, invocation.Context.SuppressResponseBody);
            Assert.Equal((int)status, invocation.Context.HttpContext.Response.StatusCode);
            Assert.Equal("no-store", invocation.Context.HttpContext.Response.Headers.CacheControl);
            Assert.Empty(downstream.ToArray());
            Assert.False(content.ReadAttempted);
            Assert.Equal((1, 1L), budget.Snapshot);
            Assert.Equal(before, telemetry.DashboardMetrics);
        }
        Assert.Equal(8, Enum.GetValues<PrivacyInspectionOutcome>().Length);
    }

    [Fact]
    public async Task Route_without_policy_emits_no_outcomes()
    {
        using var metrics = new MetricCollector();
        var values = PolicyConfiguration.Defaults();
        const string policyId = "TelemetryPassthroughGuard";
        values["PanPolicies:Policies:0:PolicyId"] = policyId;
        await using var api = SyntheticApi.Build([], builder => builder.WebHost.UseUrls("http://127.0.0.1:0"));
        await api.StartAsync();
        await using var gateway = GatewayHost.Build([], Address(api) + "/", builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(values);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        });
        try
        {
            await gateway.StartAsync();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = new Uri(Address(gateway)),
                Timeout = TimeSpan.FromSeconds(15)
            };

            var body = await client.GetStringAsync("/passthrough/payment");

            Assert.Contains(SyntheticApi.Pan, body);
            Assert.Empty(metrics.ForPolicy(Outcomes, policyId));
            Assert.Empty(metrics.ForPolicy(Duration, policyId));
            Assert.Empty(metrics.ForPolicy(InputSize, policyId));
            Assert.Empty(metrics.ForPolicy(OutputSize, policyId));
            Assert.Empty(metrics.ForPolicy(Masks, policyId));
        }
        finally
        {
            await gateway.StopAsync();
            await api.StopAsync();
        }
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    [InlineData("Observe")]
    public async Task Eligible_json_media_variants_keep_existing_inspection(string mode)
    {
        foreach (var media in new[] { "application/json", "APPLICATION/JSON", "application/json; charset=utf-8", "application/json; charset=\"UTF-8\"", "application/json; profile=synthetic" })
        {
            using var metrics = new MetricCollector();
            var policy = Policy("EligibleJson") with { Mode = mode };
            using var telemetry = new PrivacyTelemetry(Budget(1, ReservedBodyBytes));
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonBody));
            content.Headers.TryAddWithoutValidation("Content-Type", media);
            using var downstream = new MemoryStream();
            using var invocation = Invocation(telemetry, policy, content, downstream);
            await invocation.ApplyAsync();
            AssertOutcomeAndDuration(metrics, policy, mode == "Block" ? "policy_blocked" : "inspected");
            Assert.Equal(mode == "Block" ? "{\"code\":\"privacy_response_blocked\"}" : mode == "Observe" ? JsonBody
                : "{\"payment\":{\"cardNumber\":\"************1111\"}}", Encoding.UTF8.GetString(downstream.ToArray()));
        }
    }

    [Theory]
    [InlineData("head")]
    [InlineData("bodyless")]
    [InlineData("media")]
    public async Task Exclusion_listener_failure_is_isolated_and_no_upstream_emits_nothing(string scenario)
    {
        var policy = Policy("ExcludedThrower");
        foreach (var failure in new Exception[] { new InvalidOperationException(), new OperationCanceledException() })
        {
            using var metrics = new MetricCollector(Outcomes, policy.PolicyId, failure);
            var budget = Budget(1, ReservedBodyBytes);
            using var telemetry = new PrivacyTelemetry(budget);
            using var downstream = new MemoryStream();
            using var invocation = Invocation(telemetry, policy, new ForbiddenReadContent(), downstream,
                method: scenario == "head" ? "HEAD" : "GET", statusCode: scenario == "bodyless" ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            var before = telemetry.DashboardMetrics;
            await invocation.ApplyAsync();
            AssertOutcome(metrics, policy, scenario == "head" ? "excluded_head" : scenario == "bodyless" ? "excluded_bodyless" : "excluded_media_type");
            Assert.Single(metrics.ForPolicyAll(policy.PolicyId));
            Assert.Equal(before, telemetry.DashboardMetrics);
            Assert.Empty(downstream.ToArray());
            metrics.Clear();
            var noUpstream = new ResponseTransformContext
            {
                HttpContext = invocation.Context.HttpContext,
                ProxyResponse = null
            };
            await new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, telemetry).ApplyAsync(noUpstream, policy);
            Assert.Empty(metrics.ForPolicyAll(policy.PolicyId));
        }
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    [InlineData("Observe")]
    public async Task Real_http_exclusions_preserve_response_and_leave_dashboard_unchanged(string mode)
    {
        var values = PolicyConfiguration.Defaults();
        const string id = "RealExclusions";
        values["PanPolicies:Policies:0:PolicyId"] = id;
        values["PanPolicies:Policies:0:Mode"] = mode;
        using var metrics = new MetricCollector(Outcomes, id);
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        api.Map("/{**rest}", async context =>
        {
            context.Response.StatusCode = context.Request.Path.Value switch { "/204" => 204, "/304" => 304, _ => 200 };
            context.Response.ContentType = "text/plain";
            context.Response.Headers["X-Synthetic"] = "preserved";
            if (!HttpMethods.IsHead(context.Request.Method) && context.Response.StatusCode == 200)
                await context.Response.WriteAsync("synthetic uninspected body");
        });
        await api.StartAsync();
        await using var gateway = GatewayHost.Build([], Address(api) + "/", b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(values);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        try
        {
            await gateway.StartAsync();
            var telemetry = gateway.Services.GetRequiredService<PrivacyTelemetry>();
            var before = telemetry.DashboardMetrics;
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(15) };
            foreach (var (method, path, status, outcome) in new[]
            {
                (HttpMethod.Head, "/204", 204, "excluded_head"),
                (HttpMethod.Get, "/204", 204, "excluded_bodyless"),
                (HttpMethod.Get, "/304", 304, "excluded_bodyless"),
                (HttpMethod.Get, "/plain", 200, "excluded_media_type")
            })
            {
                metrics.Clear();
                using var request = new HttpRequestMessage(method, "/protected" + path);
                using var response = await client.SendAsync(request);
                Assert.Equal(status, (int)response.StatusCode);
                Assert.Equal("preserved", Assert.Single(response.Headers.GetValues("X-Synthetic")));
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                Assert.Equal(status == 200 ? "synthetic uninspected body" : "", await response.Content.ReadAsStringAsync());
                // Publication is synchronous and precedes the exclusion return/body forwarding.
                // Completed HTTP response + exactly one captured callback is the exclusion barrier;
                // Duration.Samples cannot advance on this path. The thrower captures before throwing.
                var sample = Assert.Single(metrics.ForPolicyAll(id));
                Assert.Equal(outcome, sample.Tags["gateway.privacy.outcome"]);
                Assert.Equal(4, sample.Tags.Count);
                Assert.Equal(1, sample.Value);
                Assert.Equal(Outcomes, sample.InstrumentName);
                Assert.Equal(before, telemetry.DashboardMetrics);
                Assert.Equal((0, 0L), gateway.Services.GetRequiredService<InspectionBudget>().Snapshot);
            }
            metrics.Clear();
            Assert.Equal("synthetic uninspected body", await client.GetStringAsync("/passthrough/plain"));
            Assert.Empty(metrics.ForPolicyAll(id));
            Assert.Equal(before, telemetry.DashboardMetrics);
        }
        finally { await gateway.StopAsync(); await api.StopAsync(); }
    }

    [Theory]
    [InlineData(Outcomes, false, "Mask")]
    [InlineData(Duration, false, "Mask")]
    [InlineData(Outcomes, true, "Mask")]
    [InlineData(Duration, true, "Mask")]
    [InlineData(Outcomes, false, "Block")]
    [InlineData(Duration, false, "Block")]
    [InlineData(Outcomes, true, "Block")]
    [InlineData(Duration, true, "Block")]
    [InlineData(InputSize, false, "Mask")]
    [InlineData(InputSize, true, "Mask")]
    [InlineData(InputSize, false, "Block")]
    [InlineData(InputSize, true, "Block")]
    [InlineData(OutputSize, false, "Mask")]
    [InlineData(OutputSize, true, "Mask")]
    [InlineData(OutputSize, false, "Block")]
    [InlineData(OutputSize, true, "Block")]
    [InlineData(Masks, false, "Mask")]
    public async Task Throwing_listener_preserves_masked_or_blocked_HTTP_response(string instrument, bool blocked, string mode)
    {
        var policy = Policy("TelemetryThrowingHttp") with { Mode = mode };
        using var metrics = new MetricCollector(instrument, policy.PolicyId);
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(blocked ? "{INVALID" : JsonBody), downstream);

        await invocation.ApplyAsync();

        Assert.Equal(blocked || mode == "Block" ? StatusCodes.Status502BadGateway : StatusCodes.Status200OK,
            invocation.Context.HttpContext.Response.StatusCode);
        Assert.Equal(blocked ? "{\"code\":\"privacy_inspection_failed\"}"
            : mode == "Block" ? "{\"code\":\"privacy_response_blocked\"}"
            : "{\"payment\":{\"cardNumber\":\"************1111\"}}", Encoding.UTF8.GetString(downstream.ToArray()));
        AssertOutcomeAndDuration(metrics, policy, blocked ? "blocked" : mode == "Block" ? "policy_blocked" : "inspected");
        AssertInputSize(metrics, policy, blocked ? "blocked" : mode == "Block" ? "policy_blocked" : "inspected",
            blocked ? 8 : Encoding.UTF8.GetByteCount(JsonBody));
        AssertOutputSize(metrics, policy, blocked ? "blocked" : mode == "Block" ? "policy_blocked" : "inspected",
            blocked ? 36 : mode == "Block" ? 35 : 45);
        AssertMasks(metrics, policy, "inspected", !blocked && mode == "Mask" ? 1 : 0);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Fact]
    public async Task Throwing_counter_listener_preserves_capacity_denial_contract()
    {
        var policy = Policy("TelemetryThrowingCapacity");
        using var metrics = new MetricCollector(Outcomes, policy.PolicyId);
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var held = budget.TryAcquire(1);
        Assert.NotNull(held);
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream);

        await invocation.ApplyAsync();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, invocation.Context.HttpContext.Response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_capacity_exceeded\"}", Encoding.UTF8.GetString(downstream.ToArray()));
        AssertOutcome(metrics, policy, "capacity_exceeded");
        Assert.Empty(metrics.ForPolicy(Duration, policy.PolicyId));
        Assert.Equal((1, 1L), budget.Snapshot);
        held.Dispose();
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData(Outcomes)]
    [InlineData(Duration)]
    [InlineData(InputSize)]
    [InlineData(OutputSize)]
    [InlineData(Masks)]
    [InlineData("*")]
    public void Throwing_listeners_keep_concurrent_and_repeated_completion_idempotent(string instrument)
    {
        var policy = Policy("TelemetryThrowingConcurrent");
        using var metrics = new MetricCollector(instrument, policy.PolicyId);
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var measurement = telemetry.TryStartInspection(policy);
        Assert.NotNull(measurement);

        measurement.CapturedInputBodyBytes = 0;
        measurement.PreparedOutputBodyBytes = 0;
        measurement.ValidatedMasks = 3;
        Parallel.For(0, 100, _ => measurement.Complete(PrivacyInspectionOutcome.Inspected));
        measurement.Complete(PrivacyInspectionOutcome.Canceled);
        measurement.Dispose();

        AssertOutcomeAndDuration(metrics, policy, "inspected");
        AssertInputSize(metrics, policy, "inspected", 0);
        AssertOutputSize(metrics, policy, "inspected", 0);
        AssertMasks(metrics, policy, "inspected", 3);
        Assert.Equal((0, 0L), budget.Snapshot);
        using var next = telemetry.TryStartInspection(Policy("TelemetryAfterThrowingConcurrent"));
        Assert.NotNull(next);
        Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);
        measurement.Dispose();
        Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);
        next.Dispose();
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData(Outcomes, false)]
    [InlineData(Duration, false)]
    [InlineData(Outcomes, true)]
    [InlineData(Duration, true)]
    [InlineData(InputSize, false)]
    [InlineData(InputSize, true)]
    [InlineData(OutputSize, false)]
    [InlineData(OutputSize, true)]
    [InlineData(Masks, false)]
    [InlineData(Masks, true)]
    public async Task Throwing_listener_preserves_original_error_or_cancellation(string instrument, bool canceled)
    {
        var policy = Policy("TelemetryThrowingOriginalFailure");
        using var metrics = new MetricCollector(instrument, policy.PolicyId);
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var cancellation = new CancellationTokenSource();
        using var downstream = new ControlledWriteStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream, cancellation.Token);

        var pending = invocation.ApplyAsync();
        await downstream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        if (canceled)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            var failure = new IOException("Synthetic original write failure.");
            downstream.Fail(failure);
            var actual = await Assert.ThrowsAsync<IOException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, actual);
        }

        AssertOutcomeAndDuration(metrics, policy, canceled ? "canceled" : "write_error");
        AssertInputSize(metrics, policy, canceled ? "canceled" : "write_error", Encoding.UTF8.GetByteCount(JsonBody));
        AssertOutputSize(metrics, policy, canceled ? "canceled" : "write_error", 45);
        AssertMasks(metrics, policy, canceled ? "canceled" : "write_error", 1);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData(Outcomes)]
    [InlineData(Duration)]
    [InlineData(InputSize)]
    [InlineData(OutputSize)]
    [InlineData(Masks)]
    public void Listener_cancellation_does_not_cancel_successful_inspection(string instrument)
    {
        var policy = Policy("TelemetryListenerCancellation");
        using var metrics = new MetricCollector(instrument, policy.PolicyId, new OperationCanceledException());
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var measurement = telemetry.TryStartInspection(policy);
        Assert.NotNull(measurement);

        measurement.CapturedInputBodyBytes = 17;
        measurement.PreparedOutputBodyBytes = 23;
        measurement.ValidatedMasks = 2;
        measurement.Complete(PrivacyInspectionOutcome.Inspected);

        AssertOutcomeAndDuration(metrics, policy, "inspected");
        AssertInputSize(metrics, policy, "inspected", 17);
        AssertOutputSize(metrics, policy, "inspected", 23);
        AssertMasks(metrics, policy, "inspected", 2);
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData(Outcomes)]
    [InlineData(Duration)]
    [InlineData(InputSize)]
    [InlineData(OutputSize)]
    [InlineData(Masks)]
    public void Listener_out_of_memory_is_not_hidden_and_lease_is_released(string instrument)
    {
        var policy = Policy("TelemetryListenerOutOfMemory");
        var failure = new OutOfMemoryException("Synthetic listener failure; no memory exhaustion.");
        using var metrics = new MetricCollector(instrument, policy.PolicyId, failure);
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var measurement = telemetry.TryStartInspection(policy);
        Assert.NotNull(measurement);

        measurement.CapturedInputBodyBytes = 17;
        measurement.PreparedOutputBodyBytes = 23;
        measurement.ValidatedMasks = 2;
        var actual = Assert.Throws<OutOfMemoryException>(() => measurement.Complete(PrivacyInspectionOutcome.Inspected));

        Assert.Same(failure, actual);
        Assert.Equal((0, 0L), budget.Snapshot);
        measurement.Dispose();
        Assert.Single(metrics.ForPolicy(Outcomes, policy.PolicyId));
    }

    [Theory]
    [InlineData(PrivacyInspectionOutcome.CapacityExceeded)]
    [InlineData((PrivacyInspectionOutcome)999)]
    public void Invalid_completion_outcome_is_not_hidden_by_listener_isolation(PrivacyInspectionOutcome outcome)
    {
        var policy = Policy("TelemetryInvalidOutcome");
        using var metrics = new MetricCollector("*", policy.PolicyId);
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var measurement = telemetry.TryStartInspection(policy);
        Assert.NotNull(measurement);

        Assert.Throws<ArgumentOutOfRangeException>(() => measurement.Complete(outcome));
        Assert.Empty(metrics.ForPolicy(Outcomes, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(Duration, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(InputSize, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
        measurement.Dispose();
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData("match", "policy_blocked")]
    [InlineData("null", "inspected")]
    [InlineData("missing", "inspected")]
    [InlineData("invalid-json", "blocked")]
    [InlineData("invalid-terminal", "blocked")]
    public async Task Block_records_distinct_terminal_outcomes_with_unchanged_metric_contract(string scenario, string outcome)
    {
        var body = scenario switch
        {
            "match" => JsonBody,
            "null" => "{ \"payment\" : {\"cardNumber\":null} }\n",
            "missing" => "{ \"other\" : \"4111111111111111\" }\n",
            "invalid-json" => JsonBody + "invalid",
            "invalid-terminal" => "{\"payment\":{\"cardNumber\":\"selected\",\"cardNumber\":42}}",
            _ => throw new ArgumentException(nameof(scenario))
        };
        var policy = Policy("TelemetryBlockContract") with { Mode = "Block" };
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var downstream = new MemoryStream();
        using var invocation = Invocation(telemetry, policy, JsonContent(body), downstream);
        await invocation.ApplyAsync();
        Assert.Equal(outcome == "inspected" ? 200 : 502, invocation.Context.HttpContext.Response.StatusCode);
        Assert.Equal(outcome == "inspected" ? body : outcome == "blocked"
            ? "{\"code\":\"privacy_inspection_failed\"}" : "{\"code\":\"privacy_response_blocked\"}",
            Encoding.UTF8.GetString(downstream.ToArray()));
        AssertOutcomeAndDuration(metrics, policy, outcome);
        AssertInputSize(metrics, policy, outcome, Encoding.UTF8.GetByteCount(body));
        Assert.Equal((0, 0L), budget.Snapshot);
        Assert.Equal(6, (int)PrivacyInspectionOutcome.TimedOut);
        Assert.Equal(7, (int)PrivacyInspectionOutcome.PolicyBlocked);
        AssertMasks(metrics, policy, outcome, 0);
    }

    [Theory]
    [InlineData("policy_blocked")]
    [InlineData("canceled")]
    [InlineData("timed_out")]
    [InlineData("write_error")]
    public async Task Block_write_holds_reservation_until_real_completion_and_terminal_error_wins(string outcome)
    {
        var policy = Policy("TelemetryBlockWrite") with { Mode = "Block" };
        using var metrics = new MetricCollector();
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var cancellation = new CancellationTokenSource();
        using var downstream = new ControlledWriteStream(cooperative: false);
        var clock = new ManualBlockClock();
        using var invocation = Invocation(telemetry, policy, JsonContent(JsonBody), downstream,
            cancellation.Token, clock: clock);
        var admission = new ProxyAdmission(Options.Create(new ProxyAdmissionOptions { MaxConcurrentProxyRequests = 1 }));
        invocation.Context.HttpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new Yarp.ReverseProxy.Model.RouteModel(
                new Yarp.ReverseProxy.Configuration.RouteConfig { RouteId = policy.RouteId }, null!,
                Yarp.ReverseProxy.Forwarder.HttpTransformer.Default)), "proxy"));
        var pending = new ProxyAdmissionMiddleware(_ => invocation.ApplyAsync())
            .InvokeAsync(invocation.Context.HttpContext, admission);
        await downstream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("{\"code\":\"privacy_response_blocked\"}", downstream.AttemptedBody);
        if (outcome == "canceled") cancellation.Cancel();
        if (outcome == "timed_out") clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);
        Assert.Equal(1, admission.ActiveRequests);
        Assert.Null(admission.TryAcquire());
        Assert.Null(budget.TryAcquire(ReservedBodyBytes));
        Assert.Empty(metrics.ForPolicy(Outcomes, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(Duration, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(InputSize, policy.PolicyId));
        Assert.Empty(metrics.ForPolicy(OutputSize, policy.PolicyId));
        var failure = new IOException("Synthetic block downstream failure.");
        if (outcome == "write_error") downstream.Fail(failure);
        else downstream.Complete();
        if (outcome is "canceled" or "timed_out")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        else if (outcome == "write_error")
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))));
        else await pending.WaitAsync(TimeSpan.FromSeconds(5));
        AssertOutcomeAndDuration(metrics, policy, outcome);
        AssertInputSize(metrics, policy, outcome, Encoding.UTF8.GetByteCount(JsonBody));
        AssertOutputSize(metrics, policy, outcome, 35);
        AssertMasks(metrics, policy, outcome, 0);
        Assert.Equal((0, 0L), budget.Snapshot);
        Assert.Equal(0, admission.ActiveRequests);
        using var proxyRecovered = admission.TryAcquire();
        Assert.NotNull(proxyRecovered);
        using var recovered = budget.TryAcquire(ReservedBodyBytes);
        Assert.NotNull(recovered);
    }

    // Same manual monotonic/timer pattern as InspectionDeadlineTests; no wall-clock timeout races.
    private sealed class ManualBlockClock : TimeProvider
    {
        private long ticks;
        private readonly List<ManualTimer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public Action? TimestampObserved { get; set; }
        public override long GetTimestamp() { TimestampObserved?.Invoke(); return Interlocked.Read(ref ticks); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref ticks, elapsed.Ticks);
            foreach (var timer in timers.ToArray()) timer.Fire();
        }
        private sealed class ManualTimer(ManualBlockClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks; return true; }
            public void Fire() { if (clock.ticks < due) return; due = long.MaxValue; callback(state); }
            public void Dispose() => due = long.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static void AssertInputSize(MetricCollector metrics, CompiledPanPolicy policy, string outcome, long bytes)
        => AssertBodySize(metrics, policy, outcome, bytes, InputSize);

    private static void AssertMasks(MetricCollector metrics, CompiledPanPolicy policy, string outcome, long count)
    {
        var samples = metrics.ForPolicy(Masks, policy.PolicyId);
        if (count == 0) { Assert.Empty(samples); return; }
        var sample = Assert.Single(samples);
        Assert.Equal(count, sample.Value);
        Assert.Equal("{mask}", sample.Unit);
        Assert.IsType<Counter<long>>(sample.Instrument);
        var terminal = Assert.Single(metrics.ForPolicy(Outcomes, policy.PolicyId));
        Assert.Same(terminal.Meter, sample.Meter);
        Assert.Equal("Gateway.Privacy", sample.Meter.Name);
        Assert.Equal("1.0.0", sample.Meter.Version);
        Assert.Equal(new Dictionary<string, string?>
        {
            ["gateway.route.id"] = policy.RouteId,
            ["gateway.policy.id"] = policy.PolicyId,
            ["gateway.policy.version"] = policy.Version,
            ["gateway.privacy.outcome"] = outcome
        }.OrderBy(tag => tag.Key), sample.Tags.OrderBy(tag => tag.Key));
    }

    private static void AssertOutputSize(MetricCollector metrics, CompiledPanPolicy policy, string outcome, long bytes)
        => AssertBodySize(metrics, policy, outcome, bytes, OutputSize);

    private static void AssertBodySize(MetricCollector metrics, CompiledPanPolicy policy, string outcome, long bytes, string instrument)
    {
        var size = Assert.Single(metrics.ForPolicy(instrument, policy.PolicyId));
        Assert.Equal(bytes, size.Value);
        Assert.Equal("By", size.Unit);
        Assert.IsType<Histogram<long>>(size.Instrument);
        var terminal = Assert.Single(metrics.ForPolicy(Outcomes, policy.PolicyId));
        Assert.Same(terminal.Meter, size.Meter);
        Assert.Equal("Gateway.Privacy", size.Meter.Name);
        Assert.Equal("1.0.0", size.Meter.Version);
        Assert.Equal(new Dictionary<string, string?>
        {
            ["gateway.route.id"] = policy.RouteId,
            ["gateway.policy.id"] = policy.PolicyId,
            ["gateway.policy.version"] = policy.Version,
            ["gateway.privacy.outcome"] = outcome
        }.OrderBy(tag => tag.Key), size.Tags.OrderBy(tag => tag.Key));
    }

    private static void AssertOutcomeAndDuration(MetricCollector metrics, CompiledPanPolicy policy,
        string expectedOutcome)
    {
        var outcome = AssertOutcome(metrics, policy, expectedOutcome);
        var duration = Assert.Single(metrics.ForPolicy(Duration, policy.PolicyId));
        Assert.Same(outcome.Meter, duration.Meter);
        Assert.Equal("ms", duration.Unit);
        Assert.Equal(expectedOutcome, duration.Tags["gateway.privacy.outcome"]);
        Assert.Equal(outcome.Tags.OrderBy(tag => tag.Key), duration.Tags.OrderBy(tag => tag.Key));
        Assert.True(duration.Value >= 0);
    }

    private static CapturedMeasurement AssertOutcome(MetricCollector metrics, CompiledPanPolicy policy,
        string expectedOutcome)
    {
        var outcome = Assert.Single(metrics.ForPolicy(Outcomes, policy.PolicyId));
        Assert.Equal(1, outcome.Value);
        Assert.Equal("{inspection}", outcome.Unit);
        Assert.Equal(PrivacyTelemetry.MeterName, outcome.Meter.Name);
        Assert.Equal(PrivacyTelemetry.MeterVersion, outcome.Meter.Version);
        Assert.Equal(4, outcome.Tags.Count);
        Assert.Equal(policy.RouteId, outcome.Tags["gateway.route.id"]);
        Assert.Equal(policy.PolicyId, outcome.Tags["gateway.policy.id"]);
        Assert.Equal(policy.Version, outcome.Tags["gateway.policy.version"]);
        Assert.Equal(expectedOutcome, outcome.Tags["gateway.privacy.outcome"]);
        return outcome;
    }

    private static InspectionBudget Budget(int maxConcurrent, long maxBytes) =>
        new(Options.Create(new PanPolicyOptions
        {
            InspectionBudget = new()
            {
                MaxConcurrentInspections = maxConcurrent,
                MaxReservedBodyBytes = maxBytes
            }
        }));

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static CompiledPanPolicy Policy(string policyId, int maxBodyBytes = ReservedBodyBytes) =>
        new(policyId, "v1", "protected-payment", ["payment", "cardNumber"], maxBodyBytes);

    private static StringContent JsonContent(string body) => Content(body, "application/json");

    private static StringContent Content(string body, string mediaType) =>
        new(body, Encoding.UTF8, mediaType);

    private static TransformInvocation Invocation(PrivacyTelemetry telemetry, CompiledPanPolicy policy,
        HttpContent content, Stream downstream, CancellationToken cancellationToken = default,
        string method = "GET", HttpStatusCode statusCode = HttpStatusCode.OK,
        Func<CancellationToken, ValueTask>? diagnosticCheckpoint = null, TimeProvider? clock = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Response.Body = downstream;
        httpContext.Response.StatusCode = (int)statusCode;
        var proxyResponse = new HttpResponseMessage(statusCode) { Content = content };
        var context = new ResponseTransformContext
        {
            HttpContext = httpContext,
            ProxyResponse = proxyResponse,
            HeadersCopied = true,
            CancellationToken = cancellationToken
        };
        var transform = new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, telemetry, diagnosticCheckpoint, clock);
        return new TransformInvocation(transform, policy, context, proxyResponse);
    }

    private sealed class TransformInvocation(PanResponseTransform transform, CompiledPanPolicy policy,
        ResponseTransformContext context, HttpResponseMessage proxyResponse) : IDisposable
    {
        public ResponseTransformContext Context { get; } = context;
        public Task ApplyAsync() => transform.ApplyAsync(Context, policy).AsTask();
        public void Dispose() => proxyResponse.Dispose();
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly ConcurrentQueue<CapturedMeasurement> measurements = new();
        private readonly MeterListener listener = new();

        private readonly string? failingInstrument;
        private readonly string? failingPolicyId;
        private readonly Exception listenerFailure;

        public MetricCollector(string? failingInstrument = null, string? failingPolicyId = null,
            Exception? failure = null)
        {
            this.failingInstrument = failingInstrument;
            this.failingPolicyId = failingPolicyId;
            listenerFailure = failure ?? new InvalidOperationException("Synthetic metric consumer failure.");
            listener.InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == PrivacyTelemetry.MeterName
                    && instrument.Meter.Version == PrivacyTelemetry.MeterVersion)
                    currentListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
                Capture(instrument, value, tags));
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Capture(instrument, value, tags));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                Capture(instrument, value, tags));
            listener.Start();
        }

        public CapturedMeasurement[] ForPolicy(string instrumentName, string policyId) => measurements
            .Where(item => item.InstrumentName == instrumentName
                && item.Tags.GetValueOrDefault("gateway.policy.id") == policyId)
            .ToArray();

        public CapturedMeasurement[] ForInstrument(string instrumentName) => measurements
            .Where(item => item.InstrumentName == instrumentName)
            .ToArray();

        public CapturedMeasurement[] ForPolicyAll(string policyId) => measurements
            .Where(item => item.Tags.GetValueOrDefault("gateway.policy.id") == policyId).ToArray();

        public void RecordObservableInstruments() => listener.RecordObservableInstruments();

        public void Clear()
        {
            while (measurements.TryDequeue(out _)) { }
        }

        public void Dispose() => listener.Dispose();

        private void Capture<T>(Instrument instrument, T value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags) where T : struct
        {
            var capturedTags = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags) capturedTags.Add(tag.Key, tag.Value?.ToString());
            measurements.Enqueue(new CapturedMeasurement(instrument.Name, instrument.Unit,
                Convert.ToDouble(value), capturedTags, instrument.Meter, instrument));
            // Match a test-specific policy, not every Gateway meter in this process.
            // Capture before throwing to prove all independent publications were attempted.
            if ((failingInstrument == instrument.Name || failingInstrument == "*")
                && capturedTags.GetValueOrDefault("gateway.policy.id") == failingPolicyId)
                throw listenerFailure;
        }
    }

    private sealed record CapturedMeasurement(string InstrumentName, string? Unit, double Value,
        IReadOnlyDictionary<string, string?> Tags, Meter Meter, Instrument Instrument);

    private sealed class ForbiddenReadContent : HttpContent
    {
        public bool ReadAttempted { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Excluded content must not be read.");
        }
        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Excluded content must not be opened.");
        }
    }

    private sealed class ControlledWriteStream(bool cooperative = true) : Stream
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteStarted => writeStarted.Task;
        public string? AttemptedBody { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public void Complete() => release.TrySetResult();
        public void Fail(Exception exception) => release.TrySetException(exception);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            AttemptedBody = Encoding.UTF8.GetString(buffer.Span);
            writeStarted.TrySetResult();
            if (cooperative) await release.Task.WaitAsync(cancellationToken);
            else await release.Task;
            cancellationToken.ThrowIfCancellationRequested();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FragmentedInputStream(byte[] bytes, int? failAfterReads = null, Action? onEof = null) : Stream
    {
        private int position;
        private int reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reads++ == failAfterReads) throw new IOException("Synthetic incomplete read.");
            var count = Math.Min(Math.Min(7, buffer.Length), bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            if (count == 0) onEof?.Invoke();
            return ValueTask.FromResult(count);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
