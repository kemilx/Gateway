namespace Gateway;

// Response-local selector trie: at most 1 + 16 rules * 16 steps = 257 nodes.
// This is a node bound, not a bound on total allocations or total inspection memory.
internal sealed class MultiRuleSelection
{
    internal Dictionary<string, MultiRuleSelection> Properties { get; } = new(StringComparer.Ordinal);
    internal MultiRuleSelection? ArrayItem { get; private set; }
    internal string? Transformation { get; private set; }
    internal int RuleIndex { get; private set; }

    internal static MultiRuleSelection Build(CompiledPanPolicy policy, InspectionDeadline deadline)
    {
        deadline.Check();
        var root = new MultiRuleSelection();
        Add(policy.Segments, policy.Transformation, 0);
        for (var i = 0; i < policy.AdditionalRules.Length; i++)
            Add(policy.AdditionalRules[i].Segments, policy.AdditionalRules[i].Transformation, i + 1);
        deadline.Check();
        return root;

        void Add(System.Collections.Immutable.ImmutableArray<string> steps, string transformation, int ruleIndex)
        {
            var node = root;
            foreach (var step in steps)
            {
                deadline.Check();
                if (step == "[*]") node = node.ArrayItem ??= new MultiRuleSelection();
                else
                {
                    if (!node.Properties.TryGetValue(step, out var child))
                        node.Properties.Add(step, child = new MultiRuleSelection());
                    node = child;
                }
            }
            node.Transformation = transformation;
            node.RuleIndex = ruleIndex;
        }
    }
}
