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
    public IReadOnlyList<RunRecord>? Records { get; set; } = [];

    /// <summary>The base recorded for each branch.</summary>
    public Dictionary<string, string> Bases { get; init; } = new(StringComparer.Ordinal);

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
    public bool? HasLiveRun(string homeDirectory, string repository)
    {
        if (Records is null)
        {
            return null;
        }

        return Records.Any(record => string.Equals(record.Repository, repository, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public IReadOnlyList<RunRecord>? ReadRecords(string homeDirectory) => Records;
}
