using System.ComponentModel;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>Writes the diff and the evidence file a council's gate run starts a review cycle from.</summary>
/// <param name="git">Names the change and produces its diff.</param>
/// <param name="files">Writes the files into the cycle folder.</param>
/// <param name="paths">Resolves the gate report's path through the path seam.</param>
/// <param name="environment">Names the working directory a relative gate-report path resolves against.</param>
public sealed class ReviewEvidenceService(
    IGitChangeReader git,
    IReviewEvidenceStore files,
    IPathResolver paths,
    IProcessEnvironment environment) : IReviewEvidenceService
{
    /// <summary>
    /// Writes the diff and the evidence file into <paramref name="cycleFolder"/>. When git cannot name the change, the
    /// evidence file still lists the gates and says why the diff is missing.
    /// </summary>
    /// <param name="cycleFolder">The cycle folder the files are written to.</param>
    /// <param name="tree">The work tree under review.</param>
    /// <param name="baseRef">The ref the change is measured from.</param>
    /// <param name="gates">The complete gate report, carried gates included, or null while the gates are still running.</param>
    /// <param name="gatesPath">Where the gate report is written.</param>
    /// <param name="priorHead">
    ///     In a verifier cycle, the head the prior cycle reviewed: the diff is then the fix delta from it, while the
    ///     changed files stay the whole change's.
    /// </param>
    /// <param name="coverageFloor">The host's <c>review.coverage</c> floor, so no seat has to look it up.</param>
    /// <returns>The evidence file's absolute path.</returns>
    /// <exception cref="IOException">A file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">A file could not be written.</exception>
    public string Write(
        string cycleFolder,
        string tree,
        string baseRef,
        GateReport? gates,
        string gatesPath,
        string? priorHead = null,
        ReviewCoverage? coverageFloor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatesPath);

        files.EnsureFolder(cycleFolder);
        string diffPath = Path.Combine(cycleFolder, ReviewEvidence.DiffFileName);
        ReviewChange change;
        try
        {
            (string mergeBase, string patch) = git.DiffSinceMergeBase(tree, baseRef);
            string? deltaSince = FixDelta(tree, priorHead, ref patch);
            files.WriteAtomically(diffPath, patch);
            change = new ReviewChange(
                mergeBase,
                git.HeadCommit(tree),
                git.ResolveSinceMergeBase(tree, baseRef),
                null,
                deltaSince)
            {
                DiffLines = ReviewEvidence.DiffLineRanges(patch)
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            change = new ReviewChange(null, null, [], ex.Message.Trim());
        }

        string path = Path.Combine(cycleFolder, ReviewEvidence.FileName);
        files.WriteAtomically(
            path,
            ReviewEvidence.Format(
                tree,
                baseRef,
                change,
                gates,
                diffPath,
                paths.GetFullPath(gatesPath, environment.CurrentDirectory),
                coverageFloor));
        return path;
    }

    /// <summary>
    ///     Replaces <paramref name="patch"/> with the fix delta from <paramref name="priorHead"/>. A prior head git no
    ///     longer knows, as after a rebase, keeps the whole change's diff.
    /// </summary>
    /// <returns>The prior head the delta starts from, or null when the patch is the whole change.</returns>
    private string? FixDelta(string tree, string? priorHead, ref string patch)
    {
        if (string.IsNullOrWhiteSpace(priorHead))
            return null;

        try
        {
            patch = git.DiffSince(tree, priorHead);
            return priorHead;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

}
