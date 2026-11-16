using KyberWeave.Application;
using KyberWeave.Contracts.Docs;
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

    /// <summary>The tools over <paramref name="host"/>.</summary>
    /// <param name="host">The corpus the tools answer from.</param>
    /// <param name="analysisReader">The analysis port, or null when this host has no analysis surface.</param>
    /// <param name="freshness">The CodeGraph refresh schedule, or null for none: answers come from the documents on disk.</param>
    /// <param name="roots">Resolves a per-call <c>projectPath</c>, or null when this host serves one root only.</param>
    /// <param name="services">The container the shared collaborators are resolved from, or null for the default one.</param>
    /// <param name="deliveryExecutor">Runs git and gh for the deliver tool, or null for the bounded default.</param>
    /// <param name="deliveryHome">The home directory holding the run records, or null for the user profile.</param>
    public static DocsTools Create(
        DocumentIndexHost host,
        IDocsAnalysisReader? analysisReader = null,
        CodeGraphFreshness? freshness = null,
        DocsRootRegistry? roots = null,
        IServiceProvider? services = null,
        IProcessExecutor? deliveryExecutor = null,
        string? deliveryHome = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        IServiceProvider provider = services ?? DefaultServices.Value;
        DocsQueryService query = new(
            host,
            analysisReader,
            freshness,
            new DocsGateRunner(),
            provider.GetRequiredService<ICorpusProvenance>());
        return new DocsTools(
            query,
            new PlanLifecycleService(host.RepositoryRoot, query),
            new RunDeliveryService(host.RepositoryRoot, query)
            {
                DeliveryExecutor = deliveryExecutor,
                DeliveryHome = deliveryHome,
            },
            roots);
    }
}
