using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Mcp;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using KyberWeave.Application;

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
        public IPlanTaskAgentPicker? ForConfig(KyberWeaveConfig config) => null;
    }

    /// <summary>A picker factory that always returns <paramref name="picker"/>.</summary>
    internal sealed class FixedPickerFactory(Func<IPlanTaskAgentPicker> picker) : IPlanTaskAgentPickerFactory
    {
        public IPlanTaskAgentPicker? ForConfig(KyberWeaveConfig config) => picker();
    }

    /// <summary>The container, with <paramref name="configure"/> applied before the application's own registrations.</summary>
    public static IServiceProvider Services(Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IPlanTaskAgentPickerFactory>(new NoPickerFactory());
        configure?.Invoke(services);
        return services.AddWeaveApplication().BuildServiceProvider();
    }

    public static DocsCloseoutCommand Closeout(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsCloseoutCommand(
            provider.GetRequiredService<IPlanCloseoutService>(),
            provider.GetRequiredService<IRepositoryFileStore>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>());
    }

    public static DocsFinalizeCommand Finalize(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsFinalizeCommand(
            provider.GetRequiredService<IPlanFinalizeService>(),
            provider.GetRequiredService<IRepositoryFileStore>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>());
    }

    public static DocsValidateCommand Validate(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsValidateCommand(
            provider.GetRequiredService<IDocsSchemaGate>(),
            provider.GetRequiredService<IDocumentSourceFactory>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>());
    }

    public static DocsPacketCommand Packet(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsPacketCommand(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IProcessEnvironment>(),
            provider.GetRequiredService<IPlanTaskTargetService>(),
            provider.GetRequiredService<ITaskGateSelectionService>(),
            provider.GetRequiredService<ICodeGraphPortsFactory>(),
            provider.GetRequiredService<IRepositoryFileStore>(),
            provider.GetRequiredService<IGitRepositoryFacts>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>());
    }

    public static DocsTasksCommand Tasks(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsTasksCommand(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPlanTaskAgentPickerFactory>(),
            provider.GetRequiredService<IArbiterKeyService>(),
            provider.GetRequiredService<IArbiterDryRunService>(),
            provider.GetRequiredService<IRepositoryFileStore>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>());
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

    public static DocsReviewImportCommand ReviewImport(IDocsAnalysisCommandService service) =>
        new(service, Services().GetRequiredService<IFileSystem>());

    public static DocsExportGraphCommand ExportGraph() => new(
        Services().GetRequiredService<IManagedGlossaryFactory>(),
        Services().GetRequiredService<ICodeGraphPortsFactory>(),
        Services().GetRequiredService<IKyberWeaveConfigRepository>(),
        Services().GetRequiredService<IWritableFileSystem>());

    public static DocsRootRegistry Registry(string root, Func<string, DocsTools> factory)
    {
        IServiceProvider provider = Services();
        return new DocsRootRegistry(root, factory, provider.GetRequiredService<IGitWorktreeIdentity>(), provider.GetRequiredService<IFileSystem>());
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
    public static CodeGraphFreshness Freshness(
        string root, IProcessExecutor? executor = null, bool refreshEnabled = true, Action<TimeSpan>? pause = null)
    {
        IServiceProvider provider = Services();
        return new CodeGraphFreshness(
            root,
            refreshEnabled,
            provider.GetRequiredService<ICodeGraphProvisioner>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>(),
            provider.GetRequiredService<TimeProvider>(),
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
}
