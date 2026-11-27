using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The docs verbs that read the console, the working directory or the platform print to the console they were given
/// and resolve the directory through the path and environment seams, so a unit test supplies both.
/// </summary>
public sealed class DocsCommandOutputSeamTests : IDisposable
{
    private const string WorkingDirectory = "/kw/work";
    private const string Root = "/kw/work/repo";

    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeKyberWeaveConfigRepository _configs = new();
    private readonly FakeConsole _standardError = new();

    public void Dispose() => _console.Dispose();

    [Fact]
    public void PacketResolvesTheWorkingDirectoryThroughThePathSeamAndReportsOnItsConsole()
    {
        IServiceProvider provider = DocsTestCommands.Services(services =>
        {
            _ = services.AddSingleton<IFileSystem>(_files);
            _ = services.AddSingleton<IPathResolver>(new FakePathResolver(_files));
            _ = services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(currentDirectory: WorkingDirectory));
            _ = services.AddSingleton<IKyberWeaveConfigRepository>(_configs);
        });
        DocsPacketSettings settings = new()
        {
            Config = "/kw/work/missing.yml",
            WorkingDirectory = "repo",
        };

        int exit = new DocsPacketCommand(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IWritableFileSystem>(),
            provider.GetRequiredService<IProcessEnvironment>(),
            provider.GetRequiredService<IPlanTaskTargetService>(),
            provider.GetRequiredService<ITaskGateSelectionService>(),
            provider.GetRequiredService<ICodeGraphPortsFactory>(),
            provider.GetRequiredService<IRepositoryFileStore>(),
            provider.GetRequiredService<IGitRepositoryFacts>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>(),
            provider.GetRequiredService<IDocumentSourceFactory>(),
            provider.GetRequiredService<IPathResolver>(),
            _console,
            _standardError,
            Plans(provider),
            new ArbiterRepoRoot(
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<IPathResolver>(),
                provider.GetRequiredService<IProcessEnvironment>()),
            provider.GetRequiredService<IKwSkillCatalogFactory>()).Execute(null!, settings, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains(Root, _configs.LoadedRoots, StringComparer.Ordinal);
        Assert.Contains(KyberWeaveConfigDiagnosticCodes.LoadError, _console.Output, StringComparison.Ordinal);
    }

    private PlanArguments Plans(IServiceProvider provider) => Plans(
        provider.GetRequiredService<IPathResolver>(),
        provider.GetRequiredService<IProcessEnvironment>());

    private PlanArguments Plans(IPathResolver paths, IProcessEnvironment environment) => new(
        new ArbiterRepoRoot(_files, paths, environment),
        _files,
        paths,
        environment,
        _console);

    [Fact]
    public void TasksReportsAConflictingModePairOnItsConsole()
    {
        DocsTasksSettings settings = new() { List = true, Write = true, Format = "json" };

        int exit = new DocsTasksCommand(
            tasks: null!,
            pickers: null!,
            arbiterKeys: null!,
            arbiterDryRun: null!,
            files: new InMemoryRepositoryFileStore(_files),
            configs: _configs,
            console: _console,
            plans: Plans(new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: WorkingDirectory))).Execute(null!, settings, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains(DocsTasksCommand.ConflictingModes, _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogReportsAConfigurationFailureOnItsConsole()
    {
        DocsSettings settings = new() { Path = Root, Config = "/kw/work/missing.yml", Format = "json" };

        int exit = new DocsCatalogCommand(_configs, documents: null!, _console)
            .Execute(null!, settings, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains(KyberWeaveConfigDiagnosticCodes.LoadError, _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogRendersItsSummaryOnTheInjectedConsole()
    {
        DocsSettings settings = new() { Path = Root };

        int exit = new DocsCatalogCommand(_configs, new EmptyDocumentSourceFactory(), _console)
            .Execute(null!, settings, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("0 documents in scope", _console.Output, StringComparison.Ordinal);
    }

    private sealed class EmptyDocumentSourceFactory : IDocumentSourceFactory, IDocumentSource
    {
        public IDocumentSource Create(string repoRoot, OntologyConfig ontology) => this;

        public DocumentSet Load() => new() { Documents = [] };

        public DocumentModel LoadArchivedDocument(string relativePath) =>
            throw new InvalidOperationException("The catalogue reads no archived entry.");
    }
}
