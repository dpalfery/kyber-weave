using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.Arbiter;

/// <summary>
/// Derives the secondary triggers a primary dispatch carries. A secondary trigger is evaluated
/// behind its primary: it never gates the dispatch on its own, and its outcome is recorded beside
/// the primary's on a record that shares the primary's ledger id.
/// </summary>
/// <remarks>
/// A secondary is derived from its primary and the event alone, never from a rule's own
/// eligibility list: <c>delegate.model</c> rides a <c>delegate</c> dispatch to an implementation
/// class, and no other dispatch carries it. The derivation is a plain function so the evaluator can
/// take a test double in its place without any host configuration.
/// </remarks>
internal static class SecondaryTriggers
{
    private const string Delegate = "delegate";
    private const string DelegateModel = "delegate.model";
    private const string ImplementationClass = "implementation";

    /// <summary>Returns the secondary triggers for <paramref name="primaryTrigger"/>, in evaluation order.</summary>
    /// <param name="primaryTrigger">The primary trigger the dispatch classified as.</param>
    /// <param name="ev">The event the dispatch arrived in.</param>
    public static IReadOnlyList<string> For(string primaryTrigger, ArbiterEvent ev)
    {
        ArgumentNullException.ThrowIfNull(primaryTrigger);
        ArgumentNullException.ThrowIfNull(ev);

        if (string.Equals(primaryTrigger, Delegate, StringComparison.Ordinal)
            && string.Equals(TriggerClassifier.TargetClassFor(ev.Target), ImplementationClass, StringComparison.Ordinal))
        {
            return [DelegateModel];
        }

        return [];
    }
}
