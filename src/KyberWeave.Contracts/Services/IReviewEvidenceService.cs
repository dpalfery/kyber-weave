using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>Writes the facts a review cycle starts from: the diff, the changed files and each gate's result.</summary>
public interface IReviewEvidenceService
{
    /// <summary>Writes the diff and the evidence file into the cycle folder.</summary>
    /// <param name="cycleFolder">The cycle folder the files are written to.</param>
    /// <param name="tree">The work tree under review.</param>
    /// <param name="baseRef">The ref the change is measured from.</param>
    /// <param name="gates">The complete gate report, carried gates included, or null while the gates are still running.</param>
    /// <param name="gatesPath">Where the gate report is written.</param>
    /// <param name="priorHead">In a verifier cycle, the head the prior cycle reviewed; the diff is then the fix delta from it.</param>
    /// <param name="coverageFloor">The host's <c>review.coverage</c> floor.</param>
    /// <returns>The evidence file's absolute path.</returns>
    string Write(
        string cycleFolder,
        string tree,
        string baseRef,
        GateReport? gates,
        string gatesPath,
        string? priorHead = null,
        ReviewCoverage? coverageFloor = null);
}
