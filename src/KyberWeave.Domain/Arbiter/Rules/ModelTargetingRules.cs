using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Arbiter.Rules;

/// <summary>
/// Maps <c>arbiter.model-targeting.mode</c> onto the two shipped model-targeting rules, whose enabled
/// and shadow state the mode owns. A host cannot set shadow on them directly; the mode is the one switch.
/// </summary>
/// <remarks>
/// The mode mapping runs in two places: when the configuration is loaded, so the rules a host reads are the
/// rules that will run, and when an evaluation starts, so a configuration built in code with a mode
/// the shipped rules were not loaded under still behaves as its mode says. The roster mapping runs where the
/// roster is known, at evaluation, because a loaded configuration does not carry the run's roster.
/// <para>
/// Under the tiered roster the two rules follow the three developer tiers. The difficulty question is
/// answered from the rating the task file records instead of a second model call, because that one rating
/// already chose the agent and the agent already chose the model (Requirement 2.10). The ladder climbs
/// the tiers' model profiles, so a dispatch the conductor already put on the next tier targets the token it
/// is already rendered with and nothing is written (Requirement 3.4).
/// </para>
/// </remarks>
internal static class ModelTargetingRules
{
    /// <summary>The shipped rule that picks a difficulty's profile.</summary>
    public const string DifficultyRuleId = "KW-ARB-DIFFICULTY-001";

    /// <summary>The shipped rule that climbs the ladder after a failed attempt.</summary>
    public const string LadderRuleId = "KW-ARB-LADDER-001";

    /// <summary>The fact carrying the difficulty the task file records for the dispatched task.</summary>
    public const string RecordedDifficultyFact = "plan.task.difficulty";

    /// <summary>The answer the recorded <c>medium</c> rating takes; the shipped question spells it <c>standard</c>.</summary>
    private const string StandardAnswer = "standard";

    private const string Off = "off";
    private const string Enforce = "enforce";

    // Each developer tier's model profile, from products/kyber-squad/profiles/models.yml: dev-easy runs on
    // `fast`, dev-medium on `general`, and dev-hard on the `dev-hard` profile of its own (Requirement 1.12).
    private const string EasyProfile = "fast";
    private const string MediumProfile = "general";
    private const string HardProfile = "dev-hard";

    /// <summary>The tiered roster's ladder, cheapest first: the three developer tiers' model profiles.</summary>
    public static IReadOnlyList<string> TierLadder { get; } = [EasyProfile, MediumProfile, HardProfile];

    /// <summary>Returns <paramref name="rules"/> with the model-targeting rules set for <paramref name="mode"/>.</summary>
    /// <param name="rules">The effective rule set.</param>
    /// <param name="mode"><c>off</c> disables both rules, <c>shadow</c> records them without effect, and <c>enforce</c> lets them gate.
    /// Any other mode is treated as shadow, so an unrecognised mode never gates a dispatch.</param>
    /// <param name="roster">
    /// The run's worker roster. Under <c>tiered</c> the difficulty rule answers from the recorded rating and makes
    /// no second model call; under <c>technology</c>, and when no roster is given, both rules are as they ship.
    /// </param>
    public static IReadOnlyList<ArbiterRule> Apply(
        IReadOnlyList<ArbiterRule> rules,
        string mode,
        string? roster = WorkerRosters.Technology)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(mode);

        bool tiered = string.Equals(roster, WorkerRosters.Tiered, StringComparison.Ordinal);
        List<ArbiterRule> applied = new(rules.Count);
        foreach (ArbiterRule rule in rules)
        {
            if (!IsModelTargetingRule(rule.Id))
            {
                applied.Add(rule);
                continue;
            }

            ArbiterRule targeted = tiered && string.Equals(rule.Id, DifficultyRuleId, StringComparison.Ordinal)
                ? FromRecordedDifficulty(rule)
                : rule;
            applied.Add(mode switch
            {
                Off => targeted with { Enabled = false },
                Enforce => targeted with { Shadow = false },
                _ => targeted with { Shadow = true },
            });
        }

        return applied;
    }

    /// <summary>
    /// The model-targeting block in use: under the tiered roster its difficulty map and ladder name the three
    /// developer tiers' profiles, and they do so whether or not the host set its own. Under the technology
    /// roster, and when no roster is given, the host's block is returned unchanged.
    /// </summary>
    /// <param name="targeting">The host's model-targeting block.</param>
    /// <param name="roster">The run's worker roster.</param>
    public static ArbiterModelTargetingConfig Targeting(ArbiterModelTargetingConfig targeting, string? roster)
    {
        ArgumentNullException.ThrowIfNull(targeting);
        if (!string.Equals(roster, WorkerRosters.Tiered, StringComparison.Ordinal))
        {
            return targeting;
        }

        return targeting with
        {
            DifficultyProfiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["easy"] = EasyProfile,
                [StandardAnswer] = MediumProfile,
                ["hard"] = HardProfile,
            },
            Ladder = TierLadder,
        };
    }

    /// <summary>Whether <paramref name="id"/> is one of the rules the mode owns.</summary>
    public static bool IsModelTargetingRule(string id) =>
        string.Equals(id, DifficultyRuleId, StringComparison.Ordinal)
        || string.Equals(id, LadderRuleId, StringComparison.Ordinal);

    /// <summary>
    /// The difficulty rule answered from the rating the task file records, with no question to ask. A task the
    /// pick already rated is never rated again, so the agent and the model cannot disagree; a task with no
    /// recorded rating answers undecidable and keeps, and no call is made for it either.
    /// </summary>
    private static ArbiterRule FromRecordedDifficulty(ArbiterRule rule) => rule with
    {
        Decide =
        [
            new RuleDecideClause(RulePredicate.Equals(RecordedDifficultyFact, RuleOperand.Literal("easy")), "easy"),
            new RuleDecideClause(RulePredicate.Equals(RecordedDifficultyFact, RuleOperand.Literal("medium")), StandardAnswer),
            new RuleDecideClause(RulePredicate.Equals(RecordedDifficultyFact, RuleOperand.Literal("hard")), "hard"),
        ],
        Ask = null,
    };
}
