using KyberWeave.Core.Arbiter;

namespace KyberWeave.Core.Arbiter.Rules;

/// <summary>
/// Step-0 evaluation: closed predicates, first-match <c>decide</c> clauses,
/// family combination, and the step-1 short-circuit signal.
/// </summary>
/// <remarks>
/// Pure and harness-neutral: no clock, no filesystem, no model. Step 1 can add
/// a red flag but can never overturn a step-0 result — once step 0 produces
/// the family's strongest effect the caller skips step 1 outright.
/// </remarks>
public static class RuleEngine
{
    /// <summary>The answer when no <c>decide</c> clause matches.</summary>
    public const string Undecidable = "undecidable";

    /// <summary>
    /// Evaluates one rule's step-0 clauses: the first matching clause answers,
    /// no match (or a disabled rule) answers undecidable.
    /// </summary>
    public static string Decide(ArbiterRule rule, ArbiterFactSet facts)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(facts);
        if (!rule.Enabled || rule.Decide is null)
        {
            return Undecidable;
        }

        foreach (RuleDecideClause clause in rule.Decide)
        {
            if (clause.When.Evaluate(facts))
            {
                return clause.Answer;
            }
        }

        return Undecidable;
    }

    /// <summary>
    /// Evaluates every enabled rule for <paramref name="trigger"/>, maps each
    /// answer to its effect (undecidable via the family), combines them, and
    /// reports whether step 1 can be skipped.
    /// </summary>
    public static ArbiterOutcome EvaluateStep0(
        IReadOnlyList<ArbiterRule> rules,
        ArbiterFactSet facts,
        string trigger)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(trigger);
        ArbiterTriggerFamily family = ArbiterTriggerFamilies.ForTrigger(trigger);
        List<ArbiterRuleAnswer> answers = new(rules.Count);
        List<string> effects = new(rules.Count);
        foreach (ArbiterRule rule in rules)
        {
            if (!rule.Enabled || !string.Equals(rule.Trigger, trigger, StringComparison.Ordinal))
            {
                continue;
            }

            string answer = Decide(rule, facts);
            string effect = MapAnswerToEffect(rule, answer, family);
            answers.Add(new ArbiterRuleAnswer(rule.Id, answer, 0, effect));
            effects.Add(effect);
        }

        string combined = effects.Count == 0
            ? ArbiterTriggerFamilies.UndecidableEffect(family)
            : ArbiterTriggerFamilies.Combine(family, effects);
        return new ArbiterOutcome(
            trigger,
            family,
            answers,
            combined,
            ArbiterTriggerFamilies.IsStrongestEffect(combined));
    }

    private static string MapAnswerToEffect(ArbiterRule rule, string answer, ArbiterTriggerFamily family)
    {
        if (string.Equals(answer, Undecidable, StringComparison.Ordinal))
        {
            return ArbiterTriggerFamilies.UndecidableEffect(family);
        }

        if (rule.Effects.TryGetValue(answer, out string? effect))
        {
            return effect;
        }

        return ArbiterTriggerFamilies.UndecidableEffect(family);
    }
}
