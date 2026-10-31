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
    public int InspectionTimeoutSeconds { get; set; } = 10;
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
    public string Severity { get; set; } = "High";
    public string FailureBehavior { get; set; } = "FailClosed";
    public string NonInspectableBehavior { get; set; } = "ExcludeNonJson";
    public string Transformation { get; set; } = "PAN";
    public string Selector { get; set; } = "";
    public int MaxBodyBytes { get; set; }
    public bool SecondaryPanDetection { get; set; }
    public List<SensitiveRuleDefinition> AdditionalRules { get; set; } = [];
}

public sealed class SensitiveRuleDefinition
{
    public string Selector { get; set; } = "";
    public string Transformation { get; set; } = "PAN";
}

public sealed class PanPolicyValidator(IReadOnlyList<RouteConfig> knownRoutes) : IValidateOptions<PanPolicyOptions>
{
    private readonly FrozenSet<string> knownRouteIds = knownRoutes.Select(route => route.RouteId)
        .ToFrozenSet(StringComparer.Ordinal);
    // Operational ceilings bound buffering and configuration cardinality; not PAN business rules.
    public const int MaximumBodyBytes = 16 * 1024 * 1024;
    private static readonly Regex Identifier = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);
    // Each property or array wildcard consumes one traversal step; retain the existing ceilings.
    private static readonly Regex Selector = new(@"\A\$(?:\.[A-Za-z_][A-Za-z0-9_]{0,63}|\[\*\]){1,16}\z", RegexOptions.CultureInvariant);

    public ValidateOptionsResult Validate(string? name, PanPolicyOptions options)
    {
        if (options.InspectionBudget is not
            {
                MaxConcurrentInspections: >= 1 and <= 1024,
                InspectionTimeoutSeconds: >= 1 and <= 120,
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
                || policy.Mode is not ("Mask" or "Block" or "Observe")
                || policy.Severity is not ("Low" or "Medium" or "High" or "Critical")
                || policy.FailureBehavior != "FailClosed"
                || policy.NonInspectableBehavior != "ExcludeNonJson"
                || policy.Transformation is not ("PAN" or "Email" or "Phone")
                || !SafeSelector(policy.Selector)
                || !ValidRules(policy)
                || policy.MaxBodyBytes is <= 0 or > MaximumBodyBytes
                || policy.MaxBodyBytes > options.InspectionBudget.MaxReservedBodyBytes)
                return ValidateOptionsResult.Fail("Invalid PAN policy: check identifiers, unique existing RouteId, exact Mask, Block or Observe mode, exact PAN, Email or Phone transformation, property/array selector and body limit.");
        }

        var contract = options.CoverageContract;
        if (contract is null || !SafeIdentifier(contract.Version) || contract.Required is not { Count: > 0 and <= 32 })
            return ValidateOptionsResult.Fail("PanPolicies requires a coverage contract with a safe Version and between 1 and 32 requirements.");

        var requiredPairs = new HashSet<(string RouteId, string Selector)>();
        foreach (var requirement in contract.Required)
        {
            if (requirement is null || !SafeIdentifier(requirement.RouteId)
                || !knownRouteIds.Contains(requirement.RouteId) || !requiredPairs.Add((requirement.RouteId, requirement.Selector))
                || !SafeSelector(requirement.Selector))
                return ValidateOptionsResult.Fail("Invalid PAN coverage requirement: check unique existing RouteId/Selector pair and property/array selector.");

            if (!options.Policies.Any(policy => string.Equals(policy.RouteId, requirement.RouteId, StringComparison.Ordinal)
                && (string.Equals(policy.Selector, requirement.Selector, StringComparison.Ordinal)
                    || policy.AdditionalRules.Any(rule => string.Equals(rule.Selector, requirement.Selector, StringComparison.Ordinal)))))
                return ValidateOptionsResult.Fail("PAN policies do not satisfy the minimum coverage contract.");
        }
        return ValidateOptionsResult.Success;
    }

    private static bool SafeIdentifier(string? value) => value is { Length: > 0 and <= 64 } && Identifier.IsMatch(value);
    private static bool SafeSelector(string? value) => value is { Length: > 0 and <= 1041 } && Selector.IsMatch(value);

    internal static ImmutableArray<string> CompileSelector(string selector) =>
        Regex.Matches(selector, @"[A-Za-z_][A-Za-z0-9_]*|\[\*\]")
            .Select(match => match.Value).ToImmutableArray();

    private static bool ValidRules(PanPolicyDefinition policy)
    {
        if (policy.AdditionalRules is not { Count: <= 15 }) return false;
        var selectors = new List<ImmutableArray<string>> { CompileSelector(policy.Selector) };
        foreach (var rule in policy.AdditionalRules)
        {
            if (rule is null || !SafeSelector(rule.Selector)
                || rule.Transformation is not ("PAN" or "Email" or "Phone")) return false;
            var steps = CompileSelector(rule.Selector);
            foreach (var existing in selectors)
                if (steps.Take(Math.Min(steps.Length, existing.Length))
                    .SequenceEqual(existing.Take(Math.Min(steps.Length, existing.Length)), StringComparer.Ordinal)) return false;
            selectors.Add(steps);
        }
        return true;
    }
}

public sealed record CompiledPanPolicy(string PolicyId, string Version, string RouteId,
    ImmutableArray<string> Segments, int MaxBodyBytes)
{
    public string Mode { get; init; } = "Mask";
    public string Severity { get; init; } = "High";
    public string FailureBehavior { get; init; } = "FailClosed";
    public string NonInspectableBehavior { get; init; } = "ExcludeNonJson";
    public string Transformation { get; init; } = "PAN";
    public bool SecondaryPanDetection { get; init; }
    public ImmutableArray<CompiledSensitiveRule> AdditionalRules { get; init; } = [];
}

public sealed record CompiledSensitiveRule(ImmutableArray<string> Segments, string Transformation);

// Each catalog binds once and remains immutable, including catalogs retained by in-flight requests.
public sealed class PanPolicyCatalog
{
    private readonly FrozenDictionary<string, CompiledPanPolicy> policies;

    public PanPolicyCatalog(IOptions<PanPolicyOptions> options, IConfiguration? configuration = null)
    {
        // The binder can silently elide malformed collection data. Check only the new subtree.
        if (configuration is not null) ValidateRuleShape(configuration);
        policies = options.Value.Policies.Select(p => new CompiledPanPolicy(p.PolicyId, p.Version, p.RouteId,
            PanPolicyValidator.CompileSelector(p.Selector), p.MaxBodyBytes)
            {
                Mode = p.Mode, Transformation = p.Transformation, SecondaryPanDetection = p.SecondaryPanDetection,
                Severity = p.Severity, FailureBehavior = p.FailureBehavior, NonInspectableBehavior = p.NonInspectableBehavior,
                AdditionalRules = p.AdditionalRules.Select(rule => new CompiledSensitiveRule(
                    PanPolicyValidator.CompileSelector(rule.Selector), rule.Transformation)).ToImmutableArray()
            }).ToFrozenDictionary(p => p.RouteId, StringComparer.Ordinal);
    }

    internal static void ValidateRuleShape(IConfiguration configuration)
    {
        foreach (var policy in configuration.GetSection("PanPolicies:Policies").GetChildren())
        {
            foreach (var field in policy.GetChildren())
            {
                var valid = field.Key.Equals("Severity", StringComparison.OrdinalIgnoreCase)
                    ? field.Value is "Low" or "Medium" or "High" or "Critical"
                    : field.Key.Equals("FailureBehavior", StringComparison.OrdinalIgnoreCase)
                        ? field.Value == "FailClosed"
                        : field.Key.Equals("NonInspectableBehavior", StringComparison.OrdinalIgnoreCase)
                            ? field.Value == "ExcludeNonJson" : true;
                if (!valid || ((field.Key.Equals("Severity", StringComparison.OrdinalIgnoreCase)
                    || field.Key.Equals("FailureBehavior", StringComparison.OrdinalIgnoreCase)
                    || field.Key.Equals("NonInspectableBehavior", StringComparison.OrdinalIgnoreCase)) && field.GetChildren().Any()))
                    throw new OptionsValidationException(Options.DefaultName, typeof(PanPolicyOptions), ["Invalid policy metadata configuration."]);
            }
            var detector = policy.GetChildren().FirstOrDefault(child => child.Key.Equals("SecondaryPanDetection", StringComparison.OrdinalIgnoreCase));
            // Validate the effective IConfiguration scalar, not the binder's permissive bool conversion.
            if (detector is not null && ((!string.Equals(detector.Value, "true", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(detector.Value, "false", StringComparison.OrdinalIgnoreCase)) || detector.GetChildren().Any()))
                throw new OptionsValidationException(Options.DefaultName, typeof(PanPolicyOptions),
                    ["SecondaryPanDetection requires a true or false scalar (case-insensitive, without whitespace)."]);
            var section = policy.GetChildren().FirstOrDefault(child => child.Key.Equals("AdditionalRules", StringComparison.OrdinalIgnoreCase));
            if (section is null) continue;
            var items = section.GetChildren().ToArray();
            if ((items.Length == 0 && section.Value != "") || (items.Length != 0 && section.Value is not null)
                || items.Length > 15) InvalidShape();
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item.Key != i.ToString(System.Globalization.CultureInfo.InvariantCulture) || item.Value is not null) InvalidShape();
                var fields = item.GetChildren().ToArray();
                if (!fields.Any(field => field.Key.Equals("Selector", StringComparison.OrdinalIgnoreCase))) InvalidShape();
                foreach (var field in fields)
                    if ((!field.Key.Equals("Selector", StringComparison.OrdinalIgnoreCase)
                            && !field.Key.Equals("Transformation", StringComparison.OrdinalIgnoreCase))
                        || field.Value is null || field.GetChildren().Any()) InvalidShape();
            }
        }
    }

    private static void InvalidShape() => throw new OptionsValidationException(Options.DefaultName,
        typeof(PanPolicyOptions), ["Invalid AdditionalRules configuration shape."]);

    public CompiledPanPolicy? Find(string routeId) => policies.GetValueOrDefault(routeId);
    internal IEnumerable<CompiledPanPolicy> Policies => policies.Values;
}
