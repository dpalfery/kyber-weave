using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IRunStateStore"/>: run records and bases a test declares, kept in memory.</summary>
/// <remarks>
/// The default answers as an operator with no runs directory: no record, and no live run. A test sets
/// <see cref="Records"/> to the records <c>run start</c> would have written.
/// </remarks>
internal sealed class FakeRunStateStore : IRunStateStore
{
    /// <summary>The records every read returns; null when the runs directory cannot be read.</summary>
    public IReadOnlyList<RunRecord>? Records
    {
        get => _records;
        set
        {
            _records = value;
            // A record `run start` wrote names a worktree it created with its own branch checked out, so a record a
            // test declares is live unless the test scripts the worktree as reused, missing or elsewhere — after the
            // declaration, because declaring the records again is what a later `run start` does to the fake.
            foreach (RunRecord record in value ?? [])
            {
                CheckedOutBranches[record.Worktree] = record.Branch;
            }
        }
    }

    private IReadOnlyList<RunRecord>? _records = [];

    /// <summary>The base recorded for each branch.</summary>
    public Dictionary<string, string> Bases { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The branch each worktree has checked out; a worktree absent here is not on disk. Seeded from
    /// <see cref="Records"/>, so a record a test declares is live until the test says otherwise; script a reused or
    /// missing worktree after declaring the records, since declaring them again re-seeds every worktree they name.
    /// </summary>
    public Dictionary<string, string?> CheckedOutBranches { get; } = new(StringComparer.Ordinal);

    /// <summary>Scripts a worktree that outlived its run: another branch is checked out there.</summary>
    /// <param name="worktree">The worktree the record names.</param>
    /// <param name="branch">The branch now checked out there.</param>
    public void MarkWorktreeReused(string worktree, string branch) => CheckedOutBranches[worktree] = branch;

    /// <summary>Scripts a worktree that is no longer on disk.</summary>
    /// <param name="worktree">The worktree the record names.</param>
    public void RemoveWorktree(string worktree) => CheckedOutBranches.Remove(worktree);

    /// <inheritdoc />
    public void Write(string homeDirectory, string branch, string baseRef, RunStartPlace? place = null)
    {
        Bases[branch] = baseRef;
        if (place is not null)
        {
            Records = [.. Records ?? [], new RunRecord(place.Repository, branch, place.Worktree, place.StartedAt)];
        }
    }

    /// <inheritdoc />
    public bool TryReadBaseRef(string homeDirectory, string branch, out string? baseRef)
    {
        if (Bases.TryGetValue(branch, out string? recorded))
        {
            baseRef = recorded;
            return true;
        }

        baseRef = null;
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Mirrors <c>FileRunStateStore.HasLiveRun</c>: a record is a live run only while its worktree still exists and
    /// has the record's branch checked out. Repository match alone would let a reused worktree pass here while
    /// production refuses it, and a guard test over this fake would then miss exactly the stale-run case.
    /// </remarks>
    public bool? HasLiveRun(string homeDirectory, string repository)
    {
        if (Records is null)
        {
            return null;
        }

        return Records.Any(record =>
            string.Equals(record.Repository, repository, StringComparison.Ordinal)
            && CheckedOutBranches.TryGetValue(record.Worktree, out string? branch)
            && string.Equals(branch, record.Branch, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public IReadOnlyList<RunRecord>? ReadRecords(string homeDirectory) => Records;
}
