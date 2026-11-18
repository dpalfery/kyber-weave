using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The repository locator over the git, file-system, path and environment seams: the root comes from git when it
/// answers, from the nearest <c>.git</c> entry otherwise, and every path is resolved against the injected current
/// directory rather than the process's own.
/// </summary>
public sealed class ArbiterRepositoryLocatorTests
{
    private const string WorkingDirectory = "/kw/work";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: WorkingDirectory);

    private ArbiterRepositoryLocator Locator(GitOutcome? gitOutcome = null) =>
        new(
            new FakeGit((_, _) => gitOutcome ?? new GitOutcome(1, string.Empty, "not a git repository")),
            _files,
            new FakePathResolver(_files),
            _environment);

    [Fact]
    public void TheRootIsWhatGitReports()
    {
        _files.CreateDirectory("/kw/checkout");

        Assert.Equal("/kw/checkout", Locator(new GitOutcome(0, "/kw/checkout\n", string.Empty)).ResolveRepositoryRoot(WorkingDirectory));
    }

    [Fact]
    public void AGitAnswerIsTakenAsAFullPath()
    {
        _files.CreateDirectory("/kw/checkout");

        Assert.Equal("/kw/checkout", Locator(new GitOutcome(0, "/kw/checkout/", string.Empty)).ResolveRepositoryRoot(WorkingDirectory));
    }

    [Fact]
    public void ARelativeGitAnswerResolvesAgainstTheDirectoryGitRanIn()
    {
        _files.CreateDirectory("/kw/work/project/checkout");

        Assert.Equal(
            "/kw/work/project/checkout",
            Locator(new GitOutcome(0, "checkout", string.Empty)).ResolveRepositoryRoot("/kw/work/project"));
    }

    [Fact]
    public void AGitAnswerThatNamesNoPathLeavesTheInjectedPathAsTheRoot()
    {
        _files.CreateDirectory(WorkingDirectory);

        Assert.Equal(WorkingDirectory, Locator(new GitOutcome(0, "a\0b", string.Empty)).ResolveRepositoryRoot(WorkingDirectory));
    }

    [Fact]
    public void AGitThatCannotAnswerLeavesTheInjectedPathAsTheRoot()
    {
        _files.CreateDirectory(WorkingDirectory);

        Assert.Equal(WorkingDirectory + "/nested", Locator().ResolveRepositoryRoot("nested"));
    }

    [Fact]
    public void TheArbiterDirectorySitsUnderTheArtifactsFolder()
    {
        _files.CreateDirectory("/kw/checkout");

        Assert.Equal(
            "/kw/checkout/artifacts/arbiter",
            Locator(new GitOutcome(0, "/kw/checkout", string.Empty)).ResolveArbiterDirectory(WorkingDirectory));
    }

    [Fact]
    public void FindGitRootWalksUpToTheNearestGitEntry()
    {
        _files.CreateDirectory("/kw/work/project/.git");
        _files.CreateDirectory("/kw/work/project/src/deep");

        Assert.Equal("/kw/work/project", Locator().FindGitRoot("/kw/work/project/src/deep"));
    }

    [Fact]
    public void FindGitRootAnswersTheResolvedStartWhenNoGitEntryExists()
    {
        _files.CreateDirectory("/kw/work/plain");

        Assert.Equal("/kw/work/plain", Locator().FindGitRoot("plain"));
    }

    private sealed class FakeGit(Func<string, IReadOnlyList<string>, GitOutcome> run) : IGitCommandRunner
    {
        public GitOutcome Run(string workingDirectory, IReadOnlyList<string> arguments) => run(workingDirectory, arguments);
    }
}
