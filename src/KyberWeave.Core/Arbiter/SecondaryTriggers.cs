using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter;

/// <summary>
/// Derives the secondary triggers a primary dispatch carries. A secondary trigger is evaluated
/// behind its primary: it never gates the dispatch on its own, and its outcome is recorded beside
/// the primary's on a record that shares the primary's ledger id.
/// </summary>
/// <remarks>
/// No secondary trigger is derived yet, so every dispatch evaluates the primary alone. The
/// derivation is a plain function of the primary trigger and the event so the evaluator can take
/// a test double in its place without any host configuration.
/// </remarks>
internal static class SecondaryTriggers
{
    /// <summary>Returns the secondary triggers for <paramref name="primaryTrigger"/>, in evaluation order.</summary>
    /// <param name="primaryTrigger">The primary trigger the dispatch classified as.</param>
    /// <param name="ev">The event the dispatch arrived in.</param>
    public static IReadOnlyList<string> For(string primaryTrigger, ArbiterEvent ev)
    {
        ArgumentNullException.ThrowIfNull(primaryTrigger);
        ArgumentNullException.ThrowIfNull(ev);
        return [];
    }

    /// <summary>
    /// The effect a secondary rule contributes while it cannot gate: a shadow rule, or a rule whose
    /// primary did not allow. Every secondary family shares the neutral <c>keep</c>.
    /// </summary>
    public static string NeutralEffect() => RuleEffects.Keep;
}
