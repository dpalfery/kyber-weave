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

    private Task<GitWorktreeRoots> TryRootsAsync(string directory) =>
        new GitWorktreeIdentity(_git, FakePathResolver.OverEmptyFileSystem(), new FakeProcessEnvironment()).TryRootsAsync(directory);

    [Fact]
    public async Task GitsTwoLinesAreTheTopLevelAndTheCommonDirectoryAskedForInTheGivenDirectory()
    {
        _ = _git.Script("git", new ProcessRunResult(0, "/repo\n/repo/.git\n", false), Argv);

        GitWorktreeRoots roots = await TryRootsAsync("/repo/sub");
        bool named = roots.Found;

        Assert.True(named);
        Assert.Equal(Path.GetFullPath("/repo"), roots.TopLevel);
        Assert.Equal(Path.GetFullPath("/repo/.git"), roots.CommonDirectory);
        Assert.Null(roots.Failure);
        ProcessRunRequest call = Assert.Single(_git.Calls);
        Assert.Equal("/repo/sub", call.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout);
    }

    [Fact]
    public async Task AFailedRunReportsOnlyTheFirstLineOfGitsComplaint()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(128, string.Empty, false) { StandardOutput = string.Empty, StandardError = "\nfatal: not a git repository\nhint: more" },
            Argv);

        GitWorktreeRoots roots = await TryRootsAsync("/tmp");
        bool named = roots.Found;

        Assert.False(named);
        Assert.Null(roots.TopLevel);
        Assert.Null(roots.CommonDirectory);
        Assert.Equal("fatal: not a git repository", roots.Failure);
    }

    [Fact]
    public async Task AFailedRunWithNoComplaintReportsUnavailable()
    {
        _ = _git.Script("git", new ProcessRunResult(1, string.Empty, false), Argv);

        GitWorktreeRoots roots = await TryRootsAsync("/tmp");
        string? failure = roots.Failure;

        Assert.Equal("unavailable", failure);
    }

    [Fact]
    public async Task LessThanTwoLinesIsNotAWorkingTree()
    {
        _ = _git.Script("git", new ProcessRunResult(0, "/repo\n", false), Argv);

        GitWorktreeRoots roots = await TryRootsAsync("/repo");
        bool named = roots.Found;
        string? failure = roots.Failure;

        Assert.False(named);
        Assert.Equal("git reported no working tree", failure);
    }

    [Fact]
    public async Task ATimedOutRunNamesTheFiveSecondBound()
    {
        _ = _git.ScriptTimeout("git", Argv);

        GitWorktreeRoots roots = await TryRootsAsync("/repo");
        bool named = roots.Found;
        string? failure = roots.Failure;

        Assert.False(named);
        Assert.Equal("The process did not exit within 5 seconds.", failure);
    }

    [Fact]
    public async Task AMissingGitIsAFailureWithTheSeamsMessageNotAnException()
    {
        _ = _git.WithMissingProgram("git");

        GitWorktreeRoots roots = await TryRootsAsync("/repo");
        bool named = roots.Found;
        string? failure = roots.Failure;

        Assert.False(named);
        Assert.Equal("command not found: git", failure);
    }

    [Fact]
    public async Task AMissingWorkingDirectoryIsAFailureNotAnException()
    {
        _ = _git.WithMissingDirectory("/gone");

        GitWorktreeRoots roots = await TryRootsAsync("/gone");
        bool named = roots.Found;
        string? failure = roots.Failure;

        Assert.False(named);
        Assert.Contains("/gone", failure, StringComparison.Ordinal);
    }
}
