using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace Gateway;

public sealed class PanPolicyOptions
{
    public const string SectionName = "PanPolicies";
    public InspectionBudgetOptions? InspectionBudget { get; set; }
    public PanCoverageContract? CoverageContract { get; set; }
    public List<PanPolicyDefinition> Policies { get; set; } = [];
}

public sealed class InspectionBudgetOptions
{
    public int MaxConcurrentInspections { get; set; } = 6;
    public long MaxReservedBodyBytes { get; set; } = 6 * 1024 * 1024;
}

public sealed class PanCoverageContract
{
    public string Version { get; set; } = "";
    public List<PanCoverageRequirement> Required { get; set; } = [];
}

public sealed class PanCoverageRequirement
{
    public string RouteId { get; set; } = "";
    public string Selector { get; set; } = "";
}

public sealed class PanPolicyDefinition
{
    public string PolicyId { get; set; } = "";
    public string Version { get; set; } = "";
    public string RouteId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Selector { get; set; } = "";
    public int MaxBodyBytes { get; set; }
}

public sealed class PanPolicyValidator(IReadOnlyList<RouteConfig> knownRoutes) : IValidateOptions<PanPolicyOptions>
{
    private readonly FrozenSet<string> knownRouteIds = knownRoutes.Select(route => route.RouteId)
        .ToFrozenSet(StringComparer.Ordinal);
    // Operational ceilings bound buffering and configuration cardinality; not PAN business rules.
    public const int MaximumBodyBytes = 16 * 1024 * 1024;
    private static readonly Regex Identifier = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Selector = new(@"\A\$(?:\.[A-Za-z_][A-Za-z0-9_]{0,63}){1,16}\z", RegexOptions.CultureInvariant);

    public ValidateOptionsResult Validate(string? name, PanPolicyOptions options)
    {
        if (options.InspectionBudget is not
            {
                MaxConcurrentInspections: >= 1 and <= 1024,
                MaxReservedBodyBytes: >= 1 and <= 1073741824
            })
            return ValidateOptionsResult.Fail("PanPolicies requires an inspection budget: concurrency 1..1024 and bytes 1..1073741824.");
        if (options.Policies is not { Count: > 0 and <= 32 })
            return ValidateOptionsResult.Fail("PanPolicies requires between 1 and 32 policies.");

        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in options.Policies)
        {
            if (policy is null || !SafeIdentifier(policy.PolicyId) || !SafeIdentifier(policy.Version)
                || !SafeIdentifier(policy.RouteId) || !knownRouteIds.Contains(policy.RouteId) || !routes.Add(policy.RouteId)
                || !string.Equals(policy.Mode, "Mask", StringComparison.Ordinal)
                || !SafeSelector(policy.Selector)
                || policy.MaxBodyBytes is <= 0 or > MaximumBodyBytes
                || policy.MaxBodyBytes > options.InspectionBudget.MaxReservedBodyBytes)
                return ValidateOptionsResult.Fail("Invalid PAN policy: check identifiers, unique existing RouteId, exact Mask mode, object selector and body limit.");
        }

        var contract = options.CoverageContract;
        if (contract is null || !SafeIdentifier(contract.Version) || contract.Required is not { Count: > 0 and <= 32 })
            return ValidateOptionsResult.Fail("PanPolicies requires a coverage contract with a safe Version and between 1 and 32 requirements.");

        var requiredRoutes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in contract.Required)
        {
            if (requirement is null || !SafeIdentifier(requirement.RouteId)
                || !knownRouteIds.Contains(requirement.RouteId) || !requiredRoutes.Add(requirement.RouteId)
                || !SafeSelector(requirement.Selector))
                return ValidateOptionsResult.Fail("Invalid PAN coverage requirement: check unique existing RouteId and object selector.");

            if (!options.Policies.Any(policy => string.Equals(policy.RouteId, requirement.RouteId, StringComparison.Ordinal)
                && string.Equals(policy.Selector, requirement.Selector, StringComparison.Ordinal)))
                return ValidateOptionsResult.Fail("PAN policies do not satisfy the minimum coverage contract.");
        }
        return ValidateOptionsResult.Success;
    }

    private static bool SafeIdentifier(string? value) => value is { Length: > 0 and <= 64 } && Identifier.IsMatch(value);
    private static bool SafeSelector(string? value) => value is { Length: > 0 and <= 1041 } && Selector.IsMatch(value);
}

public sealed record CompiledPanPolicy(string PolicyId, string Version, string RouteId,
    ImmutableArray<string> Segments, int MaxBodyBytes);

// IOptions (not IOptionsMonitor/Snapshot): bind once, then copy into immutable process-lifetime data.
public sealed class PanPolicyCatalog(IOptions<PanPolicyOptions> options)
{
    private readonly FrozenDictionary<string, CompiledPanPolicy> policies = options.Value.Policies
        .Select(p => new CompiledPanPolicy(p.PolicyId, p.Version, p.RouteId,
            p.Selector[2..].Split('.').ToImmutableArray(), p.MaxBodyBytes))
        .ToFrozenDictionary(p => p.RouteId, StringComparer.Ordinal);

    public CompiledPanPolicy? Find(string routeId) => policies.GetValueOrDefault(routeId);
}
