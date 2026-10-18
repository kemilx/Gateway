using Gateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gateway.Tests;

internal static class PolicyConfiguration
{
    internal static Dictionary<string, string?> Defaults() => new()
    {
        ["PanPolicies:InspectionBudget:MaxConcurrentInspections"] = "6",
        ["PanPolicies:InspectionBudget:MaxReservedBodyBytes"] = "6291456",
        ["PanPolicies:CoverageContract:Version"] = "v1",
        ["PanPolicies:CoverageContract:Required:0:RouteId"] = "protected-payment",
        ["PanPolicies:CoverageContract:Required:0:Selector"] = "$.payment.cardNumber",
        ["PanPolicies:Policies:0:PolicyId"] = "PAN",
        ["PanPolicies:Policies:0:Version"] = "v1",
        ["PanPolicies:Policies:0:RouteId"] = "protected-payment",
        ["PanPolicies:Policies:0:Mode"] = "Mask",
        ["PanPolicies:Policies:0:Selector"] = "$.payment.cardNumber",
        ["PanPolicies:Policies:0:MaxBodyBytes"] = "1048576"
    };
}

public sealed class PolicyConfigurationTests
{
    [Theory]
    [InlineData("missing", "")]
    [InlineData("empty-required", "")]
    [InlineData("missing-version", "")]
    [InlineData("duplicate", "")]
    [InlineData("duplicate-other-selector", "")]
    [InlineData("too-many", "")]
    [InlineData("Version", "")]
    [InlineData("Version", "bad version")]
    [InlineData("Version", "unsafe\nlog")]
    [InlineData("Version", "12345678901234567890123456789012345678901234567890123456789012345")]
    [InlineData("RouteId", "")]
    [InlineData("RouteId", "../route")]
    [InlineData("RouteId", "unknown")]
    [InlineData("RouteId", "Protected-payment")]
    [InlineData("RouteId", "passthrough")]
    [InlineData("Selector", "")]
    [InlineData("Selector", "$")]
    [InlineData("Selector", "$.payment[0].cardNumber")]
    [InlineData("Selector", "$.*.cardNumber")]
    [InlineData("Selector", "$.payment..cardNumber")]
    [InlineData("Selector", "$.Payment.cardNumber")]
    [InlineData("Selector", "$.payment.other")]
    [InlineData("policy-route", "passthrough")]
    [InlineData("policy-selector", "$.payment.other")]
    [InlineData("policy-selector", "$.payment.CardNumber")]
    [InlineData("missing-policy", "")]
    public async Task Invalid_or_unsatisfied_coverage_prevents_startup(string field, string value)
    {
        const string contract = "PanPolicies:CoverageContract:";
        const string required = contract + "Required:";
        var values = PolicyConfiguration.Defaults();
        if (field is "missing" or "empty-required" or "missing-version")
        {
            var prefix = field == "missing" ? contract : field == "empty-required" ? required : contract + "Version";
            foreach (var key in values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                values.Remove(key);
        }
        else if (field is "duplicate" or "duplicate-other-selector" or "too-many")
        {
            var count = field == "too-many" ? 33 : 2;
            for (var i = 1; i < count; i++)
            {
                values[$"{required}{i}:RouteId"] = "protected-payment";
                values[$"{required}{i}:Selector"] = field == "duplicate-other-selector" ? "$.other" : "$.payment.cardNumber";
            }
        }
        else if (field == "missing-policy") values[required + "1:RouteId"] = "passthrough";
        else if (field.StartsWith("policy-", StringComparison.Ordinal))
            values[$"PanPolicies:Policies:0:{(field == "policy-route" ? "RouteId" : "Selector")}"] = value;
        else values[contract + (field == "Version" ? field : "Required:0:" + field)] = value;

        // A valid but uncovered second requirement, rather than an invalid requirement.
        if (field == "missing-policy") values[required + "1:Selector"] = "$.payment.cardNumber";
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = GatewayHost.Build([], configure: b =>
            {
                b.Configuration.Sources.Clear();
                b.Configuration.AddInMemoryCollection(values);
                b.WebHost.UseUrls("http://127.0.0.1:0");
            });
            await app.StartAsync();
        });
        Assert.IsType<OptionsValidationException>(failure.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Extra_valid_policy_is_allowed_and_can_satisfy_an_additional_requirement(bool requireExtra)
    {
        var values = PolicyConfiguration.Defaults();
        foreach (var item in PolicyConfiguration.Defaults().Where(item => item.Key.StartsWith("PanPolicies:Policies:", StringComparison.Ordinal)))
            values[item.Key.Replace(":0:", ":1:")] = item.Value;
        values["PanPolicies:Policies:1:RouteId"] = "passthrough";
        values["PanPolicies:Policies:1:Selector"] = "$.other.pan";
        if (requireExtra)
        {
            values["PanPolicies:CoverageContract:Required:1:RouteId"] = "passthrough";
            values["PanPolicies:CoverageContract:Required:1:Selector"] = "$.other.pan";
        }
        await using var app = GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(values);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await app.StartAsync();
        var catalog = app.Services.GetRequiredService<PanPolicyCatalog>();
        Assert.NotNull(catalog.Find("protected-payment"));
        Assert.Equal(new[] { "other", "pan" }, catalog.Find("passthrough")!.Segments);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("missing", "")]
    [InlineData("empty", "")]
    [InlineData("PolicyId", "unsafe\nlog")]
    [InlineData("PolicyId", "")]
    [InlineData("Version", "bad version")]
    [InlineData("RouteId", "../route")]
    [InlineData("RouteId", "protected-payments")]
    [InlineData("Mode", "0")]
    [InlineData("Mode", "mask")]
    [InlineData("Mode", "MASK")]
    [InlineData("Mode", " Mask")]
    [InlineData("Mode", "Mask ")]
    [InlineData("Mode", "1")]
    [InlineData("Mode", "")]
    [InlineData("Selector", "$.payment[0].cardNumber")]
    [InlineData("Selector", "$.*.cardNumber")]
    [InlineData("Selector", "$.payment..cardNumber")]
    [InlineData("Selector", "$")]
    [InlineData("MaxBodyBytes", "0")]
    [InlineData("MaxBodyBytes", "-1")]
    [InlineData("MaxBodyBytes", "16777217")]
    [InlineData("duplicate", "")]
    public async Task Invalid_configuration_prevents_startup(string field, string value)
    {
        var values = PolicyConfiguration.Defaults();
        if (field is "missing" or "empty") values.Clear();
        if (field == "empty") values["PanPolicies:Policies"] = "";
        else if (field == "duplicate")
            foreach (var item in PolicyConfiguration.Defaults().Where(item => item.Key.StartsWith("PanPolicies:Policies:", StringComparison.Ordinal)))
                values[item.Key.Replace(":0:", ":1:")] = item.Value;
        else if (field != "missing") values[$"PanPolicies:Policies:0:{field}"] = value;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = GatewayHost.Build([], configure: b =>
            {
                b.Configuration.Sources.Clear();
                b.Configuration.AddInMemoryCollection(values);
                b.WebHost.UseUrls("http://127.0.0.1:0");
            });
            await app.StartAsync();
        });
        // YARP loads its initial routes while mapping endpoints and wraps the options failure.
        Assert.IsType<OptionsValidationException>(failure.InnerException);
    }

    [Fact]
    public async Task Shipped_appsettings_binds_and_starts_with_approved_defaults()
    {
        await using var app = GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "pan-policy-defaults.json"),
                optional: false, reloadOnChange: false);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await app.StartAsync();
        var options = app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value;
        var budget = Assert.IsType<InspectionBudgetOptions>(options.InspectionBudget);
        Assert.Equal(6, budget.MaxConcurrentInspections);
        Assert.Equal(6291456, budget.MaxReservedBodyBytes);
        var contract = Assert.IsType<PanCoverageContract>(options.CoverageContract);
        Assert.Equal("v1", contract.Version);
        var requirement = Assert.Single(contract.Required);
        Assert.Equal("protected-payment", requirement.RouteId);
        Assert.Equal("$.payment.cardNumber", requirement.Selector);
        var definition = Assert.Single(options.Policies);
        Assert.Equal("PAN", definition.PolicyId);
        Assert.Equal("v1", definition.Version);
        Assert.Equal("protected-payment", definition.RouteId);
        Assert.Equal("Mask", definition.Mode);
        Assert.Equal("$.payment.cardNumber", definition.Selector);
        Assert.Equal(1048576, definition.MaxBodyBytes);
        await app.StopAsync();
    }

    [Fact]
    public void Unknown_configuration_field_prevents_build()
    {
        var values = PolicyConfiguration.Defaults();
        values["PanPolicies:Policies:0:UnknownField"] = "synthetic";
        var failure = Assert.Throws<InvalidOperationException>(() => GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(values);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        }));
        var bindingFailure = Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Contains("UnknownField", bindingFailure.ToString());
        Assert.Contains("ErrorOnUnknownConfiguration", bindingFailure.Message);
    }

    [Fact]
    public async Task Catalog_is_immutable_after_configuration_reload_or_options_mutation()
    {
        await using var app = GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await app.StartAsync();
        var catalog = app.Services.GetRequiredService<PanPolicyCatalog>();
        var original = catalog.Find("protected-payment")!;
        app.Configuration["PanPolicies:Policies:0:Selector"] = "$.other";
        app.Configuration["PanPolicies:CoverageContract:Version"] = "v2";
        app.Configuration["PanPolicies:CoverageContract:Required:0:Selector"] = "$.other";
        ((IConfigurationRoot)app.Configuration).Reload();
        app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value.Policies.Clear();
        Assert.Same(original, catalog.Find("protected-payment"));
        Assert.Equal(new[] { "payment", "cardNumber" }, original.Segments);
        Assert.Equal(1048576, original.MaxBodyBytes);
        await app.StopAsync();
    }
}
