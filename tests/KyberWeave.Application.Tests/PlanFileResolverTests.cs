using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The shared plan argument resolver. The host's docs root is <c>handbook</c>, not <c>docs</c>, so a spelling that
/// only works against the product default fails here.
/// </summary>
public sealed class PlanFileResolverTests
{
    private const string Plan = "handbook/plans/2026-11-13-job-status-cards.md";
    private const string TasksFile = "handbook/specs/search/tasks.md";
    private const string WorkingDirectory = "/kw/work";
    private readonly string _root = "/kw/repo";
    private readonly InMemoryFileSystem _files = new();
    private readonly FakePathResolver _paths;
    private readonly KyberWeaveConfig _config = KyberWeaveConfigAssembler.FromYaml("ontology:\n  docs-root: handbook\n");
    private readonly PlanFileResolver _resolver;

    public PlanFileResolverTests()
    {
        Write(".git/HEAD", "ref: refs/heads/main\n");
        Write(Plan, "# plan\n");
        Write("handbook/plans/README.md", "# index\n");
        Write("handbook/plans/2026-11-10-job-board.md", "# plan\n");
        Write("handbook/plans/2026-11-12-job-board.md", "# plan\n");
        Write("handbook/archive/plans/2026-10-01-job-status-cards.md", "# archived\n");
        Write(TasksFile, "# tasks\n");
        _paths = new FakePathResolver(_files);
        _resolver = ResolverFor(_root);
    }

    private PlanFileResolver ResolverFor(string root, string workingDirectory = WorkingDirectory) =>
        new(root, _config, new InMemoryRepositoryFileStore(_files), _paths, new FakeProcessEnvironment(currentDirectory: workingDirectory));

    private void Write(string relative, string content)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        _files.CreateDirectory(Path.GetDirectoryName(full)!);
        _files.WriteAllText(full, content);
    }

    private string Absolute(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void ThePlanningFoldersFollowTheConfiguredDocsRoot()
    {
        Assert.Equal(["handbook/plans", "handbook/specs"], _resolver.PlanningFolders());
    }

    [Theory]
    [InlineData("2026-11-13-job-status-cards")]
    [InlineData("2026-11-13-job-status-cards.md")]
    [InlineData("job-status-cards")]
    [InlineData("job-status-cards.md")]
    [InlineData("handbook/plans/2026-11-13-job-status-cards.md")]
    [InlineData("handbook/plans/2026-11-13-job-status-cards")]
    public void EverySpellingOfTheRepositoryRelativeOrBareNameFindsThePlanFromTheRoot(string typed)
    {
        PlanPathResolution resolution = _resolver.Resolve(typed, _root);

        Assert.Equal(Plan, resolution.Path);
    }

    [Theory]
    [InlineData("2026-11-13-job-status-cards")]
    [InlineData("2026-11-13-job-status-cards.md")]
    [InlineData("job-status-cards")]
    [InlineData("handbook/plans/2026-11-13-job-status-cards.md")]
    public void TheSameSpellingsFindItFromInsideThePlansFolder(string typed)
    {
        PlanPathResolution resolution = _resolver.Resolve(typed, Absolute("handbook/plans"));

        Assert.Equal(Plan, resolution.Path);
    }

    [Fact]
    public void APathRelativeToTheCurrentDirectoryIsFoundBeforeTheRepositoryRoot()
    {
        Assert.Equal(Plan, _resolver.Resolve("2026-11-13-job-status-cards.md", Absolute("handbook/plans")).Path);
        Assert.Equal(Plan, _resolver.Resolve("plans/2026-11-13-job-status-cards.md", Absolute("handbook")).Path);
    }

    [Fact]
    public void AFullPathIsFoundFromAnyDirectory()
    {
        PlanPathResolution resolution = _resolver.Resolve(Absolute(Plan), "/kw/elsewhere");

        Assert.Equal(Plan, resolution.Path);
    }

    [Fact]
    public void ASpecificationIsFoundByItsFolderNameOrItsTaskListPath()
    {
        Assert.Equal(TasksFile, _resolver.Resolve("search", _root).Path);
        Assert.Equal(TasksFile, _resolver.Resolve(TasksFile, _root).Path);
        Assert.Equal(TasksFile, _resolver.Resolve("handbook/specs/search", _root).Path);
    }

    [Fact]
    public void ASlugMatchingSeveralActivePlansIsAmbiguousAndListsThem()
    {
        PlanPathResolution resolution = _resolver.Resolve("job-board", _root);

        Assert.False(resolution.Found);
        Assert.True(resolution.Ambiguous);
        Assert.Equal(["handbook/plans/2026-11-10-job-board.md", "handbook/plans/2026-11-12-job-board.md"], resolution.Candidates);
    }

    [Fact]
    public void TheFullNameOfOneOfSeveralMatchingPlansIsNotAmbiguous()
    {
        Assert.Equal("handbook/plans/2026-11-12-job-board.md", _resolver.Resolve("2026-11-12-job-board", _root).Path);
    }

    [Fact]
    public void AnArchivedPlanWithTheSameSlugDoesNotMakeASlugAmbiguous()
    {
        Assert.Equal(Plan, _resolver.Resolve("job-status-cards", _root).Path);
    }

    [Fact]
    public void AnArgumentThatNamesNothingResolvesToNothingAndIsNotAmbiguous()
    {
        PlanPathResolution resolution = _resolver.Resolve("no-such-plan", _root);

        Assert.False(resolution.Found);
        Assert.False(resolution.Ambiguous);
        Assert.NotEmpty(resolution.SearchedFolders);
    }

    [Fact]
    public void ARelativeRepositoryRootIsResolvedAgainstTheInjectedWorkingDirectory()
    {
        PlanFileResolver fromParent = ResolverFor("repo", "/kw");
        PlanFileResolver fromElsewhere = ResolverFor("repo", "/kw/elsewhere");

        Assert.Equal(Plan, fromParent.Resolve("job-status-cards", "/kw").Path);
        Assert.False(fromElsewhere.Resolve("job-status-cards", "/kw/elsewhere").Found);
    }
}
