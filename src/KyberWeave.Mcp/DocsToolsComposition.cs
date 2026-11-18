using KyberWeave.Application;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
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

    /// <summary>
    /// Registers the root-bound services the server's tools resolve, and the registry that serves a per-call
    /// <c>projectPath</c>. <c>Program.cs</c> and the composition-root test both build the server from these, so a
    /// tool constructor that asks for an unregistered service fails the test rather than the first tool call.
    /// </summary>
    /// <param name="services">The collection to populate; <see cref="AddWeaveApplication"/> comes first.</param>
    /// <param name="repositoryRoot">The checkout this process serves.</param>
    /// <param name="codeGraphRefreshEnabled">False when the process runs with <c>--no-codegraph-refresh</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddDocsHostServices(
        this IServiceCollection services,
        string repositoryRoot,
        bool codeGraphRefreshEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        services.AddSingleton(provider =>
        {
            // Read through the container's configuration repository once, at first resolution, so the
            // corpus this process serves is the one its configuration names.
            OntologyConfig ontology = DocsRootRegistry.ResolveOntology(repositoryRoot, provider);
            return new DocumentIndexHost(
                repositoryRoot,
                () => provider.GetRequiredService<ICodeGraphPortsFactory>().ForRepository(repositoryRoot).Resolver,
                () => provider.GetRequiredService<IDocumentSourceFactory>().Create(repositoryRoot, ontology).Load(),
                ontology.DocsRoots,
                ontology.ResolvedCatalogPath);
        });
        services.AddSingleton<IDocsAnalysisReader>(provider => AnalysisReader(provider, repositoryRoot));

        // A tool constructor parameter has to be a service the container resolves: the SDK builds each tool type from
        // the container on every call, so a bare root string would make every tool call fail.
        services.AddSingleton(provider => HostRoot(provider, repositoryRoot));

        // The refresh schedule is a process-wide decision, made once from this process's own argv. The
        // tools receive it by injection so a test that asks them directly gets the default cadence.
        services.AddSingleton<ICodeGraphFreshnessGate>(provider => Freshness(provider, repositoryRoot, codeGraphRefreshEnabled));
        services.AddSingleton<IDocsGateRunner, DocsGateRunner>();
        services.AddSingleton<IDocsQueryService, DocsQueryService>();
        services.AddSingleton<IPlanLifecycleService>(provider => new PlanLifecycleService(
            repositoryRoot,
            provider.GetRequiredService<IDocsQueryService>(),
            provider.GetRequiredService<IDocsHostConfiguration>(),
            provider.GetRequiredService<IPlanFinalizeService>(),
            provider.GetRequiredService<IPlanCloseoutService>(),
            provider.GetRequiredService<IRepositoryFileStore>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IRunDeliveryService>(provider => new RunDeliveryService(
            repositoryRoot,
            provider.GetRequiredService<IDocsQueryService>(),
            provider.GetRequiredService<IDocsHostConfiguration>(),
            provider.GetRequiredService<RunDeliveryPorts>()));
        services.AddSingleton<IToolingCheckService>(provider => new ToolingCheckService(
            repositoryRoot,
            provider.GetRequiredService<IDocsQueryService>(),
            provider.GetRequiredService<IRepositoryDoctor>(),
            provider.GetRequiredService<IBoundedProcessExecutor>(),
            provider.GetRequiredService<IProcessEnvironment>()));

        // The dispatch tool asks for this one directly, and the SDK constructs the tool from the container on each
        // call, so an unregistered parameter is an invocation error every client sees rather than a startup failure.
        services.AddSingleton<ITaskPromptService>(provider =>
            Prompts(provider, provider.GetRequiredService<DocumentIndexHost>()));

        // A tool call may name another worktree of this repository with projectPath. Those are served by
        // tools of their own, built on demand and kept in a small least-recently-used set.
        services.AddSingleton(provider => DocsRootRegistry.ForRepository(repositoryRoot, codeGraphRefreshEnabled, provider));
        return services;
    }

    /// <summary>The root the tools answer for, as a full path resolved against the process working directory.</summary>
    /// <param name="provider">The container the path and environment seams are resolved from.</param>
    /// <param name="repositoryRoot">The checkout root, as the caller spelled it.</param>
    public static DocsHostRoot HostRoot(IServiceProvider provider, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new DocsHostRoot(provider.GetRequiredService<IPathResolver>()
            .GetFullPath(repositoryRoot, provider.GetRequiredService<IProcessEnvironment>().CurrentDirectory));
    }

    /// <summary>The CodeGraph refresh schedule over <paramref name="repositoryRoot"/>, built from the ports of <paramref name="provider"/>.</summary>
    /// <param name="provider">The container the ports are resolved from.</param>
    /// <param name="repositoryRoot">The bound repository root.</param>
    /// <param name="refreshEnabled">False when the process runs with <c>--no-codegraph-refresh</c>.</param>
    public static CodeGraphFreshness Freshness(IServiceProvider provider, string repositoryRoot, bool refreshEnabled)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new CodeGraphFreshness(
            repositoryRoot,
            refreshEnabled,
            provider.GetRequiredService<ICodeGraphProvisioner>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IPathResolver>(),
            provider.GetRequiredService<IProcessEnvironment>(),
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IBoundedProcessExecutor>());
    }

    /// <summary>The task prompt service over <paramref name="host"/>'s checkout, built from the ports of <paramref name="provider"/>.</summary>
    /// <param name="provider">The container the ports are resolved from.</param>
    /// <param name="host">The corpus whose standards the prompt carries, and the root it is bound to.</param>
    public static ITaskPromptService Prompts(IServiceProvider provider, DocumentIndexHost host)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(host);
        return new TaskPromptService(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IFileSystem>(),
            technology => host.Current().StandardsFor(technology));
    }

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
            provider.GetRequiredService<IDocumentSourceFactory>(),
            provider.GetRequiredService<IPathResolver>(),
            provider.GetRequiredService<IProcessEnvironment>());
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
            Prompts(provider, host),
            hostConfiguration,
            HostRoot(provider, host.RepositoryRoot),
            roots);
    }
}
