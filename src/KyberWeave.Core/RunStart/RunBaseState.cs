using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.RunStart;

namespace KyberWeave.Core.RunStart;

/// <summary>
/// The base a run branched from, kept where a later dispatch hook can read it: a
/// branch-keyed file under the operator's home. A hook process sees a prompt and a
/// working directory but no PR metadata, so without this record its only base is the
/// <c>origin/HEAD</c> guess — wrong on a stacked branch, silent when unresolvable.
/// </summary>
/// <remarks>
/// One file per branch, refreshed by every <c>run start</c>: a rebranched branch never
/// reads a stale base, and worktrees on one branch share the record because the key is
/// the branch name, not the path. The record's format is <see cref="RunBaseStateFormat" /> and its
/// storage is <see cref="FileRunStateStore" />; this static entry point stays for the callers that
/// have no container, and answers from the same store over the real file system.
/// </remarks>
public static class RunBaseState
{
    internal static IRunStateStore Store { get; } =
        new FileRunStateStore(new PhysicalFileSystem(), new GitFactsBranchReader());

    /// <summary>The state file holding <paramref name="branch" />'s recorded base.</summary>
    public static string StatePath(string homeDirectory, string branch) => RunBaseStateFormat.StatePath(homeDirectory, branch);

    /// <summary>
    /// Records <paramref name="baseRef" /> as the base <paramref name="branch" /> was
    /// created from, replacing any earlier record. <paramref name="place" />, when given,
    /// records where the run works, which the dispatch guard reads (<see cref="HasLiveRun" />).
    /// </summary>
    public static void Write(string homeDirectory, string branch, string baseRef, RunStartPlace? place = null) =>
        Store.Write(homeDirectory, branch, baseRef, place);

    /// <summary>
    /// Whether a run is live for <paramref name="repository" />: a record names that repository, and its
    /// worktree still exists with the run's branch checked out. Both halves are machine facts: the record
    /// <c>run start</c> wrote, and git's answer for the worktree.
    /// </summary>
    /// <remarks>
    /// A record written before <c>run start</c> recorded its place names no repository and is skipped: it
    /// predates this version, so it cannot be the run in progress. A run left live by an earlier session
    /// also counts, because nothing on disk ties a run to a harness session.
    /// </remarks>
    /// <param name="homeDirectory">The operator's home, which holds <c>.kyber-weave/runs</c>.</param>
    /// <param name="repository">The repository's common git directory.</param>
    /// <returns>True or false when the records were read; null when the runs directory could not be read.</returns>
    public static bool? HasLiveRun(string homeDirectory, string repository) => Store.HasLiveRun(homeDirectory, repository);

    /// <summary>
    /// Every run record that names its repository, branch and worktree. Records written before <c>run start</c>
    /// recorded its place, and records that do not parse, are skipped.
    /// </summary>
    /// <param name="homeDirectory">The operator's home, which holds <c>.kyber-weave/runs</c>.</param>
    /// <returns>The records, empty when there are none; null when the runs directory or a record could not be read.</returns>
    public static IReadOnlyList<RunRecord>? ReadRecords(string homeDirectory) => Store.ReadRecords(homeDirectory);

    /// <summary>
    /// Reads the base <paramref name="branch" /> was created from. Absent, unreadable, or
    /// corrupt state reads <c>false</c> rather than throwing: the caller falls back to its
    /// default base.
    /// </summary>
    public static bool TryReadBaseRef(string homeDirectory, string branch, out string? baseRef) =>
        Store.TryReadBaseRef(homeDirectory, branch, out baseRef);

    /// <summary>Answers the branch question with <see cref="GitFacts" />, the way the dispatch guard always has.</summary>
    private sealed class GitFactsBranchReader : ICheckedOutBranchReader
    {
        public string? Read(string workingDirectory) => GitFacts.GetCurrentBranch(workingDirectory);
    }
}
