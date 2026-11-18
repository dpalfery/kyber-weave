using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IGitRepositoryFacts"/>: answers every fact from what the test declares, touching no repository.</summary>
/// <remarks>
/// The default is a directory outside any repository, which the seam already promises as an absence rather than a
/// throw, so a caller under test reads the same shape it reads from git and no test starts a process.
/// </remarks>
internal sealed class FakeGitRepositoryFacts : IGitRepositoryFacts
{
    /// <summary>The worktrees each working directory reports, by working directory; empty when not named.</summary>
    public Dictionary<string, IReadOnlyList<string>> Worktrees { get; init; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? GetRoot(string workingDirectory, string gitExecutable = "git") => null;

    /// <inheritdoc />
    public IReadOnlyList<string> ListWorktrees(string workingDirectory, string gitExecutable = "git") =>
        Worktrees.TryGetValue(workingDirectory, out IReadOnlyList<string>? worktrees) ? worktrees : [];

    /// <inheritdoc />
    public string? GetCommonDirectory(string workingDirectory, string gitExecutable = "git") => null;

    /// <inheritdoc />
    public string? GetCurrentBranch(string workingDirectory, string gitExecutable = "git") => null;

    /// <inheritdoc />
    public bool RefExists(string workingDirectory, string revision, string gitExecutable = "git") => false;

    /// <inheritdoc />
    public string? GetHead(string workingDirectory, string gitExecutable = "git") => null;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetDirtyStatuses(string workingDirectory, string gitExecutable = "git") =>
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetBlobIds(
        string workingDirectory,
        IEnumerable<string> paths,
        string gitExecutable = "git") => new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlySet<string> GetChangedPathsSinceBase(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git") => new HashSet<string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<HunkRange> GetHunkRanges(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git") => [];

    /// <inheritdoc />
    public IReadOnlyList<ChangedHunk>? GetChangedHunks(
        string workingDirectory,
        string baseRef,
        string gitExecutable = "git") => null;

    /// <inheritdoc />
    public ChangeSet? GetChangeSet(string workingDirectory, string baseRef, string gitExecutable = "git") => null;

    /// <inheritdoc />
    public string? GetDiffSummary(
        string workingDirectory,
        string baseRef,
        IReadOnlyCollection<string> changedPaths,
        string gitExecutable = "git") => null;

    /// <inheritdoc />
    public GitSnapshot? CaptureSnapshot(string workingDirectory, string gitExecutable = "git") => null;

    /// <inheritdoc />
    public IReadOnlySet<string> GetChangedPathsSinceSnapshot(
        string workingDirectory,
        GitSnapshot snapshot,
        string gitExecutable = "git") => new HashSet<string>(StringComparer.Ordinal);
}
