using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Gateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using static Gateway.Tests.PrivacyInspectionEventTests;

namespace Gateway.Tests;

[Collection("Secondary PAN telemetry isolation")]
public sealed class PrivacyRuleEventTests
{
    private const string Template = "Privacy rule matched; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} rule {RuleId} transformation {Transformation} validated matches {ValidatedMatches} validated masks {ValidatedMasks}.";
    private static CompiledPanPolicy Policy(string mode = "Mask") => new("RulePolicy", "v1", "route", ["selector_marker"], 4096) { Mode = mode };

    [Theory]
    [InlineData(0, false)] [InlineData(15, false)] [InlineData(15, true)]
    public async Task Exact_schema_and_positional_order_at_maximum(int additional, bool secondary)
    {
        var policy = Policy() with { AdditionalRules = Enumerable.Range(0, additional).Select(i => new CompiledSensitiveRule([$"field_marker{i}"], i % 2 == 0 ? "Email" : "Phone")).ToImmutableArray(), SecondaryPanDetection = secondary };
        // Reverse JSON order: publication order must be policy position, not discovery.
        var body = "{" + string.Join(",", Enumerable.Range(0, additional).Reverse().Select(i => $"\"field_marker{i}\":\"body_marker\"")) + (additional > 0 ? "," : "") + "\"selector_marker\":\"body_marker\",\"other\":\"4111111111111111\"}";
        using var f = new Fixture(body, policy);
        using var activity = new Activity("activity_marker").Start();
        activity.AddBaggage("secret", "baggage_marker");
        f.Context.HttpContext.TraceIdentifier = "trace_marker";
        f.Context.HttpContext.Request.Headers["X-Correlation-Id"] = "incoming_marker";
        f.Context.HttpContext.Request.QueryString = new("?secret=query_marker");
        await f.Run();
        var terminal = Assert.Single(f.Logs.Events);
        var rules = f.Logs.Rules.ToArray();
        var ids = new[] { "primary" }.Concat(Enumerable.Range(0, additional).Select(i => $"additional-{i}")).Concat(secondary ? ["secondary-pan"] : []).ToArray();
        Assert.Equal(ids, rules.Select(e => e.State["RuleId"]));
        Assert.Equal(1 + additional + (secondary ? 1 : 0), rules.Length);
        Assert.True(rules.Length <= 17);
        for (var i = 0; i < rules.Length; i++)
        {
            var item = rules[i];
            Assert.Equal(LogLevel.Information, item.Level); Assert.Equal(new EventId(6, "PrivacyRuleMatched"), item.Id); Assert.Null(item.Exception);
            Assert.Equal(new[] { "SchemaVersion", "RouteId", "ServiceId", "PolicyId", "PolicyVersion", "CorrelationId", "Outcome", "RuleId", "Transformation", "ValidatedMatches", "ValidatedMasks", "{OriginalFormat}" }.Order(), item.State.Keys.Order());
            Assert.Equal(1, Assert.IsType<int>(item.State["SchemaVersion"]));
            Assert.Equal("route", Assert.IsType<string>(item.State["RouteId"])); Assert.Equal("unknown", Assert.IsType<string>(item.State["ServiceId"]));
            Assert.Equal("RulePolicy", Assert.IsType<string>(item.State["PolicyId"])); Assert.Equal("v1", Assert.IsType<string>(item.State["PolicyVersion"]));
            Assert.Equal("inspected", Assert.IsType<string>(item.State["Outcome"])); Assert.Equal(ids[i], Assert.IsType<string>(item.State["RuleId"]));
            var transformation = i == 0 || ids[i] == "secondary-pan" ? "PAN" : (i - 1) % 2 == 0 ? "Email" : "Phone";
            Assert.Equal(transformation, Assert.IsType<string>(item.State["Transformation"]));
            Assert.Equal(1L, Assert.IsType<long>(item.State["ValidatedMatches"])); Assert.Equal(1L, Assert.IsType<long>(item.State["ValidatedMasks"]));
            var correlation = Assert.IsType<string>(item.State["CorrelationId"]); Assert.Matches("\\A[0-9a-f]{32}\\z", correlation); Assert.NotEqual(Guid.Empty, Guid.ParseExact(correlation, "N"));
            Assert.Equal(terminal.State["CorrelationId"], correlation); Assert.Equal(Template, item.State["{OriginalFormat}"]);
            Assert.Equal($"Privacy rule matched; schema 1 route route service unknown policy RulePolicy version v1 correlation {correlation} outcome inspected rule {ids[i]} transformation {transformation} validated matches 1 validated masks 1.", item.Rendered);
            var all = item.Rendered + string.Join("|", item.State.Select(p => p.Key + "=" + p.Value));
            foreach (var marker in new[] { "selector_marker", "field_marker", "body_marker", "4111111111111111", "incoming_marker", "trace_marker", "query_marker", "activity_marker", "baggage_marker", activity.Id!, activity.TraceId.ToString() }) Assert.DoesNotContain(marker, all);
        }
        Assert.Equal(terminal.State["ValidatedExplicitSensitiveMatches"], rules.Where(e => !Equals(e.State["RuleId"], "secondary-pan")).Sum(e => (long)e.State["ValidatedMatches"]!));
        Assert.Equal(terminal.State["ValidatedSecondaryPanMatches"], rules.Where(e => Equals(e.State["RuleId"], "secondary-pan")).Sum(e => (long)e.State["ValidatedMatches"]!));
        Assert.Equal(terminal.State["ValidatedMasks"], rules.Sum(e => (long)e.State["ValidatedMasks"]!));
        Assert.False(f.Context.HttpContext.Response.Headers.ContainsKey("X-Correlation-Id"));
    }

    [Theory]
    [InlineData("Mask", false)] [InlineData("Block", false)] [InlineData("Observe", false)]
    [InlineData("Mask", true)] [InlineData("Block", true)] [InlineData("Observe", true)]
    public async Task Public_invalid_metadata_omits_all_rules_only(string mode, bool excessive)
    {
        var policy = Policy(mode) with { Transformation = excessive ? "PAN" : "transformation_marker", AdditionalRules = excessive ? Enumerable.Range(0, 16).Select(i => new CompiledSensitiveRule([$"field{i}"], "PAN")).ToImmutableArray() : [] };
        using var f = new Fixture("{\"selector_marker\":\"4111111111111111\"}", policy);
        await f.Run(); Assert.Empty(f.Logs.Rules);
        var e = Assert.Single(f.Logs.Events);
        Assert.Equal(mode == "Mask" && !excessive ? "blocked" : mode == "Block" ? "policy_blocked" : "inspected", e.State["Outcome"]);
        Assert.Equal(mode == "Mask" && !excessive ? 0L : 1L, e.State["ValidatedExplicitSensitiveMatches"]);
        Assert.Equal(mode == "Mask" && excessive ? 1L : 0L, e.State["ValidatedMasks"]);
    }

    [Fact]
    public async Task Invalid_additional_transformation_omits_even_valid_primary_and_secondary()
    {
        using var f = new Fixture("{\"selector_marker\":\"x\",\"other\":\"4111111111111111\"}", Policy("Observe") with { AdditionalRules = [new(["absent"], "arbitrary_marker")], SecondaryPanDetection = true });
        await f.Run(); Assert.Empty(f.Logs.Rules); var e = Assert.Single(f.Logs.Events);
        Assert.Equal(1L, e.State["ValidatedExplicitSensitiveMatches"]); Assert.Equal(1L, e.State["ValidatedSecondaryPanMatches"]); Assert.Equal("inspected", e.State["Outcome"]);
    }

    [Fact]
    public async Task Excessive_public_policy_is_not_truncated_or_rejected_by_telemetry()
    {
        var rules = Enumerable.Range(0, 16).Select(i => new CompiledSensitiveRule([$"field{i}"], "PAN")).ToImmutableArray();
        var body = "{\"selector_marker\":\"4111111111111111\",\"other\":\"4111111111111111\"," + string.Join(",", Enumerable.Range(0, 16).Select(i => $"\"field{i}\":\"4111111111111111\"")) + "}";
        using var f = new Fixture(body, Policy() with { AdditionalRules = rules, SecondaryPanDetection = true });
        await f.Run(); Assert.Empty(f.Logs.Rules); Assert.DoesNotContain("4111111111111111", f.Text);
        var e = Assert.Single(f.Logs.Events); Assert.Equal(17L, e.State["ValidatedExplicitSensitiveMatches"]); Assert.Equal(1L, e.State["ValidatedSecondaryPanMatches"]); Assert.Equal(18L, e.State["ValidatedMasks"]);
        Assert.Equal(18L, f.Telemetry.DashboardMetrics.ValidatedMasks);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Multi_precommit_cancel_timeout_discards_rules(bool cancel, bool afterWriter)
    {
        using var f = new Fixture("{\"selector_marker\":\"ordinary\",\"other\":\"4111111111111111\"}", Policy() with { SecondaryPanDetection = true });
        var traversed = false; var checks = 0; var triggered = false;
        f.Clock.OnTimestamp = () =>
        {
            var inside = new StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == "WriteMultiple");
            if (inside) { traversed = true; checks++; }
            if (triggered || (afterWriter ? !traversed || inside : !inside || checks != 5)) return;
            triggered = true; f.Clock.OnTimestamp = null;
            if (cancel) f.Caller.Cancel(); else f.Clock.Advance();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Run); Assert.True(triggered); Assert.Empty(f.Text); Assert.Empty(f.Logs.Rules);
        var e = Assert.Single(f.Logs.Events); Assert.Equal(cancel ? "canceled" : "timed_out", e.State["Outcome"]); Assert.Equal(0L, e.State["ValidatedMasks"]); Assert.Equal(0L, e.State["ValidatedExplicitSensitiveMatches"]); Assert.Equal(0L, e.State["ValidatedSecondaryPanMatches"]);
    }

    [Fact]
    public async Task Trie_indices_follow_normalized_policy_positions_not_selector_text()
    {
        using var f = new Fixture("{}", Policy() with { Segments = ["z", "[*]", "nested"], AdditionalRules = [new(["a"], "Email"), new(["b", "[*]"], "Phone")] });
        await using var deadline = new InspectionDeadline(f.Clock, f.Clock.GetTimestamp(), TimeSpan.FromSeconds(1), f.Context.HttpContext, f.Caller.Token);
        var trie = MultiRuleSelection.Build(f.Policy, deadline);
        Assert.Equal(0, trie.Properties["z"].ArrayItem!.Properties["nested"].RuleIndex);
        Assert.Equal("PAN", trie.Properties["z"].ArrayItem!.Properties["nested"].Transformation);
        Assert.Equal(1, trie.Properties["a"].RuleIndex); Assert.Equal("Email", trie.Properties["a"].Transformation);
        Assert.Equal(2, trie.Properties["b"].ArrayItem!.RuleIndex); Assert.Equal("Phone", trie.Properties["b"].ArrayItem!.Transformation);
    }

    [Theory]
    [InlineData("{}", false)] [InlineData("{\"selector_marker\":null}", false)]
    [InlineData("{\"selector_marker\":[]}", true)] [InlineData("{\"selector_marker\":42}", true)]
    public async Task No_match_partitions_emit_no_rules(string body, bool arraySelector)
    {
        using var f = new Fixture(body, Policy() with { Segments = arraySelector ? ["selector_marker", "[*]"] : ["selector_marker"], SecondaryPanDetection = true });
        await f.Run(); Assert.Empty(f.Logs.Rules); Assert.Equal("inspected", Assert.Single(f.Logs.Events).State["Outcome"]); Assert.Equal(body, f.Text);
    }

    [Theory]
    [InlineData("Mask", "PAN")] [InlineData("Observe", "PAN")] [InlineData("Block", "PAN")]
    [InlineData("Mask", "Email")] [InlineData("Observe", "Email")] [InlineData("Block", "Email")]
    [InlineData("Mask", "Phone")] [InlineData("Observe", "Phone")] [InlineData("Block", "Phone")]
    public async Task Fast_primary_counts_empty_strings_duplicates_and_skips_null(string mode, string transformation)
    {
        using var f = new Fixture("{\"selector_marker\":[\"\",null,\"ordinary\"],\"selector_marker\":[\"4111111111111111\"]}", Policy(mode) with { Segments = ["selector_marker", "[*]"], Transformation = transformation });
        await f.Run(); var e = Assert.Single(f.Logs.Rules); Assert.Equal("primary", e.State["RuleId"]); Assert.Equal(3L, e.State["ValidatedMatches"]); Assert.Equal(mode == "Mask" ? 3L : 0L, e.State["ValidatedMasks"]);
        Assert.Equal(transformation, e.State["Transformation"]);
    }

    [Theory]
    [InlineData("Mask")] [InlineData("Observe")] [InlineData("Block")]
    public async Task Sparse_indices_and_nested_secondary_arrays_preserve_priority(string mode)
    {
        const string body = "{\"items\":[{\"email\":\"4111111111111111\",\"loose\":[\"4111111111111111\",null,{\"phone\":\"4111111111111111\"}]},{\"email\":\"\"}],\"selector_marker\":null}";
        using var f = new Fixture(body, Policy(mode) with { AdditionalRules = [new(["absent"], "PAN"), new(["items", "[*]", "email"], "Email")], SecondaryPanDetection = true });
        await f.Run(); var rules = f.Logs.Rules.ToArray();
        Assert.Equal(new[] { "additional-1", "secondary-pan" }, rules.Select(e => e.State["RuleId"]));
        Assert.Equal(new[] { "Email", "PAN" }, rules.Select(e => e.State["Transformation"]));
        Assert.All(rules, e => { Assert.Equal(2L, e.State["ValidatedMatches"]); Assert.Equal(mode == "Mask" ? 2L : 0L, e.State["ValidatedMasks"]); });
        var terminal = Assert.Single(f.Logs.Events); Assert.Equal(2L, terminal.State["ValidatedExplicitSensitiveMatches"]); Assert.Equal(2L, terminal.State["ValidatedSecondaryPanMatches"]);
        if (mode == "Observe") Assert.Equal(body, f.Text);
    }

    [Theory]
    [InlineData("5")] [InlineData("primary")] [InlineData("additional-0")]
    [InlineData("enabled-first")] [InlineData("enabled-second")] [InlineData("disabled-first")] [InlineData("disabled-all")]
    public async Task Independent_publications_survive_selective_failures(string failure)
    {
        using var f = new Fixture("{\"selector_marker\":\"ordinary\",\"extra\":\"ordinary\",\"other\":\"4111111111111111\"}", Policy() with { AdditionalRules = [new(["extra"], "Email")], SecondaryPanDetection = true });
        var logs = new SelectiveLogger { Failure = failure };
        var transform = new PanResponseTransform(logs, f.Telemetry);
        await transform.ApplyAsync(f.Context, f.Policy);
        Assert.Equal(200, f.Context.HttpContext.Response.StatusCode); Assert.DoesNotContain("4111111111111111", f.Text); Assert.Equal((0, 0L), f.Budget.Snapshot);
        Assert.Equal(4, logs.EnabledCalls);
        var expected = failure switch
        {
            "5" or "enabled-first" or "disabled-first" => new[] { "primary", "additional-0", "secondary-pan" },
            "primary" or "enabled-second" => ["5", "additional-0", "secondary-pan"],
            "additional-0" => ["5", "primary", "secondary-pan"],
            _ => Array.Empty<string>()
        };
        Assert.Equal(expected, logs.Published.Select(e => e.Id));
        Assert.True(logs.Published.Select(e => e.Correlation).Distinct().Count() <= 1);
        Assert.Equal(failure == "disabled-all" ? 0 : failure is "enabled-first" or "enabled-second" or "disabled-first" ? 3 : 4, logs.Attempts.Count);
        Assert.Equal(1, f.Telemetry.DashboardMetrics.Outcomes.Inspected);
    }

    [Fact]
    public async Task Blocking_rule_logger_owns_no_lease()
    {
        using var f = new Fixture("{\"selector_marker\":\"ordinary\"}", Policy());
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new SelectiveLogger { OnRule = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); } };
        var pending = Task.Run(async () => await new PanResponseTransform(logs, f.Telemetry).ApplyAsync(f.Context, f.Policy));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted); Assert.Equal((0, 0L), f.Budget.Snapshot);
            Assert.Equal(1, f.Telemetry.DashboardMetrics.Outcomes.Inspected); Assert.Equal(1, f.Telemetry.DashboardMetrics.Duration.Samples);
            using var next = f.Budget.TryAcquire(4096); Assert.NotNull(next);
            Assert.Contains("REDACTED", f.Text);
        }
        finally { release.Set(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(new[] { "5", "primary" }, logs.Published.Select(e => e.Id));
    }

    [Fact]
    public void Winning_summary_is_immutable_and_notified_once()
    {
        var budget = new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = 4096 } }));
        using var telemetry = new PrivacyTelemetry(budget);
        var builder = ImmutableArray.CreateBuilder<PrivacyTelemetry.RuleMatch>();
        builder.Add(new("primary", "PAN", 9007199254740993L, 2));
        var snapshot = builder.ToImmutable(); var callbacks = new ConcurrentQueue<PrivacyTelemetry.InspectionSummary>();
        using var m = telemetry.TryStartInspection(Policy(), TimeProvider.System, summary => { Assert.Equal((0, 0L), budget.Snapshot); callbacks.Enqueue(summary); })!;
        m.RuleMatches = snapshot; m.ValidatedExplicitSensitiveMatches = 9007199254740993L; m.ValidatedMasks = 2;
        builder[0] = new("secondary-pan", "PAN", 7, 7);
        Parallel.For(0, 64, i => { if (i % 2 == 0) m.Complete(PrivacyInspectionOutcome.Inspected); else m.Dispose(); });
        m.RuleMatches = []; m.Dispose();
        var s = Assert.Single(callbacks); Assert.Equal(new PrivacyTelemetry.RuleMatch("primary", "PAN", 9007199254740993L, 2), Assert.Single(s.RuleMatches));
        Assert.Equal(1, telemetry.DashboardMetrics.Duration.Samples);
        var logger = new Logs(); new PanResponseTransform(logger, telemetry).RecordInspectionCompleted(Policy(), "safe-service", s);
        var e = Assert.Single(logger.Rules); Assert.Equal(9007199254740993L, Assert.IsType<long>(e.State["ValidatedMatches"])); Assert.Equal(2L, e.State["ValidatedMasks"]);
        Assert.Equal("safe-service", e.State["ServiceId"]); Assert.Equal(logger.Events.Single().State["CorrelationId"], e.State["CorrelationId"]);
    }

    private sealed class SelectiveLogger : ILogger<PanResponseTransform>
    {
        public string Failure = ""; public int EnabledCalls; public Action? OnRule;
        public readonly ConcurrentQueue<string> Attempts = new();
        public readonly ConcurrentQueue<(string Id, string Correlation)> Published = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level)
        {
            var call = Interlocked.Increment(ref EnabledCalls);
            if (Failure == "enabled-first" && call == 1 || Failure == "enabled-second" && call == 2) throw new OperationCanceledException("logger_marker");
            return Failure != "disabled-all" && !(Failure == "disabled-first" && call == 1);
        }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (id.Id is not (5 or 6)) return;
            var fields = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(p => p.Key, p => p.Value);
            var key = id.Id == 5 ? "5" : (string)fields["RuleId"]!; Attempts.Enqueue(key);
            if (Failure == key) throw new InvalidOperationException("logger_marker");
            if (id.Id == 6) OnRule?.Invoke();
            Published.Enqueue((key, (string)fields["CorrelationId"]!));
        }
    }
}
