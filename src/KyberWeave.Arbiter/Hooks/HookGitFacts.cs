using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The hook's git facts: the fact bag already carries every fact the trigger
/// declares (absent as null), so this enriches only what a hook process can state
/// truthfully from a live working tree.
/// </summary>
/// <remarks>
/// The hook stores no snapshot with its pre-dispatch ledger event, so a
/// since-dispatch changed set is unknowable here and stays absent: absent facts
/// satisfy <c>exists: false</c> rather than failing the dispatch. What is known
/// is the review's change against <see cref="ReviewBase" /> — the committed diff
/// plus the dirty set — which backs <c>review.changed-paths</c> on the review
/// triggers that declare it. The dirty set alone is not that set: a per-task
/// commit leaves a clean tree, and an empty changed set used to skip every lens
/// whose applicability it was meant to answer. Every probe goes through
/// <see cref="GitFacts"/>, which answers absent outside a repository or without
/// git on <c>PATH</c> and never throws.
/// </remarks>
internal sealed class HookGitFacts(string workingDirectory) : IArbiterGitFacts
{
    /// <summary>
    /// The ref the review's changed set is measured from: <c>origin/HEAD</c>, the
    /// default <c>review gates</c> diffs against, so the lenses judge the same change
    /// the council does. The hook event carries no base of its own.
    /// </summary>
    private const string ReviewBase = "origin/HEAD";

    /// <inheritdoc/>
    public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(ev);

        if (facts.Contains("review.changed-paths"))
        {
            string cwd = !string.IsNullOrWhiteSpace(ev.Cwd) ? ev.Cwd : workingDirectory;
            IReadOnlyList<string> changed = [.. GitFacts.GetChangedPathsSinceBase(cwd, ReviewBase)];
            facts = facts.With("review.changed-paths", changed, ArbiterFactLabel.Derived);
        }

        return facts;
    }
}
