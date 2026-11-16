namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>A new-side hunk range from a unified diff.</summary>
/// <param name="Path">Repository-relative path of the file.</param>
/// <param name="StartLine">One-based first new-side line.</param>
/// <param name="LineCount">Number of new-side lines in the hunk.</param>
public sealed record HunkRange(string Path, int StartLine, int LineCount);

/// <summary>One hunk of a change, with the old and new ranges git reports for it.</summary>
/// <remarks>
/// Ranges are the zero-context ones git prints. A zero count marks the side that has no lines:
/// a pure insertion has <c>OldCount</c> 0, a pure deletion has <c>Count</c> 0. A whole new file
/// is one hunk with <c>OldStart</c> and <c>OldCount</c> 0.
/// </remarks>
/// <param name="Path">Repository-relative path of the file, under its new name.</param>
/// <param name="OldStart">One-based first old-side line.</param>
/// <param name="OldCount">Number of old-side lines in the hunk.</param>
/// <param name="Start">One-based first new-side line.</param>
/// <param name="Count">Number of new-side lines in the hunk.</param>
public sealed record ChangedHunk(string Path, int OldStart, int OldCount, int Start, int Count);

/// <summary>A changed hunk together with the content of the lines it adds and removes.</summary>
/// <param name="Hunk">The hunk's ranges.</param>
/// <param name="ChangedLines">The line contents the hunk really changes, without the diff's <c>+</c> or <c>-</c> prefix:
/// added and removed lines, net of any removed line git printed back unchanged.</param>
public sealed record MeasuredHunk(ChangedHunk Hunk, IReadOnlyList<string> ChangedLines);

/// <summary>One measurement of a change against a base: its hunks and the paths renamed without a content change.</summary>
/// <param name="Hunks">Every hunk, ordered by path; the order within one path is git's.</param>
/// <param name="RenameOnlyPaths">New paths of renames at full similarity. They have no hunk.</param>
public sealed record ChangeSet(IReadOnlyList<MeasuredHunk> Hunks, IReadOnlySet<string> RenameOnlyPaths);

/// <summary>The dirty-state snapshot the ledger stores at pre-dispatch.</summary>
/// <param name="Head">The commit the snapshot was taken at.</param>
/// <param name="BlobIds">Blob id per dirty or untracked path.</param>
/// <param name="Statuses">Porcelain status per dirty or untracked path.</param>
public sealed record GitSnapshot(
    string Head,
    IReadOnlyDictionary<string, string> BlobIds,
    IReadOnlyDictionary<string, string> Statuses);
