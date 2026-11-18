using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Scaffolding;

/// <summary>
/// The docs scaffolder resolves its repository root through the path seam against the injected environment, so a
/// unit test places the checkout wherever it likes.
/// </summary>
public sealed class DocsScaffolderSeamTests
{
    private const string WorkingDirectory = "/kw/work";
    private const string Root = "/kw/work/repo";

    private readonly InMemoryFileSystem _disk = new();
    private readonly InMemoryRepositoryFileStore _store;
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: WorkingDirectory);
    private readonly FakeEmbeddedResources _resources = new();

    public DocsScaffolderSeamTests()
    {
        _store = new InMemoryRepositoryFileStore(_disk);
        _paths = new FakePathResolver(_disk);
    }

    private DocsScaffolder Scaffolder() => new(
        _store,
        new FakeKyberWeaveConfigRepository(),
        new NoFiles(),
        new NoProbe(),
        _paths,
        _environment,
        TimeProvider.System,
        new KyberStandardsTemplateReader(_resources));

    [Fact]
    public void ItScaffoldsARelativeRootUnderTheInjectedWorkingDirectory()
    {
        ScaffoldResult result = Scaffolder().Scaffold("repo", "docs", reviewGates: false);

        Assert.True(_disk.FileExists($"{Root}/docs/catalog.md"));
        Assert.Contains(result.Files, file => file.RelativePath == "docs/catalog.md");
    }

    [Fact]
    public void ItRefusesADocsRootThatLeavesTheRepository()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            Scaffolder().Scaffold("repo", "../outside", reviewGates: false));

        Assert.Contains("outside the repository root", error.Message, StringComparison.Ordinal);
        Assert.False(_disk.DirectoryExists("/kw/outside"));
    }

    private sealed class NoFiles : IRepositoryFileLister
    {
        public IReadOnlyList<string> List(string repositoryRoot) => [];
    }

    private sealed class NoProbe : IRepositoryTreeProbe
    {
        public RepositoryRootAccess ProbeRoot(string root) => new(false);

        public bool DirectoryExists(string path) => false;

        public bool LeadsOutsideThroughSymlink(string root, string path) => false;
    }
}
