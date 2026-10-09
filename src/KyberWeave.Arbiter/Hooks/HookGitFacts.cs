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
/// is the current dirty set, which backs <c>review.changed-paths</c> on the review
/// triggers that declare it. Every probe goes through <see cref="GitFacts"/>, which
/// answers absent outside a repository or without git on <c>PATH</c> and never throws.
/// </remarks>
internal sealed class HookGitFacts(string workingDirectory) : IArbiterGitFacts
{
    /// <inheritdoc/>
    public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(ev);

        if (facts.Contains("review.changed-paths"))
        {
            string cwd = !string.IsNullOrWhiteSpace(ev.Cwd) ? ev.Cwd : workingDirectory;
            IReadOnlyList<string> changed = [.. GitFacts.GetDirtyStatuses(cwd).Keys];
            facts = facts.With("review.changed-paths", changed, ArbiterFactLabel.Derived);
        }

        return facts;
    }
}
