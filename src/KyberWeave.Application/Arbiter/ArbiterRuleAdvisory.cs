using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter;

/// <summary>
/// Decides whether a rule can gate at all. A rule is <b>advisory</b> when every answer it can give maps to its
/// trigger family's proceed effect — <c>allow</c>, <c>keep</c>, <c>spawn</c> or <c>applies</c> — so no answer can
/// refuse, skip or retarget anything.
/// </summary>
/// <remarks>
/// The property is computed from the rule's own effects rather than declared, so a host that changes an effect makes
/// a rule gate without a second place to edit. <c>arbiter rules</c> lists it. Marking changes no effect: an advisory
/// rule is still evaluated, logged and recorded in the decision log.
/// </remarks>
public static class ArbiterRuleAdvisory
{
    /// <summary>Whether every answer of <paramref name="rule"/> proceeds, so the rule can not gate.</summary>
    /// <param name="rule">The rule to read.</param>
    public static bool IsAdvisory(ArbiterRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.Effects.Count == 0)
        {
            return false;
        }

        ArbiterTriggerFamily family;
        try
        {
            family = ArbiterTriggerFamilies.ForTrigger(rule.Trigger);
        }
        catch (ArgumentException)
        {
            return false;
        }

        string proceed = ArbiterTriggerFamilies.NeutralEffect(family);
        return rule.Effects.Values.All(effect => string.Equals(effect, proceed, StringComparison.Ordinal));
    }
}
