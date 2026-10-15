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
            foreach (var item in PolicyConfiguration.Defaults())
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
        var definition = Assert.Single(app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value.Policies);
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
        ((IConfigurationRoot)app.Configuration).Reload();
        app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value.Policies.Clear();
        Assert.Same(original, catalog.Find("protected-payment"));
        Assert.Equal(new[] { "payment", "cardNumber" }, original.Segments);
        Assert.Equal(1048576, original.MaxBodyBytes);
        await app.StopAsync();
    }
}
