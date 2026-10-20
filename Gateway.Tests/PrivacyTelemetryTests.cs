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

public sealed class PrivacyTelemetryTests
{
    private const int ReservedBodyBytes = 1024;
    private const string JsonBody = "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}";
    private const string Outcomes = "gateway.privacy.inspection.outcomes";
    private const string Duration = "gateway.privacy.inspection.duration";
    private const string Active = "gateway.privacy.inspection.active";
    private const string Reserved = "gateway.privacy.inspection.reserved_body_bytes";

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
        Assert.Equal((1, 1L), budget.Snapshot);
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

        downstream.Complete();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        AssertOutcomeAndDuration(metrics, policy, "inspected");
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

        Parallel.For(0, 100, index => measurement.Complete(index % 2 == 0
            ? PrivacyInspectionOutcome.Inspected
            : PrivacyInspectionOutcome.Canceled));

        var outcome = Assert.Single(metrics.ForPolicy(Outcomes, policy.PolicyId));
        Assert.Contains(outcome.Tags["gateway.privacy.outcome"], new[] { "inspected", "canceled" });
        Assert.Single(metrics.ForPolicy(Duration, policy.PolicyId));
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
    public async Task Head_bodyless_and_non_json_responses_emit_no_outcomes(string mode)
    {
        using var metrics = new MetricCollector();
        var budget = Budget(maxConcurrent: 1, maxBytes: ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        var scenarios = new[]
        {
            (Policy("TelemetryHead"), HttpMethods.Head, HttpStatusCode.OK, "application/json"),
            (Policy("TelemetryNoContent"), HttpMethods.Get, HttpStatusCode.NoContent, "application/json"),
            (Policy("TelemetryNotModified"), HttpMethods.Get, HttpStatusCode.NotModified, "application/json"),
            (Policy("TelemetryNonJson"), HttpMethods.Get, HttpStatusCode.OK, "text/plain")
        };

        foreach (var (policy, method, status, mediaType) in scenarios)
        {
            using var downstream = new MemoryStream();
            using var invocation = Invocation(telemetry, policy with { Mode = mode }, Content(JsonBody, mediaType), downstream,
                method: method, statusCode: status);
            await invocation.ApplyAsync();
            Assert.Empty(metrics.ForPolicy(Outcomes, policy.PolicyId));
            Assert.Empty(metrics.ForPolicy(Duration, policy.PolicyId));
        }

        Assert.Equal((0, 0L), budget.Snapshot);
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
        }
        finally
        {
            await gateway.StopAsync();
            await api.StopAsync();
        }
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
    [InlineData("*")]
    public void Throwing_listeners_keep_concurrent_and_repeated_completion_idempotent(string instrument)
    {
        var policy = Policy("TelemetryThrowingConcurrent");
        using var metrics = new MetricCollector(instrument, policy.PolicyId);
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var measurement = telemetry.TryStartInspection(policy);
        Assert.NotNull(measurement);

        Parallel.For(0, 100, _ => measurement.Complete(PrivacyInspectionOutcome.Inspected));
        measurement.Complete(PrivacyInspectionOutcome.Canceled);
        measurement.Dispose();

        AssertOutcomeAndDuration(metrics, policy, "inspected");
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
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData(Outcomes)]
    [InlineData(Duration)]
    public void Listener_cancellation_does_not_cancel_successful_inspection(string instrument)
    {
        var policy = Policy("TelemetryListenerCancellation");
        using var metrics = new MetricCollector(instrument, policy.PolicyId, new OperationCanceledException());
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var measurement = telemetry.TryStartInspection(policy);
        Assert.NotNull(measurement);

        measurement.Complete(PrivacyInspectionOutcome.Inspected);

        AssertOutcomeAndDuration(metrics, policy, "inspected");
        Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Theory]
    [InlineData(Outcomes)]
    [InlineData(Duration)]
    public void Listener_out_of_memory_is_not_hidden_and_lease_is_released(string instrument)
    {
        var policy = Policy("TelemetryListenerOutOfMemory");
        var failure = new OutOfMemoryException("Synthetic listener failure; no memory exhaustion.");
        using var metrics = new MetricCollector(instrument, policy.PolicyId, failure);
        var budget = Budget(1, ReservedBodyBytes);
        using var telemetry = new PrivacyTelemetry(budget);
        using var measurement = telemetry.TryStartInspection(policy);
        Assert.NotNull(measurement);

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
        Assert.Equal((0, 0L), budget.Snapshot);
        Assert.Equal(6, (int)PrivacyInspectionOutcome.TimedOut);
        Assert.Equal(7, (int)PrivacyInspectionOutcome.PolicyBlocked);
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
        var failure = new IOException("Synthetic block downstream failure.");
        if (outcome == "write_error") downstream.Fail(failure);
        else downstream.Complete();
        if (outcome is "canceled" or "timed_out")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        else if (outcome == "write_error")
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))));
        else await pending.WaitAsync(TimeSpan.FromSeconds(5));
        AssertOutcomeAndDuration(metrics, policy, outcome);
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
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
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
                Convert.ToDouble(value), capturedTags, instrument.Meter));
            // Match a test-specific policy, not every Gateway meter in this process.
            // Capture before throwing to prove both independent publications were attempted.
            if ((failingInstrument == instrument.Name || failingInstrument == "*")
                && capturedTags.GetValueOrDefault("gateway.policy.id") == failingPolicyId)
                throw listenerFailure;
        }
    }

    private sealed record CapturedMeasurement(string InstrumentName, string? Unit, double Value,
        IReadOnlyDictionary<string, string?> Tags, Meter Meter);

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
}
