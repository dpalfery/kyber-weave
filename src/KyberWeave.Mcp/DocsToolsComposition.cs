using KyberWeave.Application;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Mcp;

/// <summary>
/// Builds the tool adapter and the root-bound services behind it for one checkout, from the container's shared
/// collaborators. The server root's tools come from the host container; every other worktree a call names is composed
/// here, on demand, from the same container.
/// </summary>
internal static class DocsToolsComposition
{
    private static readonly Lazy<IServiceProvider> DefaultServices = new(
        () => new ServiceCollection().AddWeaveApplication().BuildServiceProvider());

    /// <summary>The container the shared collaborators are resolved from: <paramref name="services"/>, or the default one.</summary>
    /// <param name="services">The caller's container, or null.</param>
    public static IServiceProvider ServicesOrDefault(IServiceProvider? services) => services ?? DefaultServices.Value;

    /// <summary>The analysis reader over <paramref name="repositoryRoot"/>, built from the ports of <paramref name="provider"/>.</summary>
    /// <param name="provider">The container the ports are resolved from.</param>
    /// <param name="repositoryRoot">The bound repository root.</param>
    public static RepositoryDocsAnalysisReader AnalysisReader(IServiceProvider provider, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new RepositoryDocsAnalysisReader(
            repositoryRoot,
            provider.GetRequiredService<IDocsHostConfiguration>(),
            provider.GetRequiredService<ICodeGraphPortsFactory>(),
            provider.GetRequiredService<IManagedGlossaryFactory>(),
            provider.GetRequiredService<IDocumentSourceFactory>());
    }

    /// <summary>The tools over <paramref name="host"/>.</summary>
    /// <param name="host">The corpus the tools answer from.</param>
    /// <param name="analysisReader">The analysis port, or null when this host has no analysis surface.</param>
    /// <param name="freshness">The CodeGraph refresh schedule, or null for none: answers come from the documents on disk.</param>
    /// <param name="roots">Resolves a per-call <c>projectPath</c>, or null when this host serves one root only.</param>
    /// <param name="services">The container the shared collaborators are resolved from, or null for the default one.</param>
    /// <param name="deliveryExecutor">Runs git and gh for the deliver tool, or null for the bounded default.</param>
    /// <param name="deliveryHome">The home directory holding the run records, or null for the user profile.</param>
    /// <param name="toolingExecutor">Runs git and CodeGraph for the tooling-check tool, or null for the bounded default.</param>
    /// <param name="toolingMcpExecutable">The docs server executable the tooling check proves, or null for the one on <c>PATH</c>.</param>
    public static DocsTools Create(
        DocumentIndexHost host,
        IDocsAnalysisReader? analysisReader = null,
        CodeGraphFreshness? freshness = null,
        DocsRootRegistry? roots = null,
        IServiceProvider? services = null,
        IProcessExecutor? deliveryExecutor = null,
        string? deliveryHome = null,
        IProcessExecutor? toolingExecutor = null,
        string? toolingMcpExecutable = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        IServiceProvider provider = ServicesOrDefault(services);
        IDocsHostConfiguration hostConfiguration = provider.GetRequiredService<IDocsHostConfiguration>();
        DocsQueryService query = new(
            host,
            analysisReader,
            freshness,
            new DocsGateRunner(
                hostConfiguration,
                provider.GetRequiredService<IDocsSchemaGate>(),
                provider.GetRequiredService<IDocumentSourceFactory>()),
            provider.GetRequiredService<ICorpusProvenance>());
        return new DocsTools(
            query,
            new PlanLifecycleService(
                host.RepositoryRoot,
                query,
                hostConfiguration,
                provider.GetRequiredService<IPlanFinalizeService>(),
                provider.GetRequiredService<IPlanCloseoutService>(),
                provider.GetRequiredService<IRepositoryFileStore>(),
                provider.GetRequiredService<TimeProvider>()),
            new RunDeliveryService(host.RepositoryRoot, query, hostConfiguration, provider.GetRequiredService<RunDeliveryPorts>())
            {
                DeliveryExecutor = deliveryExecutor,
                DeliveryHome = deliveryHome,
            },
            new ToolingCheckService(
                host.RepositoryRoot,
                query,
                provider.GetRequiredService<IRepositoryDoctor>(),
                provider.GetRequiredService<IBoundedProcessExecutor>(),
                provider.GetRequiredService<IProcessEnvironment>())
            {
                Executor = toolingExecutor,
                McpExecutable = toolingMcpExecutable,
            },
            roots);
    }
}
