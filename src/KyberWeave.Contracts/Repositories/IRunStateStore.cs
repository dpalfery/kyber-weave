using KyberWeave.Core.RunStart;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Keeps the base each run branched from and where it works, where a later dispatch hook can read it.</summary>
/// <remarks>
/// A hook process sees a prompt and a working directory but no pull request metadata, so without this record its only
/// base is the <c>origin/HEAD</c> guess: wrong on a stacked branch, silent when unresolvable.
/// </remarks>
public interface IRunStateStore
{
    /// <summary>Records the base a branch was created from, replacing any earlier record.</summary>
    /// <param name="homeDirectory">The operator's home, which holds <c>.kyber-weave/runs</c>.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="baseRef">The base it was created from.</param>
    /// <param name="place">Where the run works, which the dispatch guard reads; null records the base only.</param>
    void Write(string homeDirectory, string branch, string baseRef, RunStartPlace? place = null);

    /// <summary>Reads the base a branch was created from.</summary>
    /// <param name="homeDirectory">The operator's home.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="baseRef">The recorded base.</param>
    /// <returns>False when the record is absent, unreadable or corrupt, so the caller falls back to its default base.</returns>
    bool TryReadBaseRef(string homeDirectory, string branch, out string? baseRef);

    /// <summary>Whether a run is live for a repository: a record names it and its worktree still has the run's branch checked out.</summary>
    /// <param name="homeDirectory">The operator's home.</param>
    /// <param name="repository">The repository's common git directory.</param>
    /// <returns>True or false when the records were read; null when the runs directory could not be read.</returns>
    bool? HasLiveRun(string homeDirectory, string repository);

    /// <summary>Reads every record that names its repository, branch and worktree.</summary>
    /// <param name="homeDirectory">The operator's home.</param>
    /// <returns>The records, empty when there are none; null when the runs directory or a record could not be read.</returns>
    IReadOnlyList<RunRecord>? ReadRecords(string homeDirectory);
}
