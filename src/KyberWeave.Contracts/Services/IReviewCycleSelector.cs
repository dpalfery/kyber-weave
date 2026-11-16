using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// Finds the review folder and the recorded findings a command works from: choosing the task folder, listing what
/// changed since a commit, and reading the prior cycle's findings to re-check.
/// </summary>
public interface IReviewCycleSelector
{
    /// <summary>
    /// Chooses the task folder: the given id normalised and matched to an existing folder without regard to case; with no
    /// id, the one review folder there is, or the one named for the current branch.
    /// </summary>
    /// <param name="tree">The git work tree the review belongs to.</param>
    /// <param name="scratchpad">The <c>.agent_scratchpad</c> folder.</param>
    /// <param name="given">The id the caller gave, or null.</param>
    ReviewTaskChoice ChooseTask(string tree, string scratchpad, string? given);

    /// <summary>The paths the working tree changed since <paramref name="commit"/>, or null when git cannot list them.</summary>
    /// <param name="tree">The git work tree the review belongs to.</param>
    /// <param name="commit">The commit to compare the working tree with.</param>
    IReadOnlyList<string>? ChangedSince(string tree, string commit);

    /// <summary>
    /// The prior cycle's findings the fix round took, which this cycle re-checks: every finding at or above
    /// <c>review.policy.fix-level</c>.
    /// </summary>
    /// <param name="prior">The prior cycle whose findings are read.</param>
    /// <param name="policy">The host's review policy, which decides which findings a fix round takes.</param>
    /// <param name="report">Receives a finding when the findings cannot be read.</param>
    /// <param name="unreadableCode">The rule id the unreadable report carries.</param>
    /// <param name="unreadableSeverity">The severity that report carries.</param>
    /// <param name="subject">The command name the report names.</param>
    (bool Ok, IReadOnlyList<ReviewFinding> Recheck) ReadRecheck(
        ReviewCycleRecord prior,
        ReviewPolicy policy,
        DiagnosticReport report,
        string unreadableCode,
        Severity unreadableSeverity,
        string subject);
}
