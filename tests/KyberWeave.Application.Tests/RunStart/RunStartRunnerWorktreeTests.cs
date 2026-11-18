using KyberWeave.Application.RunStart;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.RunStart;

/// <summary>Where a run's worktree folder goes and how two runs in one second are kept apart, over the in-memory file system.</summary>
public sealed class RunStartRunnerWorktreeTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 18, 1, 2, 3, TimeSpan.Zero);

    private readonly InMemoryFileSystem _files = new();

    private RunStartRunner Runner(string repositoryRoot) =>
        new(
            new UnusedGit(),
            _files,
            new FakeProcessEnvironment(currentDirectory: "/kw/work"),
            new FakePathResolver(_files),
            repositoryRoot);

    [Fact]
    public void TheWorktreeFolderSitsBesideTheCheckoutAndCarriesTheStampAndBranch()
    {
        string path = Runner("/kw/work/repo/").WorktreePathFor("claude/x", Started);

        Assert.Equal("/kw/work/repo.worktrees/20261118T010203Z-claude-x", path);
        Assert.True(_files.FileExists(path + ".claim"));
    }

    [Fact]
    public void ARelativeRepositoryRootIsReadAgainstTheWorkingDirectory()
    {
        string path = Runner("repo").WorktreePathFor("b", Started);

        Assert.Equal("/kw/work/repo.worktrees/20261118T010203Z-b", path);
    }

    [Fact]
    public void ASecondRunInTheSameSecondTakesTheNextSuffix()
    {
        RunStartRunner runner = Runner("/kw/work/repo");

        string first = runner.WorktreePathFor("b", Started);
        string second = runner.WorktreePathFor("b", Started);

        Assert.Equal(first + "-2", second);
    }

    [Fact]
    public void ReleasingTheClaimFreesTheName()
    {
        RunStartRunner runner = Runner("/kw/work/repo");
        string first = runner.WorktreePathFor("b", Started);

        runner.ReleaseWorktreeClaim(first);

        Assert.False(_files.FileExists(first + ".claim"));
        Assert.Equal(first, runner.WorktreePathFor("b", Started));
    }

    private sealed class UnusedGit : IGitCommandRunner
    {
        public GitOutcome Run(string workingDirectory, IReadOnlyList<string> arguments) => throw new NotSupportedException();
    }
}
