using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Mcp;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using KyberWeave.Application;
using Spectre.Console;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Builds the Docs commands from the container the real host builds, with whichever collaborators a test replaces
/// registered first. No test run reaches a paid decision model unless a test registers a picker for it.
/// </summary>
internal static class DocsTestCommands
{
    /// <summary>A picker factory that configures no decision model.</summary>
    internal sealed class NoPickerFactory : IPlanTaskAgentPickerFactory
    {
        public Task<IPlanTaskAgentPicker?> ForConfigAsync(KyberWeaveConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult<IPlanTaskAgentPicker?>(null);
    }

    /// <summary>A picker factory that always returns <paramref name="picker"/>.</summary>
    internal sealed class FixedPickerFactory(Func<IPlanTaskAgentPicker> picker) : IPlanTaskAgentPickerFactory
    {
        public Task<IPlanTaskAgentPicker?> ForConfigAsync(KyberWeaveConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult<IPlanTaskAgentPicker?>(picker());
    }

    /// <summary>The container, with <paramref name="configure"/> applied before the application's own registrations.</summary>
    public static IServiceProvider Services(Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IPlanTaskAgentPickerFactory>(new NoPickerFactory());
        configure?.Invoke(services);
        return services.AddWeaveApplication().BuildServiceProvider();
    }

    /// <summary>
    /// <c>docs init</c> over the container's real services, with the doctor and console a test supplies. A test that
    /// reads the report passes a capturing console; one that only checks exit codes takes the default.
    /// </summary>
    public static DocsInitCommand Init(
        IRepositoryDoctor doctor,
        IAnsiConsole? console = null,
        IServiceProvider? services = null,
        IProcessRunner? executor = null,
        string? mcpExecutable = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsInitCommand(
            McpWiringTestServices.Wiring,
            provider.GetRequiredService<IDocsScaffoldingService>(),
            doctor,
            provider.GetRequiredService<IKyberWeaveConfigRepository>(),
            console ?? AnsiConsole.Console,
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IPathResolver>(),
            provider.GetRequiredService<IProcessEnvironment>(),
            provider.GetRequiredService<IProcessRunner>())
        {
            Executor = executor,
            McpExecutable = mcpExecutable,
        };
    }

    private static ArbiterRepoRoot RepoRootFor(IServiceProvider provider) => new(
        provider.GetRequiredService<IFileSystem>(),
        provider.GetRequiredService<IPathResolver>(),
        provider.GetRequiredService<IProcessEnvironment>());

    private static PlanArguments PlanArgumentsFor(IServiceProvider provider, IAnsiConsole? console) => new(
        RepoRootFor(provider),
        provider.GetRequiredService<IFileSystem>(),
        provider.GetRequiredService<IPathResolver>(),
        provider.GetRequiredService<IProcessEnvironment>(),
        console ?? AnsiConsole.Console);

    public static DocsCloseoutCommand Closeout(IServiceProvider? services = null, IAnsiConsole? console = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsCloseoutCommand(
            provider.GetRequiredService<IPlanCloseoutService>(),
            provider.GetRequiredService<IRepositoryFileStore>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>(),
            provider.GetRequiredService<TimeProvider>(),
            console ?? AnsiConsole.Console,
            PlanArgumentsFor(provider, console));
    }

    public static DocsFinalizeCommand Finalize(IServiceProvider? services = null, IAnsiConsole? console = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsFinalizeCommand(
            provider.GetRequiredService<IPlanFinalizeService>(),
            provider.GetRequiredService<IRepositoryFileStore>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>(),
            provider.GetRequiredService<TimeProvider>(),
            console ?? AnsiConsole.Console,
            PlanArgumentsFor(provider, console));
    }

    public static DocsValidateCommand Validate(IServiceProvider? services = null, IAnsiConsole? console = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsValidateCommand(
            provider.GetRequiredService<IDocsSchemaGate>(),
            provider.GetRequiredService<IDocumentSourceFactory>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>(),
            console ?? AnsiConsole.Console);
    }

    public static DocsPacketCommand Packet(IServiceProvider? services = null, IAnsiConsole? console = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsPacketCommand(
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
            console ?? AnsiConsole.Console,
            provider.GetRequiredService<IConsole>(),
            PlanArgumentsFor(provider, console),
            RepoRootFor(provider),
            provider.GetRequiredService<IKwSkillCatalogFactory>());
    }

    public static DocsTasksCommand Tasks(IServiceProvider? services = null, IAnsiConsole? console = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsTasksCommand(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPlanTaskAgentPickerFactory>(),
            provider.GetRequiredService<IArbiterKeyService>(),
            provider.GetRequiredService<IArbiterDryRunService>(),
            provider.GetRequiredService<IRepositoryFileStore>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>(),
            console ?? AnsiConsole.Console,
            PlanArgumentsFor(provider, console));
    }

    public static IDocsSchemaGate SchemaGate => Services().GetRequiredService<IDocsSchemaGate>();

    internal static RepositoryDocsAnalysisCommandService AnalysisService(DocsAnalysisCompositionFactories factories)
    {
        IServiceProvider provider = Services();
        return new RepositoryDocsAnalysisCommandService(
            factories,
            provider.GetRequiredService<IManagedGlossaryFactory>(),
            provider.GetRequiredService<IDocumentSourceFactory>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>());
    }

    public static DocsReviewImportCommand ReviewImport(IDocsAnalysisCommandService service, IAnsiConsole? console = null) =>
        new(service, Services().GetRequiredService<IFileSystem>(), console ?? AnsiConsole.Console);

    public static DocsReviewExportCommand ReviewExport(IDocsAnalysisCommandService service, IAnsiConsole? console = null) =>
        new(service, Services().GetRequiredService<IWritableFileSystem>(), console ?? AnsiConsole.Console);

    public static DocsExportGraphCommand ExportGraph(IAnsiConsole? console = null) => new(
        Services().GetRequiredService<IManagedGlossaryFactory>(),
        Services().GetRequiredService<ICodeGraphPortsFactory>(),
        Services().GetRequiredService<IKyberWeaveConfigRepository>(),
        Services().GetRequiredService<IWritableFileSystem>(),
        Services().GetRequiredService<IDocumentSourceFactory>(),
        console ?? AnsiConsole.Console);

    public static DocsRootRegistry Registry(string root, Func<string, DocsTools> factory)
    {
        IServiceProvider provider = Services();
        return new DocsRootRegistry(
            root,
            factory,
            provider.GetRequiredService<IGitWorktreeIdentity>(),
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IPathResolver>(),
            provider.GetRequiredService<IProcessEnvironment>());
    }

    public static IPacketHostFacts HostFacts => Services().GetRequiredService<IPacketHostFacts>();

    /// <summary>
    /// A CodeGraph refresh schedule over <paramref name="root"/>, with the container's provisioner, the real
    /// configuration repository and the system clock.
    /// </summary>
    /// <param name="root">The bound repository root.</param>
    /// <param name="executor">Runs the CodeGraph child; null for the bounded production runner.</param>
    /// <param name="refreshEnabled">False to model <c>--no-codegraph-refresh</c>.</param>
    /// <param name="pause">Waits between sync attempts on a held CodeGraph lock; null to really wait.</param>
    public static ICodeGraphFreshnessGate Freshness(
        string root,
        IProcessRunner? executor = null,
        bool refreshEnabled = true,
        Func<TimeSpan, CancellationToken, Task>? pause = null)
    {
        IServiceProvider provider = Services();
        return new CodeGraphFreshness(
            root,
            refreshEnabled,
            provider.GetRequiredService<ICodeGraphProvisioner>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IPathResolver>(),
            provider.GetRequiredService<IProcessEnvironment>(),
            provider.GetRequiredService<IProcessRunner>(),
            executor,
            pause);
    }

    /// <summary>The review-gate catalogue over the real adapters the host wires.</summary>
    public static IReviewGateCatalogue Catalogue => Services().GetRequiredService<IReviewGateCatalogue>();

    /// <summary>The scaffolder over the real adapters the host wires.</summary>
    public static IDocsScaffoldingService Scaffolding => Services().GetRequiredService<IDocsScaffoldingService>();

    public static RunDeliveryPorts DeliveryPorts => Services().GetRequiredService<RunDeliveryPorts>();
}

/// <summary>The MCP host's collaborators built over the container the real host builds.</summary>
internal static class McpTestServices
{
    /// <summary>The analysis reader over <paramref name="repositoryRoot"/>.</summary>
    public static RepositoryDocsAnalysisReader AnalysisReader(string repositoryRoot) =>
        DocsToolsComposition.AnalysisReader(DocsTestCommands.Services(), repositoryRoot);

    /// <summary>The docs tools over <paramref name="host"/>, with the analysis reader <paramref name="services"/> builds for its root.</summary>
    /// <param name="host">The document index the tools answer from.</param>
    /// <param name="services">The container whose ports the analysis reader reads through.</param>
    /// <param name="repositoryRoot">The repository root the analysis reader is bound to.</param>
    public static DocsTools Tools(DocumentIndexHost host, IServiceProvider services, string repositoryRoot) =>
        DocsToolsComposition.Create(host, DocsToolsComposition.AnalysisReader(services, repositoryRoot));
}
