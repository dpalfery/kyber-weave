using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.RunStart;

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
/// is the review's change against the run's recorded base — the committed diff
/// plus the dirty set — which backs <c>review.changed-paths</c> on the review
/// triggers that declare it. The run's base is what <c>run start</c> branched from
/// and recorded per branch; only a branch with no recorded run falls back to
/// <see cref="ReviewBase" />. The basis is recorded beside the set as
/// <c>review.base-ref</c>, so a fallback is inspectable rather than silent. The dirty
/// set alone is not that set: a per-task
/// commit leaves a clean tree, and an empty changed set used to skip every lens
/// whose applicability it was meant to answer. Every probe goes through
/// <see cref="GitFacts"/>, which answers absent outside a repository or without
/// git on <c>PATH</c> and never throws.
/// </remarks>
internal sealed class HookGitFacts(string workingDirectory, string? homeDirectory = null) : IArbiterGitFacts
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
            string baseRef = ResolveBase(cwd);
            IReadOnlyList<string> changed = [.. GitFacts.GetChangedPathsSinceBase(cwd, baseRef)];
            facts = facts.With("review.changed-paths", changed, ArbiterFactLabel.Derived);
            facts = facts.With("review.base-ref", baseRef, ArbiterFactLabel.Derived);
        }

        return facts;
    }

    /// <summary>
    /// The base the review is measured from: the base the run recorded for the checkout's
    /// branch when that base still resolves, or the default review base otherwise. A stale
    /// or bogus record falls back rather than measuring against a ref that is gone.
    /// </summary>
    private string ResolveBase(string workingDirectory)
    {
        string home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? branch = GitFacts.GetCurrentBranch(workingDirectory);
        if (branch is not null &&
            RunBaseState.TryReadBaseRef(home, branch, out string? recorded) &&
            !string.IsNullOrWhiteSpace(recorded) &&
            GitFacts.RefExists(workingDirectory, recorded))
        {
            return recorded;
        }

        return ReviewBase;
    }
}
