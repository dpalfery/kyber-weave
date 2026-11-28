using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The git change reader over the scripted process seam: every git answer is a scripted string, so the argv each
/// read uses and the way its output becomes paths and change kinds are proven without a repository or a git install.
/// </summary>
public sealed class GitChangeReaderTests
{
    private const string Root = "/repo";

    private readonly ScriptedProcessRunner _git = new();

    /// <summary>The reader the synchronous members are proven through: the scripted seam answers their synchronous runs.</summary>
    private GitChangeReader Reader() => new(_git, _git.Run);

    /// <summary>The reader the asynchronous members are proven through: no synchronous hook, so only the injected runner can answer.</summary>
    private GitChangeReader AsyncReader() => new(_git);

    [Fact]
    public async Task ResolveAsyncCombinesChangedAndUntrackedPathsOnceInOrdinalOrderThroughTheInjectedRunner()
    {
        _ = _git
            .Script(
                "git",
                new ProcessRunResult(0, "src/b.cs\0src/a.cs\0", false),
                "diff",
                "-z",
                "--name-only",
                "--relative",
                "--diff-filter=ACMRT",
                "main",
                "--")
            .Script(
                "git",
                new ProcessRunResult(0, "src/z.cs\0src/a.cs\0", false),
                "ls-files",
                "-z",
                "--others",
                "--exclude-standard",
                "--");

        IReadOnlyList<string> paths = await AsyncReader().ResolveAsync(Root, "main", CancellationToken.None);

        Assert.Equal(["src/a.cs", "src/b.cs", "src/z.cs"], paths);
        Assert.Equal(2, _git.Calls.Count);
        Assert.All(_git.Calls, call => Assert.Equal(Root, call.WorkingDirectory));
        Assert.All(_git.Calls, call => Assert.Equal("git", call.Program));
        Assert.All(_git.Calls, call => Assert.Equal(TimeSpan.FromSeconds(60), call.Timeout));
    }

    [Fact]
    public async Task ResolveSinceMergeBaseAsyncKeepsTheKindOfEachChangeAndNamesARenamesNewPath()
    {
        _ = _git
            .Script("git", new ProcessRunResult(0, "abc123\n", false), "merge-base", "main", "HEAD")
            .Script(
                "git",
                new ProcessRunResult(0, "M\0src/a.cs\0A\0src/b.cs\0R100\0old.cs\0new.cs\0D\0gone.cs\0", false),
                "diff",
                "-z",
                "--name-status",
                "-M",
                "--relative",
                "abc123",
                "--")
            .Script(
                "git",
                new ProcessRunResult(0, "src/untracked.cs\0", false),
                "ls-files",
                "-z",
                "--others",
                "--exclude-standard",
                "--");

        IReadOnlyList<ChangedPath> changes = await AsyncReader().ResolveSinceMergeBaseAsync(Root, "main", CancellationToken.None);

        Assert.Equal(
            [
                new ChangedPath("gone.cs", ChangeKind.Deleted),
                new ChangedPath("new.cs", ChangeKind.Renamed),
                new ChangedPath("src/a.cs", ChangeKind.Modified),
                new ChangedPath("src/b.cs", ChangeKind.Added),
                new ChangedPath("src/untracked.cs", ChangeKind.Untracked)
            ],
            changes);
    }

    [Fact]
    public void ResolveSinceKeepsADeletedPathBecauseTheCallerComparesAgainstAKnownCommit()
    {
        _ = _git
            .Script(
                "git",
                new ProcessRunResult(0, "src/gone.cs\0src/kept.cs\0", false),
                "diff",
                "-z",
                "--name-only",
                "--relative",
                "abc123",
                "--")
            .Script(
                "git",
                new ProcessRunResult(0, "src/untracked.cs\0", false),
                "ls-files",
                "-z",
                "--others",
                "--exclude-standard",
                "--");

        Assert.Equal(["src/gone.cs", "src/kept.cs", "src/untracked.cs"], Reader().ResolveSince(Root, "abc123"));
    }

    [Fact]
    public void HeadAndMergeBaseCommitsAreTheTrimmedCommitNames()
    {
        _ = _git
            .Script("git", new ProcessRunResult(0, "abc123\n", false), "rev-parse", "--verify", "HEAD")
            .Script("git", new ProcessRunResult(0, "def456\n", false), "merge-base", "main", "HEAD");

        Assert.Equal("abc123", Reader().HeadCommit(Root));
        Assert.Equal("def456", Reader().MergeBaseCommit(Root, "main"));
    }

    [Fact]
    public async Task DiffSinceAsyncAsksForTheDiffAgainstTheCommitItWasGiven()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(0, "diff --git a/x.cs b/x.cs\n", false),
            "diff",
            "--no-color",
            "--relative",
            "abc123",
            "--");

        Assert.Contains("diff --git", await AsyncReader().DiffSinceAsync(Root, "abc123", CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACopyAndATypeChangeKeepTheirOwnKinds()
    {
        _ = _git
            .Script("git", new ProcessRunResult(0, "abc123\n", false), "merge-base", "main", "HEAD")
            .Script(
                "git",
                new ProcessRunResult(0, "C075\0src/old.cs\0src/copy.cs\0T\0src/mode.cs\0", false),
                "diff",
                "-z",
                "--name-status",
                "-M",
                "--relative",
                "abc123",
                "--")
            .Script(
                "git",
                new ProcessRunResult(0, string.Empty, false),
                "ls-files",
                "-z",
                "--others",
                "--exclude-standard",
                "--");

        Assert.Equal(
            [new ChangedPath("src/copy.cs", ChangeKind.Copied), new ChangedPath("src/mode.cs", ChangeKind.Modified)],
            await AsyncReader().ResolveSinceMergeBaseAsync(Root, "main", CancellationToken.None));
    }

    [Fact]
    public async Task ADiffFailureSurfacesRatherThanReturningAnEmptyPatch()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(128, string.Empty, false) { StandardError = "fatal: bad revision\n" },
            "diff",
            "--no-color",
            "--relative",
            "abc123",
            "--");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AsyncReader().DiffSinceAsync(Root, "abc123", CancellationToken.None));

        Assert.Contains("fatal: bad revision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffSinceMergeBaseAsyncReportsTheMergeBaseItDiffedFrom()
    {
        _ = _git
            .Script("git", new ProcessRunResult(0, "abc123\n", false), "merge-base", "main", "HEAD")
            .Script(
                "git",
                new ProcessRunResult(0, "diff --git a/x.cs b/x.cs\n", false),
                "diff",
                "--no-color",
                "--relative",
                "abc123",
                "--");

        (string mergeBase, string patch) = await AsyncReader().DiffSinceMergeBaseAsync(Root, "main", CancellationToken.None);

        Assert.Equal("abc123", mergeBase);
        Assert.Contains("diff --git", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void ACurrentBranchIsTheTrimmedName()
    {
        _ = _git.Script("git", new ProcessRunResult(0, "feature/x\n", false), "branch", "--show-current");

        Assert.Equal("feature/x", Reader().CurrentBranch(Root));
    }

    /// <summary>A fix packet's commits section names the paths after <c>--</c>, so a path is never read as an option.</summary>
    [Fact]
    public async Task LogOnelineAsyncAsksForTheCommitsOnTheGivenPathsAfterTheGivenCommit()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(0, "abc1234 one fix\ndef5678 another\n", false),
            "--literal-pathspecs",
            "log",
            "--oneline",
            "--no-color",
            "abc1234..HEAD",
            "--",
            "src/a.cs",
            "src/b.cs");

        IReadOnlyList<string> commits = await AsyncReader().LogOnelineAsync(Root, "abc1234", ["src/a.cs", "src/b.cs"], CancellationToken.None);

        Assert.Equal(["abc1234 one fix", "def5678 another"], commits);
    }

    /// <summary>
    /// Every path is a literal name, not a pattern, and a path that is not repository-relative is dropped: git would
    /// otherwise read it as a path on the machine rather than in the change.
    /// </summary>
    [Fact]
    public async Task LogOnelineAsyncRunsWithLiteralPathspecsAndDropsPathsOutsideTheRepository()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(0, "abc1234 one fix\n", false),
            "--literal-pathspecs",
            "log",
            "--oneline",
            "--no-color",
            "abc1234..HEAD",
            "--",
            "src/a.cs");

        IReadOnlyList<string> commits = await AsyncReader().LogOnelineAsync(
            Root, "abc1234", ["/abs/src/a.cs", "../outside.cs", "src/a.cs"], CancellationToken.None);

        Assert.Equal(["abc1234 one fix"], commits);
        Assert.Single(_git.Calls);
    }

    /// <summary>With no repository-relative path left, the read is empty and git is not asked for every commit.</summary>
    [Fact]
    public async Task LogOnelineAsyncWithNoRepositoryRelativePathListsNothingWithoutRunningGit()
    {
        Assert.Empty(await AsyncReader().LogOnelineAsync(Root, "abc1234", ["/abs/src/a.cs", "../outside.cs"], CancellationToken.None));
        Assert.Empty(_git.Calls);
    }

    [Fact]
    public void ADetachedHeadHasNoCurrentBranch()
    {
        _ = _git.Script("git", new ProcessRunResult(0, string.Empty, false), "branch", "--show-current");

        Assert.Equal(string.Empty, Reader().CurrentBranch(Root));
    }

    [Fact]
    public void AGitFailureNamesTheCommandItRanAndItsErrorMessage()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(128, string.Empty, false) { StandardError = "fatal: not a git repository\n" },
            "rev-parse",
            "--verify",
            "HEAD");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Reader().HeadCommit(Root));

        Assert.Contains("rev-parse --verify HEAD", error.Message, StringComparison.Ordinal);
        Assert.Contains("fatal: not a git repository", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCommitIsNotAnAncestorOfHeadRatherThanAnError()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(1, string.Empty, false) { StandardError = "fatal: Not a valid commit name\n" },
            "merge-base",
            "--is-ancestor",
            "deadbeef",
            "HEAD");

        Assert.False(Reader().IsAncestorOfHead(Root, "deadbeef"));
    }

    [Fact]
    public async Task AnAsyncReadWithNoMergeBaseIsRefusedWithAHintToFetchTheHistory()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(1, string.Empty, false) { StandardError = "fatal: Not a valid object name\n" },
            "merge-base",
            "origin/main",
            "HEAD");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AsyncReader().ResolveSinceMergeBaseAsync(Root, "origin/main", CancellationToken.None));

        Assert.Contains("Archive closeout scope is unknown", error.Message, StringComparison.Ordinal);
        Assert.Contains("git fetch --unshallow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMergeBaseThatResolvesNoCommitIsRefusedWithAHintToFetchTheHistory()
    {
        _ = _git.Script("git", new ProcessRunResult(0, string.Empty, false), "merge-base", "origin/main", "HEAD");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Reader().ResolveSinceMergeBase(Root, "origin/main"));

        Assert.Contains("no common commit", error.Message, StringComparison.Ordinal);
        Assert.Contains("git fetch --unshallow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAsyncGitFailureNamesTheCommandItRanAndItsErrorMessage()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(128, string.Empty, false) { StandardError = "fatal: not a git repository\n" },
            "diff",
            "-z",
            "--name-only",
            "--relative",
            "--diff-filter=ACMRT",
            "main",
            "--");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AsyncReader().ResolveAsync(Root, "main", CancellationToken.None));

        Assert.Contains("diff -z --name-only", error.Message, StringComparison.Ordinal);
        Assert.Contains("fatal: not a git repository", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThatOutlivesTheGitTimeoutSurfacesAsATimeout()
    {
        _ = _git.ScriptTimeout("git", "diff", "--no-color", "--relative", "abc123", "--");

        _ = await Assert.ThrowsAsync<TimeoutException>(
            () => AsyncReader().DiffSinceAsync(Root, "abc123", CancellationToken.None));
    }

    [Fact]
    public async Task AGitThatIsNotInstalledSurfacesAsAStartFailure()
    {
        _ = _git.WithMissingProgram("git");

        _ = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(
            () => AsyncReader().DiffSinceAsync(Root, "abc123", CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledAsyncReadNeverStartsGit()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AsyncReader().DiffSinceAsync(Root, "abc123", cancelled.Token));
    }
}
