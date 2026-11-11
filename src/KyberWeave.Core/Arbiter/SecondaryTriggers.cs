using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;

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
    private const string Investigate = "investigate";
    private const string DelegatePlanner = "delegate.planner";
    private const string TriageShortcut = "triage.shortcut";
    private const string IntakeShortcut = "intake.shortcut";
    private const string DocsImpactShortcut = "docs-impact.shortcut";
    private const string BugCrusher = "bug-crusher";
    private const string BugCrusherInvestigator = "bug-crusher-investigator";
    private const string DocsDev = "docs-dev";
    private const string IntakeMarker = "INTAKE";
    private const string DocsImpactMarker = "DOCS-IMPACT: true";

    /// <summary>Returns the secondary triggers for <paramref name="primaryTrigger"/>, in evaluation order.</summary>
    /// <param name="primaryTrigger">The primary trigger the dispatch classified as.</param>
    /// <param name="ev">The event the dispatch arrived in.</param>
    public static IReadOnlyList<string> For(string primaryTrigger, ArbiterEvent ev)
    {
        ArgumentNullException.ThrowIfNull(primaryTrigger);
        ArgumentNullException.ThrowIfNull(ev);

        List<string> triggers = [];
        if (string.Equals(primaryTrigger, Delegate, StringComparison.Ordinal)
            && string.Equals(TriggerClassifier.TargetClassFor(ev.Target), ImplementationClass, StringComparison.Ordinal))
        {
            triggers.Add(DelegateModel);
        }

        if (string.Equals(primaryTrigger, Investigate, StringComparison.Ordinal)
            && string.Equals(ev.HarnessCaller ?? ev.Caller, BugCrusher, StringComparison.Ordinal)
            && string.Equals(ev.Target, BugCrusherInvestigator, StringComparison.Ordinal))
        {
            triggers.Add(TriageShortcut);
        }

        if (string.Equals(primaryTrigger, DelegatePlanner, StringComparison.Ordinal)
            && string.Equals(TriggerClassifier.PlannerMarkerFor(ev.Prompt, HeaderBlock.Parse(ev.Prompt).Headers), IntakeMarker, StringComparison.Ordinal))
        {
            triggers.Add(IntakeShortcut);
        }

        if (string.Equals(primaryTrigger, Delegate, StringComparison.Ordinal)
            && string.Equals(ev.Target, DocsDev, StringComparison.Ordinal)
            && IsDocsImpactDispatch(ev.Prompt))
        {
            triggers.Add(DocsImpactShortcut);
        }

        return triggers;
    }

    /// <summary>
    /// Whether the dispatch body's first line is the docs-impact marker. The marker counts only as the first
    /// body line, so a later line with the same text does not derive the shortcut.
    /// </summary>
    private static bool IsDocsImpactDispatch(string prompt)
    {
        string? first = HeaderBlock.Strip(prompt)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
        return string.Equals(first, DocsImpactMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// The effect a secondary rule contributes while it cannot gate: a shadow rule, or a rule whose
    /// primary did not allow. Every secondary family shares the neutral <c>keep</c>.
    /// </summary>
    public static string NeutralEffect() => RuleEffects.Keep;
}
