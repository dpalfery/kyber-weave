namespace KyberWeave.Core.Arbiter.Rules;

/// <summary>
/// Maps <c>arbiter.model-targeting.mode</c> onto the two shipped model-targeting rules, whose enabled
/// and shadow state the mode owns. A host cannot set shadow on them directly; the mode is the one switch.
/// </summary>
/// <remarks>
/// The mapping runs in two places: when the configuration is loaded, so the rules a host reads are the
/// rules that will run, and when an evaluation starts, so a configuration built in code with a mode
/// the shipped rules were not loaded under still behaves as its mode says.
/// </remarks>
internal static class ModelTargetingRules
{
    /// <summary>The shipped rule that picks a difficulty's profile.</summary>
    public const string DifficultyRuleId = "KW-ARB-DIFFICULTY-001";

    /// <summary>The shipped rule that climbs the ladder after a failed attempt.</summary>
    public const string LadderRuleId = "KW-ARB-LADDER-001";

    private const string Off = "off";
    private const string Enforce = "enforce";

    /// <summary>Returns <paramref name="rules"/> with the model-targeting rules set for <paramref name="mode"/>.</summary>
    /// <param name="rules">The effective rule set.</param>
    /// <param name="mode"><c>off</c> disables both rules, <c>shadow</c> records them without effect, and <c>enforce</c> lets them gate.
    /// Any other mode is treated as shadow, so an unrecognised mode never gates a dispatch.</param>
    public static IReadOnlyList<ArbiterRule> Apply(IReadOnlyList<ArbiterRule> rules, string mode)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(mode);

        List<ArbiterRule> applied = new(rules.Count);
        foreach (ArbiterRule rule in rules)
        {
            if (!IsModelTargetingRule(rule.Id))
            {
                applied.Add(rule);
                continue;
            }

            applied.Add(mode switch
            {
                Off => rule with { Enabled = false },
                Enforce => rule with { Shadow = false },
                _ => rule with { Shadow = true },
            });
        }

        return applied;
    }

    /// <summary>Whether <paramref name="id"/> is one of the rules the mode owns.</summary>
    public static bool IsModelTargetingRule(string id) =>
        string.Equals(id, DifficultyRuleId, StringComparison.Ordinal)
        || string.Equals(id, LadderRuleId, StringComparison.Ordinal);
}
