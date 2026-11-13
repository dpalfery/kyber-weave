using System.ComponentModel;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Core.Review;

/// <summary>
/// Where a review's state lives and which change it belongs to, from machine facts: the git work tree that contains
/// the path a review command was run against, and the branch, base and head of the change checked out there.
/// </summary>
public static class ReviewWorkTree
{
    /// <summary>The ref the change's base is resolved against when none is named.</summary>
    public const string DefaultBase = "origin/HEAD";

    private static readonly string[] FallbackBases = ["origin/main", "origin/master", "main", "master"];

    private const string GitEntry = ".git";
    private const string ScratchpadFolder = ".agent_scratchpad";

    /// <summary>
    /// The git work tree that contains <paramref name="path"/>: the nearest ancestor holding a <c>.git</c> entry, a
    /// directory for a main checkout and a file for a linked worktree. A path in no work tree answers itself.
    /// </summary>
    public static string Of(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);
        for (DirectoryInfo? directory = new(full); directory is not null; directory = directory.Parent)
        {
            string entry = Path.Combine(directory.FullName, GitEntry);
            if (Directory.Exists(entry) || File.Exists(entry))
                return directory.FullName;
        }

        return full;
    }

    /// <summary>The <c>.agent_scratchpad</c> folder of <paramref name="workTree"/>.</summary>
    public static string Scratchpad(string workTree) => Path.Combine(workTree, ScratchpadFolder);

    /// <summary>The branch, base and head of the change checked out in <paramref name="workTree"/>; null when git cannot name them.</summary>
    /// <remarks>
    /// The base is the merge-base commit of the base ref and <c>HEAD</c>, so it names where the change forked and stays
    /// put as the branch grows. Without a named ref, <see cref="DefaultBase"/> is tried and then the usual default
    /// branches, because a repository with no <c>origin/HEAD</c> would otherwise never recognise a prior cycle.
    /// </remarks>
    /// <param name="workTree">The work tree.</param>
    /// <param name="baseRef">The ref the base is resolved against; the defaults are tried when null.</param>
    public static ReviewCycleIdentity? IdentityOf(string workTree, string? baseRef)
    {
        try
        {
            string branch = ChangedFiles.CurrentBranch(workTree);
            string head = ChangedFiles.HeadCommit(workTree);
            string[] candidates = string.IsNullOrWhiteSpace(baseRef) ? [DefaultBase, .. FallbackBases] : [baseRef.Trim()];
            foreach (string candidate in candidates)
            {
                string mergeBase;
                try
                {
                    mergeBase = ChangedFiles.MergeBaseCommit(workTree, candidate);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (branch.Length > 0 && mergeBase.Length > 0 && head.Length > 0)
                    return new ReviewCycleIdentity(branch, mergeBase, head);
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Splits the finished cycles of <paramref name="workTree"/> into those that belong to its change and those that do not.</summary>
    public static PriorCycles Prior(string workTree, string? baseRef, IReadOnlyList<ReviewCycleRecord> finished)
    {
        ArgumentNullException.ThrowIfNull(finished);
        return ReviewCycles.Belonging(
            finished,
            IdentityOf(workTree, baseRef),
            commit => ChangedFiles.IsAncestorOfHead(workTree, commit));
    }

    /// <summary>
    /// One plain line when <paramref name="workTree"/> is a repository's canonical checkout while a live run of it works
    /// in a worktree of its own: it names the worktree the review belongs in. Null in a linked worktree, when no such run
    /// is live, or when the run records cannot be read.
    /// </summary>
    /// <param name="workTree">The work tree the command was run against.</param>
    /// <param name="homeDirectory">The operator's home, which holds the run records.</param>
    public static string? LiveRunNote(string workTree, string homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory) || !Directory.Exists(Path.Combine(workTree, GitEntry)))
            return null;

        IReadOnlyList<RunRecord>? runs = RunCheckout.LiveSeparateRuns(homeDirectory, workTree);
        if (runs is not { Count: > 0 })
            return null;

        string where = string.Join(
            " or ",
            runs.Select(run => $"{run.Worktree} (branch {run.Branch}; review folder {Scratchpad(run.Worktree)})"));
        return $"worktree: using {workTree}, the canonical checkout, but a live run works in {where}; run the review there.";
    }

    /// <summary>The home directory the run records are read from.</summary>
    public static string HomeDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
