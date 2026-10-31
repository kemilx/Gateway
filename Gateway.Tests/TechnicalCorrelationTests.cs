using System.Net;
using System.Text;
using Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

[Collection("Secondary PAN telemetry isolation")]
public sealed class TechnicalCorrelationTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private const string Body = "{\"selected\":\"4111111111111111\"}";
    private static readonly CompiledPanPolicy Policy = new("Policy", "v1", "protected-payment", ["selected"], 4096);

    [Theory]
    [InlineData("head")] [InlineData("204")] [InlineData("304")] [InlineData("nonJSON")]
    [InlineData("capacity")] [InlineData("absent")] [InlineData("precancel")]
    [InlineData("invalidJSON")] [InlineData("logger-failed")]
    [InlineData("Mask")] [InlineData("Block")] [InlineData("Observe")]
    public async Task Correlation_preserves_exclusions_and_failure_semantics(string scenario)
    {
        var policy = Policy with { Mode = scenario is "Block" or "Observe" ? scenario : "Mask" };
        var budget = new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = 4096 } }));
        using var telemetry = new PrivacyTelemetry(budget);
        var logs = new TechnicalCorrelationIntegrationTests.Logs { ThrowEvents = scenario == "logger-failed" };
        var transform = new PanResponseTransform(logs, telemetry);
        using var output = new MemoryStream(); using var caller = new CancellationTokenSource();
        var http = new DefaultHttpContext(); http.Request.Method = scenario == "head" ? "HEAD" : "GET"; http.Response.Body = output;
        http.Features.Set(new TechnicalCorrelationCapture(Id));
        using var upstream = scenario == "absent" ? null : new HttpResponseMessage(scenario == "204" ? HttpStatusCode.NoContent : scenario == "304" ? HttpStatusCode.NotModified : HttpStatusCode.OK)
        { Content = new StringContent(scenario == "invalidJSON" ? "invalid" : Body, Encoding.UTF8, scenario == "nonJSON" ? "text/plain" : "application/json") };
        var context = new ResponseTransformContext { HttpContext = http, ProxyResponse = upstream, CancellationToken = caller.Token };
        using var held = scenario == "capacity" ? budget.TryAcquire(4096) : null;
        if (scenario == "precancel")
        {
            caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transform.ApplyAsync(context, policy, "synthetic-api").AsTask());
        }
        else await transform.ApplyAsync(context, policy, "synthetic-api");
        var exclusion = scenario is "head" or "204" or "nonJSON";
        var noEvents = scenario is "absent" or "precancel" or "logger-failed";
        Assert.Equal(noEvents ? 0 : exclusion || scenario is "capacity" or "invalidJSON" or "304" ? 1 : 2, logs.Events.Count);
        Assert.All(logs.Events, e => { Assert.Equal(Id, e.State["CorrelationId"]); Assert.Equal(1, e.State["SchemaVersion"]); Assert.Null(e.Exception); });
        if (exclusion)
        {
            var entry = Assert.Single(logs.Events); Assert.Equal(4, entry.Id.Id);
            Assert.Equal(scenario == "head" ? "excluded_head" : scenario == "nonJSON" ? "excluded_media_type" : "excluded_bodyless", entry.State["Reason"]);
            Assert.Equal("not_evaluated", entry.State["RuleEvaluation"]);
        }
        else if (!noEvents)
        {
            var terminal = Assert.Single(logs.Events, e => e.Id.Id == 5);
            Assert.Equal(scenario == "capacity" ? "capacity_exceeded" : scenario is "invalidJSON" or "304" ? "blocked" : scenario == "Block" ? "policy_blocked" : "inspected", terminal.State["Outcome"]);
        }
        Assert.Equal(scenario == "capacity" ? 503 : scenario is "Block" or "invalidJSON" or "304" ? 502 : 200, http.Response.StatusCode);
        Assert.Equal(scenario == "capacity" ? "{\"code\":\"privacy_capacity_exceeded\"}" : scenario is "invalidJSON" or "304" ? "{\"code\":\"privacy_inspection_failed\"}" : scenario == "Block" ? "{\"code\":\"privacy_response_blocked\"}" : scenario == "Observe" ? Body : exclusion || scenario is "absent" or "precancel" ? "" : "{\"selected\":\"************1111\"}", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(scenario == "capacity" ? (1, 4096L) : (0, 0L), budget.Snapshot);
    }

    [Fact]
    public async Task Terminal_callback_uses_feature_string_copied_before_inspection()
    {
        using var telemetry = new PrivacyTelemetry(new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() })));
        var logs = new TechnicalCorrelationIntegrationTests.Logs();
        using var output = new MemoryStream(); var http = new DefaultHttpContext(); http.Response.Body = output;
        http.Features.Set(new TechnicalCorrelationCapture(Id));
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        var context = new ResponseTransformContext { HttpContext = http, ProxyResponse = upstream };
        var transform = new PanResponseTransform(logs, telemetry, _ =>
        {
            http.Features.Set(new TechnicalCorrelationCapture("fedcba9876543210fedcba9876543210"));
            return ValueTask.CompletedTask;
        });
        await transform.ApplyAsync(context, Policy, "synthetic-api");
        Assert.Equal(2, logs.Events.Count); Assert.All(logs.Events, e => Assert.Equal(Id, e.State["CorrelationId"]));
    }

    [Theory]
    [InlineData(PrivacyInspectionOutcome.Inspected)] [InlineData(PrivacyInspectionOutcome.Blocked)]
    [InlineData(PrivacyInspectionOutcome.CapacityExceeded)] [InlineData(PrivacyInspectionOutcome.Canceled)]
    [InlineData(PrivacyInspectionOutcome.WriteError)] [InlineData(PrivacyInspectionOutcome.Faulted)]
    [InlineData(PrivacyInspectionOutcome.TimedOut)] [InlineData(PrivacyInspectionOutcome.PolicyBlocked)]
    public void Explicit_terminal_correlation_and_legacy_overload_share_unchanged_schema(PrivacyInspectionOutcome outcome)
    {
        using var telemetry = new PrivacyTelemetry(new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() })));
        var logs = new TechnicalCorrelationIntegrationTests.Logs(); var transform = new PanResponseTransform(logs, telemetry);
        var summary = new PrivacyTelemetry.InspectionSummary(outcome, 1, 2, 3, [new("primary", "PAN", 2, 1)]);
        transform.RecordInspectionCompleted(Policy, "synthetic-api", summary, Id);
        transform.RecordInspectionCompleted(Policy, "synthetic-api", summary);
        var entries = logs.Events.ToArray(); Assert.Equal(4, entries.Length);
        Assert.Equal(Id, entries[0].State["CorrelationId"]); Assert.Equal(Id, entries[1].State["CorrelationId"]);
        Assert.Matches("\\A[0-9a-f]{32}\\z", (string)entries[2].State["CorrelationId"]!);
        Assert.NotEqual(Id, entries[2].State["CorrelationId"]); Assert.Equal(entries[2].State["CorrelationId"], entries[3].State["CorrelationId"]);
        foreach (var pair in new[] { (0, 2), (1, 3) })
        {
            Assert.Equal(entries[pair.Item1].Id, entries[pair.Item2].Id);
            Assert.Equal(entries[pair.Item1].State.Where(p => p.Key != "CorrelationId"), entries[pair.Item2].State.Where(p => p.Key != "CorrelationId"));
        }
    }
}
