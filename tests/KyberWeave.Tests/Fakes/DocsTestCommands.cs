using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Mcp;
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

    public static DocsCloseoutCommand Closeout(IServiceProvider? services = null) =>
        new((services ?? Services()).GetRequiredService<IPlanCloseoutService>());

    public static DocsFinalizeCommand Finalize(IServiceProvider? services = null) =>
        new((services ?? Services()).GetRequiredService<IPlanFinalizeService>());

    public static DocsValidateCommand Validate(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsValidateCommand(provider.GetRequiredService<IDocsSchemaGate>(), provider.GetRequiredService<IDocumentSourceFactory>());
    }

    public static DocsPacketCommand Packet(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsPacketCommand(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IProcessEnvironment>());
    }

    public static DocsTasksCommand Tasks(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsTasksCommand(provider.GetRequiredService<IPlanTaskService>(), provider.GetRequiredService<IPlanTaskAgentPickerFactory>());
    }

    public static IDocsSchemaGate SchemaGate => Services().GetRequiredService<IDocsSchemaGate>();

    internal static RepositoryDocsAnalysisCommandService AnalysisService(DocsAnalysisCompositionFactories factories)
    {
        IServiceProvider provider = Services();
        return new RepositoryDocsAnalysisCommandService(
            factories, provider.GetRequiredService<IManagedGlossaryFactory>(), provider.GetRequiredService<IDocumentSourceFactory>());
    }

    public static DocsReviewImportCommand ReviewImport(IDocsAnalysisCommandService service) =>
        new(service, Services().GetRequiredService<IFileSystem>());

    public static DocsExportGraphCommand ExportGraph() => new(Services().GetRequiredService<IManagedGlossaryFactory>());

    public static DocsRootRegistry Registry(string root, Func<string, DocsTools> factory)
    {
        IServiceProvider provider = Services();
        return new DocsRootRegistry(root, factory, provider.GetRequiredService<IGitWorktreeIdentity>(), provider.GetRequiredService<IFileSystem>());
    }

    public static IPacketHostFacts HostFacts => Services().GetRequiredService<IPacketHostFacts>();

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
