using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gateway.Tests;

// FASE1: configuration/startup and redacted metadata only. Runtime traversal belongs to FASE2.
public sealed class MultiRuleConfigurationTests
{
    private const string RulePrefix = "PanPolicies:Policies:0:AdditionalRules";
    private static JsonObject Defaults() => JsonNode.Parse("""
        {"PanPolicies":{
          "InspectionBudget":{"MaxConcurrentInspections":6,"MaxReservedBodyBytes":6291456},
          "CoverageContract":{"Version":"v1","Required":[{"RouteId":"protected-payment","Selector":"$.payment.cardNumber"}]},
          "Policies":[{"PolicyId":"arbitrary","Version":"v1","RouteId":"protected-payment","Mode":"Mask",
            "Selector":"$.payment.cardNumber","MaxBodyBytes":1048576}]
        }}
        """)!.AsObject();
    private static JsonObject Policy(JsonObject root) => root["PanPolicies"]!["Policies"]![0]!.AsObject();
    private static JsonArray Required(JsonObject root) => root["PanPolicies"]!["CoverageContract"]!["Required"]!.AsArray();
    private static JsonObject Rule(string selector, string? transformation = null)
    {
        var rule = new JsonObject { ["Selector"] = selector };
        if (transformation is not null) rule["Transformation"] = transformation;
        return rule;
    }
    private static JsonObject Requirement(string selector, string route = "protected-payment") =>
        new() { ["RouteId"] = route, ["Selector"] = selector };

    private static WebApplication Build(JsonObject root) => GatewayHost.Build([], configure: b =>
    {
        b.Configuration.Sources.Clear();
        b.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString())));
        b.WebHost.UseUrls("http://127.0.0.1:0");
    });

    private static WebApplication Build(Dictionary<string, string?> values) => GatewayHost.Build([], configure: b =>
    {
        b.Configuration.Sources.Clear();
        b.Configuration.AddInMemoryCollection(values);
        b.WebHost.UseUrls("http://127.0.0.1:0");
    });

    private static async Task Reject(Func<WebApplication> build)
    {
        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var app = build();
            await app.StartAsync();
            await app.StopAsync();
        });
        Assert.NotNull(failure);
        Assert.IsAssignableFrom<InvalidOperationException>(failure);
    }

    [Fact]
    public void Defaults_and_five_argument_constructor_remain_compatible()
    {
        Assert.Empty(new PanPolicyDefinition().AdditionalRules);
        Assert.Equal("PAN", new SensitiveRuleDefinition().Transformation);
        var policy = new CompiledPanPolicy("id", "v1", "route", ["a"], 1024);
        var (id, version, route, segments, bytes) = policy;
        Assert.Equal(("id", "v1", "route", 1024), (id, version, route, bytes));
        Assert.Equal(new[] { "a" }, segments);
        Assert.Empty(policy.AdditionalRules);
        Assert.Equal("PAN", policy.Transformation);
        Assert.Equal("Mask", policy.Mode);
        // A derived record must carry its own rules, with no cached trie from the original.
        var derived = policy with { AdditionalRules = [new(["b"], "Email")] };
        Assert.Empty(policy.AdditionalRules);
        Assert.Equal("Email", Assert.Single(derived.AdditionalRules).Transformation);
    }

    [Theory]
    [InlineData("json-absent", -1)]
    [InlineData("json-empty", 0)]
    [InlineData("json-one", 1)]
    [InlineData("json-max", 15)]
    [InlineData("memory-absent", -1)]
    [InlineData("memory-one", 1)]
    [InlineData("memory-max", 15)]
    public async Task Valid_rule_counts_start_and_missing_transformation_defaults_to_PAN(string scenario, int count)
    {
        var root = Defaults();
        var values = PolicyConfiguration.Defaults();
        if (count >= 0)
        {
            var rules = new JsonArray();
            for (var i = 0; i < count; i++)
            {
                rules.Add(Rule("$.field" + i));
                values[$"{RulePrefix}:{i}:Selector"] = "$.field" + i;
            }
            Policy(root)["AdditionalRules"] = rules;
        }
        await using var app = scenario.StartsWith("json", StringComparison.Ordinal) ? Build(root) : Build(values);
        await app.StartAsync();
        var policy = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        Assert.Equal(Math.Max(0, count), policy.AdditionalRules.Length);
        Assert.All(policy.AdditionalRules, rule => Assert.Equal("PAN", rule.Transformation));
        Assert.Equal("PAN", policy.Transformation);
        Assert.Equal("Mask", policy.Mode);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("memory")]
    public async Task Sixteen_additional_rules_prevent_startup(string provider)
    {
        var root = Defaults();
        var rules = new JsonArray();
        var values = PolicyConfiguration.Defaults();
        for (var i = 0; i < 16; i++)
        {
            rules.Add(Rule("$.field" + i));
            values[$"{RulePrefix}:{i}:Selector"] = "$.field" + i;
        }
        Policy(root)["AdditionalRules"] = rules;
        await Reject(() => provider == "json" ? Build(root) : Build(values));
    }

    [Theory]
    [InlineData("list-null")]
    [InlineData("list-string")]
    [InlineData("list-number")]
    [InlineData("list-boolean")]
    [InlineData("list-object")]
    [InlineData("item-null")]
    [InlineData("item-string")]
    [InlineData("item-number")]
    [InlineData("item-boolean")]
    [InlineData("item-array")]
    [InlineData("item-empty-object")]
    [InlineData("unknown-field")]
    [InlineData("selector-missing")]
    [InlineData("selector-null")]
    [InlineData("selector-empty")]
    [InlineData("selector-number")]
    [InlineData("selector-boolean")]
    [InlineData("selector-object")]
    [InlineData("selector-array")]
    [InlineData("transformation-null")]
    [InlineData("transformation-empty")]
    [InlineData("transformation-number")]
    [InlineData("transformation-boolean")]
    [InlineData("transformation-object")]
    [InlineData("transformation-array")]
    [InlineData("transformation-case")]
    [InlineData("transformation-unknown")]
    public async Task Malformed_JSON_rule_shapes_fail_closed_at_real_startup(string scenario)
    {
        var json = scenario switch
        {
            "list-null" => "null", "list-empty-string" => "\"\"", "list-string" => "\"bad\"",
            "list-number" => "1", "list-boolean" => "true", "list-object" => "{}",
            "list-indexed-object" => "{\"0\":{\"Selector\":\"$.other\"}}",
            "item-null" => "[null]", "item-string" => "[\"\"]", "item-number" => "[1]",
            "item-boolean" => "[true]", "item-array" => "[[]]", "item-empty-object" => "[{}]",
            "unknown-field" => "[{\"Selector\":\"$.other\",\"Unknown\":\"x\"}]",
            "selector-missing" => "[{\"Transformation\":\"Email\"}]",
            "selector-null" => "[{\"Selector\":null}]", "selector-empty" => "[{\"Selector\":\"\"}]",
            "selector-number" => "[{\"Selector\":1}]", "selector-boolean" => "[{\"Selector\":true}]",
            "selector-object" => "[{\"Selector\":{}}]", "selector-array" => "[{\"Selector\":[]}]",
            "transformation-null" => "[{\"Selector\":\"$.other\",\"Transformation\":null}]",
            "transformation-empty" => "[{\"Selector\":\"$.other\",\"Transformation\":\"\"}]",
            "transformation-number" => "[{\"Selector\":\"$.other\",\"Transformation\":1}]",
            "transformation-boolean" => "[{\"Selector\":\"$.other\",\"Transformation\":true}]",
            "transformation-object" => "[{\"Selector\":\"$.other\",\"Transformation\":{}}]",
            "transformation-array" => "[{\"Selector\":\"$.other\",\"Transformation\":[]}]",
            "transformation-case" => "[{\"Selector\":\"$.other\",\"Transformation\":\"email\"}]",
            "transformation-unknown" => "[{\"Selector\":\"$.other\",\"Transformation\":\"Observe\"}]",
            _ => throw new ArgumentException(nameof(scenario))
        };
        var root = Defaults();
        Policy(root)["AdditionalRules"] = JsonNode.Parse(json);
        await Reject(() => Build(root));
    }

    [Theory]
    [InlineData("list-null")]
    [InlineData("list-string")]
    [InlineData("item-null")]
    [InlineData("item-empty-string")]
    [InlineData("item-scalar-with-fields")]
    [InlineData("non-index")]
    [InlineData("index-gap")]
    [InlineData("unknown-field")]
    [InlineData("selector-null")]
    [InlineData("selector-nested")]
    [InlineData("transformation-null")]
    [InlineData("transformation-empty")]
    [InlineData("transformation-nested")]
    [InlineData("transformation-unknown")]
    public async Task Malformed_InMemory_rule_shapes_fail_closed_at_real_startup(string scenario)
    {
        var values = PolicyConfiguration.Defaults();
        values[RulePrefix + ":0:Selector"] = "$.other";
        switch (scenario)
        {
            case "list-null": values[RulePrefix] = null; values.Remove(RulePrefix + ":0:Selector"); break;
            case "list-string": values[RulePrefix] = "bad"; break;
            case "item-null": values[RulePrefix + ":0"] = null; values.Remove(RulePrefix + ":0:Selector"); break;
            case "item-empty-string": values[RulePrefix + ":0"] = ""; break;
            case "item-scalar-with-fields": values[RulePrefix + ":0"] = "bad"; break;
            case "non-index": values[RulePrefix + ":bad:Selector"] = "$.third"; break;
            case "index-gap": values[RulePrefix + ":2:Selector"] = "$.third"; break;
            case "unknown-field": values[RulePrefix + ":0:Unknown"] = "bad"; break;
            case "selector-null": values[RulePrefix + ":0:Selector"] = null; break;
            case "selector-nested": values[RulePrefix + ":0:Selector:0"] = "$.nested"; break;
            case "transformation-null": values[RulePrefix + ":0:Transformation"] = null; break;
            case "transformation-empty": values[RulePrefix + ":0:Transformation"] = ""; break;
            case "transformation-nested": values[RulePrefix + ":0:Transformation:0"] = "Email"; break;
            case "transformation-unknown": values[RulePrefix + ":0:Transformation"] = "Unknown"; break;
        }
        await Reject(() => Build(values));
    }

    // IConfiguration's normalized contract intentionally cannot distinguish [] from "".
    [Theory]
    [InlineData("absent")]
    [InlineData("array")]
    [InlineData("empty-string")]
    public async Task Empty_normalized_lists_are_equivalent(string representation)
    {
        var root = Defaults();
        if (representation != "absent") Policy(root)["AdditionalRules"] = JsonNode.Parse(representation == "array" ? "[]" : "\"\"");
        await using var app = Build(root);
        await app.StartAsync();
        var policy = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        Assert.Empty(policy.AdditionalRules);
        Assert.Equal(new[] { "payment", "cardNumber" }, policy.Segments);
        Assert.Equal("PAN", policy.Transformation);
        await app.StopAsync();
    }

    [Fact]
    public async Task Empty_normalized_list_cannot_satisfy_extra_coverage()
    {
        var root = Defaults();
        Policy(root)["AdditionalRules"] = "";
        Required(root).Add(Requirement("$.contact.email"));
        await Reject(() => Build(root));
    }

    [Theory]
    [InlineData("array")]
    [InlineData("indexed")]
    public async Task Indexed_objects_are_ordered_frozen_array_equivalents(string representation)
    {
        var root = Defaults();
        // Deliberately reverse textual keys: semantic order is canonical numeric index.
        Policy(root)["AdditionalRules"] = JsonNode.Parse(representation == "array"
            ? "[{\"Selector\":\"$.contact.email\",\"Transformation\":\"Email\"},{\"Selector\":\"$.contact.phone\",\"Transformation\":\"Phone\"}]"
            : "{\"1\":{\"Selector\":\"$.contact.phone\",\"Transformation\":\"Phone\"},\"0\":{\"Selector\":\"$.contact.email\",\"Transformation\":\"Email\"}}");
        Required(root).Add(Requirement("$.contact.email"));
        Required(root).Add(Requirement("$.contact.phone"));
        await using var app = Build(root);
        await app.StartAsync();
        var policy = app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!;
        Assert.Equal(new[] { "Email", "Phone" }, policy.AdditionalRules.Select(r => r.Transformation));
        Assert.Equal(new[] { "contact", "email" }, policy.AdditionalRules[0].Segments);
        Assert.Equal(new[] { "contact", "phone" }, policy.AdditionalRules[1].Segments);
        app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value.Policies[0].AdditionalRules.Clear();
        app.Configuration[RulePrefix + ":0:Selector"] = "$.changed";
        Assert.Equal(2, policy.AdditionalRules.Length);
        Assert.Equal("email", policy.AdditionalRules[0].Segments[1]);
        Assert.Same(policy, app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment"));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("bad-type")]
    [InlineData("duplicate")]
    [InlineData("prefix")]
    [InlineData("over-limit")]
    [InlineData("negative")]
    [InlineData("leading-zero")]
    [InlineData("gap")]
    public async Task Indexed_aliases_retain_validation(string scenario)
    {
        var root = Defaults();
        var rules = new JsonObject { [scenario == "negative" ? "-1" : scenario == "leading-zero" ? "00" : scenario == "gap" ? "1" : "0"] =
            Rule(scenario == "duplicate" ? "$.payment.cardNumber" : scenario == "prefix" ? "$.payment" : "$.other", scenario == "bad-type" ? "email" : "Email") };
        if (scenario == "over-limit") for (var i = 1; i < 16; i++) rules[i.ToString()] = Rule("$.field" + i);
        Policy(root)["AdditionalRules"] = rules;
        await Reject(() => Build(root));
    }

    [Theory]
    [InlineData("property-prefix", "$.a", "$.a.b")]
    [InlineData("array-prefix", "$.a[*]", "$.a[*].email")]
    [InlineData("duplicate", "$.a.b", "$.a.b")]
    [InlineData("root-array-prefix", "$[*]", "$[*].email")]
    public async Task Duplicates_and_step_prefixes_are_rejected_in_both_orders_and_rule_positions(string scenario, string a, string b)
    {
        Assert.NotEmpty(scenario);
        foreach (var reverse in new[] { false, true })
        foreach (var bothAdditional in new[] { false, true })
        {
            var root = Defaults();
            var first = reverse ? b : a;
            var second = reverse ? a : b;
            Policy(root)["Selector"] = bothAdditional ? "$.unrelated" : first;
            Required(root)[0]!["Selector"] = bothAdditional ? "$.unrelated" : first;
            Policy(root)["AdditionalRules"] = bothAdditional
                ? new JsonArray(Rule(first, "Email"), Rule(second, "Phone")) : new JsonArray(Rule(second, "Phone"));
            await Reject(() => Build(root));
        }
    }

    [Theory]
    [InlineData("name-not-prefix", "$.a", "$.ab")]
    [InlineData("mixed-branches", "$.a.b", "$.a[*].b")]
    [InlineData("ordinal-case", "$.a.b", "$.a.B")]
    [InlineData("shared-prefix", "$.a.b", "$.a.c")]
    public async Task Disjoint_step_selectors_can_share_a_route(string scenario, string a, string b)
    {
        Assert.NotEmpty(scenario);
        var root = Defaults();
        Policy(root)["Selector"] = a;
        Policy(root)["AdditionalRules"] = new JsonArray(Rule(b, "Email"));
        Required(root)[0]!["Selector"] = a;
        Required(root).Add(Requirement(b));
        await using var app = Build(root);
        await app.StartAsync();
        Assert.Single(app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!.AdditionalRules);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("max-property", 16, 64, true)]
    [InlineData("too-many-steps", 17, 1, false)]
    [InlineData("long-property", 1, 65, false)]
    public async Task Additional_selector_grammar_retains_step_and_length_boundaries(string scenario, int steps, int nameLength, bool valid)
    {
        Assert.NotEmpty(scenario);
        var selector = "$" + string.Concat(Enumerable.Repeat("." + new string('a', nameLength), steps));
        if (scenario == "max-property") Assert.Equal(1041, selector.Length);
        var root = Defaults();
        Policy(root)["AdditionalRules"] = new JsonArray(Rule(selector));
        if (!valid) { await Reject(() => Build(root)); return; }
        await using var app = Build(root);
        await app.StartAsync();
        Assert.Equal(16, app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!.AdditionalRules[0].Segments.Length);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("root-only")]
    [InlineData("numeric-index")]
    [InlineData("property-wildcard")]
    [InlineData("empty-step")]
    [InlineData("escaped-name")]
    [InlineData("array-16")]
    [InlineData("array-17")]
    public async Task Additional_selector_grammar_rejects_unsupported_syntax_and_bounds_array_steps(string scenario)
    {
        var selector = scenario switch
        {
            "root-only" => "$", "numeric-index" => "$.a[0]", "property-wildcard" => "$.*",
            "empty-step" => "$.a..b", "escaped-name" => "$['a.b']",
            "array-16" => "$" + string.Concat(Enumerable.Repeat("[*]", 16)),
            "array-17" => "$" + string.Concat(Enumerable.Repeat("[*]", 17)),
            _ => throw new ArgumentException(nameof(scenario))
        };
        var root = Defaults();
        Policy(root)["AdditionalRules"] = new JsonArray(Rule(selector, "Phone"));
        if (scenario != "array-16") { await Reject(() => Build(root)); return; }
        await using var app = Build(root);
        await app.StartAsync();
        Assert.Equal(16, app.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment")!.AdditionalRules[0].Segments.Length);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("PAN")]
    [InlineData("Email")]
    [InlineData("Phone")]
    public async Task Snapshot_types_start_with_primary_and_remain_distinct_at_maximum_rule_count(string primary)
    {
        var root = Defaults();
        Policy(root)["Transformation"] = primary;
        var rules = new JsonArray();
        var types = new[] { "Phone", "Email", "PAN" };
        for (var i = 0; i < 15; i++) rules.Add(Rule("$.field" + i, types[i % 3]));
        Policy(root)["AdditionalRules"] = rules;
        await using var app = Build(root);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var summary = JsonDocument.Parse(await client.GetStringAsync("/dashboard/api/summary"));
        var policy = summary.RootElement.GetProperty("policies")[0];
        Assert.Equal(16, policy.GetProperty("ruleCount").GetInt32());
        Assert.Equal(primary, policy.GetProperty("transformation").GetString());
        var expected = primary switch
        {
            "PAN" => new[] { "PAN", "Phone", "Email" },
            "Email" => new[] { "Email", "Phone", "PAN" },
            _ => new[] { "Phone", "Email", "PAN" }
        };
        Assert.Equal(expected, policy.GetProperty("transformations").EnumerateArray().Select(value => value.GetString()));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("duplicate-pair")]
    [InlineData("uncovered")]
    [InlineData("case-mismatch")]
    [InlineData("unknown-route")]
    public async Task Coverage_rejects_duplicate_unknown_or_uncovered_exact_pairs(string scenario)
    {
        var root = Defaults();
        Policy(root)["AdditionalRules"] = new JsonArray(Rule("$.contact.email", "Email"));
        Required(root).Add(scenario switch
        {
            "duplicate-pair" => Requirement("$.payment.cardNumber"),
            "uncovered" => Requirement("$.other"),
            "case-mismatch" => Requirement("$.contact.Email"),
            "unknown-route" => Requirement("$.contact.email", "unknown"),
            _ => throw new ArgumentException(nameof(scenario))
        });
        await Reject(() => Build(root));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public async Task Coverage_limit_is_32_pairs_not_32_routes(int count, bool valid)
    {
        var root = Defaults();
        var policies = root["PanPolicies"]!["Policies"]!.AsArray();
        var first = Policy(root);
        var second = (JsonObject)first.DeepClone();
        second["RouteId"] = "passthrough";
        second["PolicyId"] = "second";
        policies.Add(second);
        Required(root).Clear();
        foreach (var policy in new[] { first, second })
        {
            var rules = new JsonArray();
            policy["Selector"] = "$.field0";
            for (var i = 1; i <= 15; i++) rules.Add(Rule("$.field" + i));
            policy["AdditionalRules"] = rules;
            for (var i = 0; i <= 15; i++) Required(root).Add(Requirement("$.field" + i, policy["RouteId"]!.GetValue<string>()));
        }
        if (count == 33) Required(root).Add(Requirement("$.field0"));
        if (!valid) { await Reject(() => Build(root)); return; }
        await using var app = Build(root);
        await app.StartAsync();
        Assert.Equal(32, app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value.CoverageContract!.Required.Count);
        await app.StopAsync();
    }

    [Fact]
    public async Task Catalog_deep_freezes_rules_and_snapshot_only_exposes_ordered_distinct_types()
    {
        var values = PolicyConfiguration.Defaults();
        values[RulePrefix + ":0:Selector"] = "$.contact.email";
        values[RulePrefix + ":0:Transformation"] = "Email";
        values[RulePrefix + ":1:Selector"] = "$.contact.phone";
        values[RulePrefix + ":1:Transformation"] = "Phone";
        values[RulePrefix + ":2:Selector"] = "$.other.email";
        values[RulePrefix + ":2:Transformation"] = "Email";
        values["PanPolicies:CoverageContract:Required:1:RouteId"] = "protected-payment";
        values["PanPolicies:CoverageContract:Required:1:Selector"] = "$.contact.email";
        await using var app = Build(values);
        await app.StartAsync();
        var catalog = app.Services.GetRequiredService<PanPolicyCatalog>();
        var original = catalog.Find("protected-payment")!;
        var definition = app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value.Policies[0];
        definition.Selector = "$.changed";
        definition.Transformation = "Phone";
        definition.AdditionalRules[0].Selector = "$.changed";
        definition.AdditionalRules[0].Transformation = "Phone";
        definition.AdditionalRules.Clear();
        app.Configuration[RulePrefix + ":0:Selector"] = "$.changed";
        app.Configuration["PanPolicies:CoverageContract:Required:1:Selector"] = "$.changed";
        ((IConfigurationRoot)app.Configuration).Reload();
        Assert.Same(original, catalog.Find("protected-payment"));
        Assert.Equal(new[] { "payment", "cardNumber" }, original.Segments);
        Assert.Equal(new[] { "contact", "email" }, original.AdditionalRules[0].Segments);
        Assert.Equal(new[] { "Email", "Phone", "Email" }, original.AdditionalRules.Select(rule => rule.Transformation));
        Assert.Equal("PAN", original.Transformation);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var summary = JsonDocument.Parse(await client.GetStringAsync("/dashboard/api/summary"));
        Assert.Equal(1, summary.RootElement.GetProperty("schemaVersion").GetInt32());
        var metadata = summary.RootElement.GetProperty("policies")[0];
        Assert.Equal(new[] { "maxBodyBytes", "mode", "policyId", "routeId", "ruleCount", "transformation", "transformations", "version" },
            metadata.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(4, metadata.GetProperty("ruleCount").GetInt32());
        Assert.Equal("PAN", metadata.GetProperty("transformation").GetString());
        Assert.Equal(new[] { "PAN", "Email", "Phone" }, metadata.GetProperty("transformations").EnumerateArray().Select(value => value.GetString()));
        await app.StopAsync();
    }
}
