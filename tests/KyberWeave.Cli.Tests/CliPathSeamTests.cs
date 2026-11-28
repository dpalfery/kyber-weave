using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Run;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Sections;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The commands that took the process working directory straight from the framework resolve a relative argument against
/// the environment seam through the path seam, so each answer follows the directory a test names.
/// </summary>
public sealed class CliPathSeamTests : IDisposable
{
    private const string Work = "/kw/work";
    private const string Repo = "/kw/work/repo";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: Work);
    private readonly TestConsole _console = new();

    public CliPathSeamTests()
    {
        _paths = new FakePathResolver(_files);
        _files.CreateDirectory(Work);
        _files.CreateDirectory(Repo);
    }

    public void Dispose() => _console.Dispose();

    [Fact]
    public async Task RunDeliver_ARelativePath_IsDeliveredFromTheDirectoryUnderTheEnvironmentsWorkingDirectory()
    {
        RecordingDelivery delivery = new();

        int exitCode = await new RunDeliverCommand(delivery, new AcceptingConfigs(), _paths, _environment, _console)
            .ExecuteAsync(null!, new RunDeliverSettings { Path = "repo", Format = "json" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(Repo, delivery.RepoRoot);
    }

    [Fact]
    public async Task RunDeliver_AnAbsolutePath_IsDeliveredFromThatPathNotTheWorkingDirectory()
    {
        RecordingDelivery delivery = new();

        await new RunDeliverCommand(delivery, new AcceptingConfigs(), _paths, _environment, _console)
            .ExecuteAsync(null!, new RunDeliverSettings { Path = "/kw/elsewhere", Format = "json" }, CancellationToken.None);

        Assert.Equal("/kw/elsewhere", delivery.RepoRoot);
    }

    [Fact]
    public async Task Sections_LooksForTheGitRootFromTheEnvironmentsWorkingDirectory()
    {
        RecordingLocator locator = new(Repo);
        FileFacts facts = new(containedFile: "/kw/work/repo/doc.md");
        SectionSelector selector = new();

        int exitCode = await new ArbiterSectionsCommand(facts, locator, new FixedLength(10), selector, _paths, _environment, _console)
            .ExecuteAsync(null!, new ArbiterSectionsSettings { Path = "doc.md", Task = "t" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(Work, locator.Start);
        Assert.Equal(Repo, facts.ContainedRoot);
        Assert.Equal("/kw/work/repo/doc.md", selector.Document);
    }

    [Fact]
    public async Task Sections_ADocumentOutsideTheRoot_ExitsOneWithoutSelecting()
    {
        SectionSelector selector = new();

        int exitCode = await new ArbiterSectionsCommand(new FileFacts(containedFile: null), new RecordingLocator(Repo), new FixedLength(10), selector, _paths, _environment, _console)
            .ExecuteAsync(null!, new ArbiterSectionsSettings { Path = "../out.md", Task = "t" }, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(selector.Document);
    }

    [Fact]
    public async Task Comment_ARelativeRoot_IsContainedAgainstTheEnvironmentsWorkingDirectory()
    {
        FileFacts facts = new(containedFile: null);

        int exitCode = await new ArbiterCommentCommand(facts, null!, null!, null!, _paths, _environment, _console)
            .ExecuteAsync(null!, new ArbiterCommentSettings { Root = "repo", Path = "doc.md", Body = "b" }, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(Repo, facts.WithinRoot);
    }

    [Fact]
    public async Task Comment_AnAbsoluteRoot_IsUsedAsGiven()
    {
        FileFacts facts = new(containedFile: null);

        _ = await new ArbiterCommentCommand(facts, null!, null!, null!, _paths, _environment, _console)
            .ExecuteAsync(null!, new ArbiterCommentSettings { Root = "/kw/other", Path = "doc.md", Body = "b" }, CancellationToken.None);

        Assert.Equal("/kw/other", facts.WithinRoot);
    }

    [Fact]
    public async Task Audit_AnEventPlanIsMatchedByItsPathUnderTheRoot_WhetherOrNotThePlanFileExists()
    {
        _files.CreateDirectory("/kw/work/repo/docs/plans");
        _files.WriteAllText("/kw/work/repo/docs/plans/2026-11-13-a.md", "# A\n");
        ProbingAudits audits = new();
        InMemoryRepositoryFileStore store = new(_files);
        PlanArguments plans = new(new ArbiterRepoRoot(_files, _paths, _environment), _files, _paths, _environment, _console);

        int exitCode = await new ArbiterAuditCommand(new FixedConfigs(), new RecordingLocator(Repo), audits, store, _paths, _console, plans)
            .ExecuteAsync(null!, new ArbiterSettings { Path = Repo, Plan = "docs/plans/2026-11-13-a.md", WorkingDirectory = Repo, Format = "json" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(audits.Matches("docs/plans/2026-11-13-a.md"));
        Assert.True(audits.Matches("./docs/plans/../plans/2026-11-13-a.md"));
        Assert.False(audits.Matches("docs/plans/2026-11-12-other.md"));
        Assert.False(audits.Matches("docs/elsewhere/2026-11-13-a.md"));
    }

    private sealed class FixedConfigs : IArbiterConfigResolver
    {
        public bool TryResolve(string path, string? configFile, DiagnosticReport report, out KyberWeaveConfig config, out string? configPath)
        {
            config = KyberWeaveConfigDefaults.Product;
            configPath = null;
            return true;
        }

        public KyberWeaveConfig? TryLoadRepository(string root) => null;

        public KyberWeaveConfigLoadResult Load(string root, string? configFile, bool projectScope) => throw new NotSupportedException();

        public KyberWeaveConfig LoadFromYaml(string yaml) => throw new NotSupportedException();
    }

    private sealed class ProbingAudits : IArbiterAuditService
    {
        private Func<string?, bool>? _matches;

        public bool Matches(string plan) => _matches!(plan);

        public ArbiterAuditReport? Audit(string repositoryPath, KyberWeaveConfig config, DateTimeOffset? since, string? session, Func<string?, bool>? matchesPlan, DiagnosticReport report)
        {
            _matches = matchesPlan;
            return new ArbiterAuditReport(0, 0, [], 0, 0);
        }

        public Task<ArbiterAuditReport?> AuditAsync(string repositoryPath, KyberWeaveConfig config, DateTimeOffset? since, string? session, Func<string?, bool>? matchesPlan, DiagnosticReport report, CancellationToken cancellationToken = default)
        {
            _matches = matchesPlan;
            ArbiterAuditReport? audit = new ArbiterAuditReport(0, 0, [], 0, 0);
            return Task.FromResult<ArbiterAuditReport?>(audit);
        }
    }

    private sealed class RecordingDelivery : IRunDeliveryRunner
    {
        public string? RepoRoot { get; private set; }

        public Task<DeliveryOutcome> RunAsync(string repoRoot, KyberWeaveConfig config, DeliveryRequest request, CancellationToken cancellationToken = default)
        {
            RepoRoot = repoRoot;
            return Task.FromResult(new DeliveryOutcome(new DiagnosticReport(), Delivered: false));
        }
    }

    private sealed class AcceptingConfigs : IConfigLoadService
    {
        public bool TryLoad(string repoRoot, string? configPath, DiagnosticReport report, out KyberWeaveConfig config, out string? resolvedConfigPath)
        {
            config = KyberWeaveConfigDefaults.Product;
            resolvedConfigPath = null;
            return true;
        }

        public bool TryLoadWithDocsRoots(string repoRoot, string? configPath, IReadOnlyList<string> docsRoots, DiagnosticReport report, out KyberWeaveConfig config, out OntologyConfig ontology)
        {
            config = KyberWeaveConfigDefaults.Product;
            ontology = new OntologyConfig();
            return true;
        }
    }

    private sealed class RecordingLocator(string gitRoot) : IArbiterRepositoryLocator
    {
        public string? Start { get; private set; }

        public string ResolveRepositoryRoot(string path) => gitRoot;

        public string ResolveArbiterDirectory(string path) => throw new NotSupportedException();

        public Task<string> ResolveRepositoryRootAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(gitRoot);

        public Task<string> ResolveArbiterDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string FindGitRoot(string start)
        {
            Start = start;
            return gitRoot;
        }
    }

    private sealed class FileFacts(string? containedFile) : IFileTextFacts
    {
        public string? ContainedRoot { get; private set; }

        public string? WithinRoot { get; private set; }

        public FileExcerpt? ReadExcerpt(string repoRoot, string path, int line) => throw new NotSupportedException();

        public string? ResolveWithinRoot(string repoRoot, string path)
        {
            WithinRoot = repoRoot;
            return containedFile;
        }

        public string? ResolveContainedFile(string repoRoot, string path)
        {
            ContainedRoot = repoRoot;
            return containedFile;
        }

        public long LengthOf(string fullPath) => throw new NotSupportedException();
    }

    private sealed class FixedLength(long length) : IFileStatReader
    {
        public long GetLength(string path) => length;

        public DateTime GetLastWriteTimeUtc(string path) => throw new NotSupportedException();
    }

    private sealed class SectionSelector : IArbiterSectionSelectionService
    {
        public string? Document { get; private set; }

        public Task<SectionSelection> SelectAsync(string documentPath, string task, IArbiterProvider provider, CancellationToken ct = default)
        {
            Document = documentPath;
            return Task.FromResult(new SectionSelection([], FailedSafe: false));
        }
    }
}
