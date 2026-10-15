using Gateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gateway.Tests;

public sealed class InspectionBudgetTests
{
    [Theory]
    [InlineData("missing", "")]
    [InlineData("MaxConcurrentInspections", "0")]
    [InlineData("MaxConcurrentInspections", "1025")]
    [InlineData("MaxReservedBodyBytes", "0")]
    [InlineData("MaxReservedBodyBytes", "1073741825")]
    [InlineData("MaxReservedBodyBytes", "1048575")]
    [InlineData("UnknownField", "1")]
    public void Invalid_budget_prevents_build(string field, string value)
    {
        var values = PolicyConfiguration.Defaults();
        if (field == "missing")
            foreach (var key in values.Keys.Where(k => k.Contains(":InspectionBudget:")).ToArray()) values.Remove(key);
        else values["PanPolicies:InspectionBudget:" + field] = value;
        Assert.Throws<InvalidOperationException>(() => GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(values);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        }));
    }

    [Fact]
    public void Atomic_limits_idempotent_release_overflow_and_immutable_snapshot()
    {
        var options = new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 2, MaxReservedBodyBytes = 10 } };
        var budget = new InspectionBudget(Options.Create(options));
        options.InspectionBudget.MaxReservedBodyBytes = long.MaxValue;
        using var first = budget.TryAcquire(6);
        Assert.NotNull(first);
        Assert.Null(budget.TryAcquire(long.MaxValue));
        Assert.Null(budget.TryAcquire(5));
        var second = budget.TryAcquire(4);
        Assert.NotNull(second);
        Assert.Null(budget.TryAcquire(1));
        Parallel.For(0, 100, _ => second.Dispose());
        Assert.Equal((1, 6L), budget.Snapshot);
        first.Dispose();
        Assert.Equal((0, 0L), budget.Snapshot);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryAcquire(0));
    }
}
