using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Docs.Mcp;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Mcp;

/// <summary>The worktree identity over a scripted process seam: git's answers and failures are scripted, so nothing starts.</summary>
public sealed class GitWorktreeIdentityTests
{
    private static readonly string[] Argv = ["rev-parse", "--path-format=absolute", "--show-toplevel", "--git-common-dir"];

    private readonly ScriptedProcessRunner _git = new();

    private bool TryRoots(string directory, out string? topLevel, out string? commonDirectory, out string? failure) =>
        new GitWorktreeIdentity(_git, FakePathResolver.OverEmptyFileSystem(), new FakeProcessEnvironment()).TryRoots(directory, out topLevel, out commonDirectory, out failure);

    [Fact]
    public void GitsTwoLinesAreTheTopLevelAndTheCommonDirectoryAskedForInTheGivenDirectory()
    {
        _ = _git.Script("git", new ProcessRunResult(0, "/repo\n/repo/.git\n", false), Argv);

        bool named = TryRoots("/repo/sub", out string? top, out string? common, out string? failure);

        Assert.True(named);
        Assert.Equal(Path.GetFullPath("/repo"), top);
        Assert.Equal(Path.GetFullPath("/repo/.git"), common);
        Assert.Null(failure);
        ProcessRunRequest call = Assert.Single(_git.Calls);
        Assert.Equal("/repo/sub", call.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout);
    }

    [Fact]
    public void AFailedRunReportsOnlyTheFirstLineOfGitsComplaint()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(128, string.Empty, false) { StandardOutput = string.Empty, StandardError = "\nfatal: not a git repository\nhint: more" },
            Argv);

        bool named = TryRoots("/tmp", out string? top, out string? common, out string? failure);

        Assert.False(named);
        Assert.Null(top);
        Assert.Null(common);
        Assert.Equal("fatal: not a git repository", failure);
    }

    [Fact]
    public void AFailedRunWithNoComplaintReportsUnavailable()
    {
        _ = _git.Script("git", new ProcessRunResult(1, string.Empty, false), Argv);

        _ = TryRoots("/tmp", out _, out _, out string? failure);

        Assert.Equal("unavailable", failure);
    }

    [Fact]
    public void LessThanTwoLinesIsNotAWorkingTree()
    {
        _ = _git.Script("git", new ProcessRunResult(0, "/repo\n", false), Argv);

        bool named = TryRoots("/repo", out _, out _, out string? failure);

        Assert.False(named);
        Assert.Equal("git reported no working tree", failure);
    }

    [Fact]
    public void ATimedOutRunNamesTheFiveSecondBound()
    {
        _ = _git.ScriptTimeout("git", Argv);

        bool named = TryRoots("/repo", out _, out _, out string? failure);

        Assert.False(named);
        Assert.Equal("The process did not exit within 5 seconds.", failure);
    }

    [Fact]
    public void AMissingGitIsAFailureWithTheSeamsMessageNotAnException()
    {
        _ = _git.WithMissingProgram("git");

        bool named = TryRoots("/repo", out _, out _, out string? failure);

        Assert.False(named);
        Assert.Equal("command not found: git", failure);
    }

    [Fact]
    public void AMissingWorkingDirectoryIsAFailureNotAnException()
    {
        _ = _git.WithMissingDirectory("/gone");

        bool named = TryRoots("/gone", out _, out _, out string? failure);

        Assert.False(named);
        Assert.Contains("/gone", failure, StringComparison.Ordinal);
    }
}
