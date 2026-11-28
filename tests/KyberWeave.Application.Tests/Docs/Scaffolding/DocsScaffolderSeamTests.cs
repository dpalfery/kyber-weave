using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Review;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;
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
    public async Task ItScaffoldsARelativeRootUnderTheInjectedWorkingDirectory()
    {
        ScaffoldResult result = await Scaffolder().ScaffoldAsync("repo", "docs", reviewGates: false);

        Assert.True(_disk.FileExists($"{Root}/docs/catalog.md"));
        Assert.Contains(result.Files, file => file.RelativePath == "docs/catalog.md");
    }

    [Fact]
    public async Task ItRefusesADocsRootThatLeavesTheRepository()
    {
        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Scaffolder().ScaffoldAsync("repo", "../outside", reviewGates: false));

        Assert.Contains("outside the repository root", error.Message, StringComparison.Ordinal);
        Assert.False(_disk.DirectoryExists("/kw/outside"));
    }

    [Fact]
    public async Task AShippedSkillsListThatIsNotASequenceOfNamesStopsTheScaffold()
    {
        _resources.Add("Standards.csharp.skills.yml", "name: not-a-list\n");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Scaffolder().ScaffoldAsync("repo", "docs", reviewGates: false, standards: ["csharp"]));

        Assert.Contains("csharp", error.Message, StringComparison.Ordinal);
        Assert.Contains("skills list", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A list the YAML parser itself rejects is reported with the parser's reason, not only the
    /// shape sentence, so the author of the shipped template can see what was wrong with the text.
    /// The parser's own wording is not pinned here — only that a reason follows the sentence.
    /// </summary>
    [Fact]
    public async Task AShippedSkillsListThatIsNotYamlNamesTheParserReason()
    {
        _resources.Add("Standards.csharp.skills.yml", "- go-gates\n  bad: [\n");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Scaffolder().ScaffoldAsync("repo", "docs", reviewGates: false, standards: ["csharp"]));

        Assert.Contains("csharp", error.Message, StringComparison.Ordinal);
        Assert.Contains("skills list", error.Message, StringComparison.Ordinal);
        Assert.False(
            error.Message.EndsWith("skill names.", StringComparison.Ordinal),
            $"the parser's reason is carried, not swallowed: {error.Message}");
    }

    [Fact]
    public async Task AShippedSkillsListNamingASkillTheBuildDoesNotShipStopsTheScaffold()
    {
        _resources.Add("Standards.csharp.skills.yml", "- never-shipped\n");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Scaffolder().ScaffoldAsync("repo", "docs", reviewGates: false, standards: ["csharp"]));

        Assert.Contains("csharp", error.Message, StringComparison.Ordinal);
        Assert.Contains("never-shipped", error.Message, StringComparison.Ordinal);
        Assert.False(_disk.DirectoryExists($"{Root}/docs/standards/skills/never-shipped"));
    }

    [Fact]
    public async Task ReviewBlockWriterPlanAsyncRendersWithoutBlocking()
    {
        _disk.CreateDirectory(Root);
        ReviewBlockWriter writer = new(_store, new NoFiles(), new NoProbe(), _paths, _environment);

        ReviewGatesPlan plan = await writer.PlanAsync(Root, ["csharp"], CancellationToken.None);

        Assert.NotNull(plan);
        Assert.True(plan.ShouldWrite);
    }

    [Fact]
    public async Task ScaffoldAsyncListsFilesWithoutBlockingAndScaffoldsReviewGates()
    {
        _disk.CreateDirectory(Root);
        RecordingLister lister = new();
        FileRepositoryTreeProbe tree = new(_disk, _paths, _environment);
        FileKyberWeaveConfigRepository configs = new(tree, _disk, _paths, _environment);
        DocsScaffolder scaffolder = new(
            _store,
            configs,
            lister,
            tree,
            _paths,
            _environment,
            TimeProvider.System,
            new KyberStandardsTemplateReader(_resources));

        using CancellationTokenSource cts = new();
        ScaffoldResult result = await scaffolder.ScaffoldAsync(
            "repo",
            "docs",
            standards: ["csharp"],
            cancellationToken: cts.Token);

        Assert.Equal(0, lister.SyncCalls);
        Assert.Equal(1, lister.AsyncCalls);
        Assert.Equal(cts.Token, lister.LastToken);
        Assert.NotNull(result.ReviewGates);
        Assert.True(result.ReviewGates.Written);
        Assert.True(_disk.FileExists($"{Root}/docs/catalog.md"));
    }

    [Fact]
    public async Task ScaffoldAsyncHonorsCancellationBeforeListing()
    {
        _disk.CreateDirectory(Root);
        RecordingLister lister = new();
        DocsScaffolder scaffolder = new(
            _store,
            new FakeKyberWeaveConfigRepository(),
            lister,
            new NoProbe(),
            _paths,
            _environment,
            TimeProvider.System,
            new KyberStandardsTemplateReader(_resources));

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            scaffolder.ScaffoldAsync("repo", "docs", cancellationToken: cts.Token));

        Assert.Equal(0, lister.SyncCalls);
        Assert.Equal(0, lister.AsyncCalls);
        Assert.False(_disk.FileExists($"{Root}/docs/catalog.md"));
    }

    private sealed class RecordingLister : IRepositoryFileLister
    {
        public int SyncCalls { get; private set; }
        public int AsyncCalls { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public IReadOnlyList<string> List(string repositoryRoot)
        {
            SyncCalls++;
            return [];
        }

        public Task<IReadOnlyList<string>> ListAsync(
            string repositoryRoot,
            CancellationToken cancellationToken = default)
        {
            AsyncCalls++;
            LastToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class NoFiles : IRepositoryFileLister
    {
        public IReadOnlyList<string> List(string repositoryRoot) => [];

        public Task<IReadOnlyList<string>> ListAsync(
            string repositoryRoot,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoProbe : IRepositoryTreeProbe
    {
        public RepositoryRootAccess ProbeRoot(string root) => new(false);

        public bool DirectoryExists(string path) => false;

        public bool LeadsOutsideThroughSymlink(string root, string path) => false;
    }
}
