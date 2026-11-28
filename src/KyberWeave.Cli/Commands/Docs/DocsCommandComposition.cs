using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Parsing;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// CLI composition root for docs commands: constructs injectable collaborators once from
/// resolved settings. Core types never invent <see cref="DocumentLoader"/> or
/// <see cref="ICodeGraphResolver"/> implementations.
/// </summary>
internal static class DocsCommandComposition
{
    public static bool TryResolveOntology(
        IKyberWeaveConfigRepository configs,
        DocsSettings settings,
        DiagnosticReport report,
        out OntologyConfig ontology)
    {
        return TryResolveConfig(configs, settings, report, out _, out ontology);
    }

    public static bool TryResolveConfig(
        IKyberWeaveConfigRepository configs,
        DocsSettings settings,
        DiagnosticReport report,
        out KyberWeaveConfig config,
        out OntologyConfig ontology)
    {
        if (!CommandHelpers.TryLoadConfig(configs, settings.Path, settings.Config, report, out config))
        {
            ontology = OntologyConfig.ProductDefaults;
            return false;
        }

        // An unsupplied option leaves the configured roots alone; a supplied one replaces
        // them outright. The flag has no default of its own, so it cannot be confused with
        // the product default and silently ignored on a repository configured otherwise.
        OntologyConfig loaded = config.Ontology;
        if (settings.DocsRoots.Length == 0)
        {
            ontology = loaded;
            return true;
        }

        try
        {
            ontology = loaded.WithDocsRoots(settings.DocsRoots);

            // Only the roots change. Rebuilding the config by hand would silently reset every
            // setting the object does not name — the review policy, MCP grants, the tool-failure
            // and worktree settings, the roster, the prompt budget, the Arbiter and the registry.
            config = config
                .WithOntology(ontology)
                .WithDocsAnalysis(config.DocsAnalysis.ResolveFor(ontology));
            return true;
        }
        catch (ArgumentException ex)
        {
            // A rejected --docs-root is the same class of operator error as a rejected
            // docs-root in the config file, and reports under the same code.
            report.Add(new Diagnostic(
                KyberWeaveConfigDiagnosticCodes.LoadError,
                Severity.Error,
                ex.Message,
                "--docs-root"));
            ontology = OntologyConfig.ProductDefaults;
            return false;
        }
    }

    public static bool TryCreateLoader(
        IKyberWeaveConfigRepository configs,
        DocsSettings settings,
        DiagnosticReport report,
        IDocumentSourceFactory documents,
        out IDocumentSource? loader) =>
        TryCreateLoader(configs, settings, report, documents, out loader, out _);

    public static bool TryCreateLoader(
        IKyberWeaveConfigRepository configs,
        DocsSettings settings,
        DiagnosticReport report,
        IDocumentSourceFactory documents,
        out IDocumentSource? loader,
        out KyberWeaveConfig config)
    {
        if (!TryResolveConfig(configs, settings, report, out config, out OntologyConfig ontology))
        {
            loader = null;
            return false;
        }

        loader = documents.Create(settings.Path, ontology);
        return true;
    }

    public static async Task<ICodeGraphResolver> CreateResolverAsync(
        DocsSettings settings,
        ICodeGraphPortsFactory codeGraph,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(codeGraph);
        CodeGraphPorts ports = await codeGraph.ForRepositoryAsync(settings.Path, cancellationToken).ConfigureAwait(false);
        return ports.Resolver;
    }

    public static async Task<DocsAnalysisRuntime?> TryCreateAnalysisRuntimeAsync(
        IKyberWeaveConfigRepository configs,
        DocsSettings settings,
        DiagnosticReport report,
        DocsAnalysisCompositionFactories factories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(factories);

        if (!TryResolveConfig(configs, settings, report, out KyberWeaveConfig config, out OntologyConfig ontology))
        {
            return null;
        }

        ICodeGraphResolver resolver = await factories.CreateResolverAsync(settings.Path, cancellationToken).ConfigureAwait(false);
        if (!resolver.IsAvailable)
        {
            report.Add(new Diagnostic(
                DocumentationAnalyzer.CodeGraphUnavailableRuleCode,
                Severity.Warning,
                resolver.UnavailableReason ?? "The CodeGraph index is unavailable.",
                "CodeGraph",
                resolver.DatabasePath,
                "Analysis continues with document relationships and bounded lexical search."));
        }

        DocsAnalysisEmbeddingMode mode = config.DocsAnalysis.Embeddings.Mode;
        bool cacheSafe = await factories.IsCacheSafeAsync(settings.Path, cancellationToken).ConfigureAwait(false);
        if (!cacheSafe && mode != DocsAnalysisEmbeddingMode.Off)
        {
            AddEmbeddingAvailability(
                report,
                mode,
                "Analysis cache persistence is unsafe; no document text was sent to the embedding endpoint.");
            if (mode == DocsAnalysisEmbeddingMode.Required) return null;
        }

        IAnalysisPersistence? persistence = null;
        IEmbeddingGenerator? embeddingGenerator = null;
        if (cacheSafe)
        {
            persistence = await factories.CreatePersistenceAsync(settings.Path, cancellationToken).ConfigureAwait(false);
            if (!persistence.IsAvailable)
            {
                DisposeIfOwned(persistence);
                persistence = null;
                if (mode != DocsAnalysisEmbeddingMode.Off)
                {
                    AddEmbeddingAvailability(
                        report,
                        mode,
                        "The local analysis cache is unavailable; embeddings were not constructed or called.");
                    if (mode == DocsAnalysisEmbeddingMode.Required) return null;
                }
            }
        }

        if (mode != DocsAnalysisEmbeddingMode.Off && persistence is not null)
            embeddingGenerator = factories.CreateEmbeddingGenerator();

        return new DocsAnalysisRuntime(
            settings.Path,
            config,
            ontology,
            resolver,
            persistence,
            embeddingGenerator);
    }

    private static void AddEmbeddingAvailability(
        DiagnosticReport report,
        DocsAnalysisEmbeddingMode mode,
        string message) =>
        report.Add(new Diagnostic(
            DocumentationAnalyzer.EmbeddingUnavailableRuleCode,
            mode == DocsAnalysisEmbeddingMode.Required ? Severity.Error : Severity.Warning,
            message,
            "embeddings",
            Hint: mode == DocsAnalysisEmbeddingMode.Required
                ? "Create the narrow .kyber-weave/.gitignore cache entry and ensure sqlite3 is available."
                : "Lexical analysis remains active; configure safe persistence to enable embeddings."));

    private static void DisposeIfOwned(object value)
    {
        if (value is IDisposable disposable) disposable.Dispose();
    }
}

internal sealed class DocsAnalysisCompositionFactories(
    ICodeGraphPortsFactory codeGraph,
    IAnalysisPersistenceFactory persistence,
    IEmbeddingGeneratorFactory embeddings)
{
    public Func<string, CancellationToken, Task<bool>> IsCacheSafeAsync { get; init; } = persistence.IsCacheSafeAsync;
    public Func<string, CancellationToken, Task<IAnalysisPersistence>> CreatePersistenceAsync { get; init; } = persistence.OpenAsync;
    public Func<IEmbeddingGenerator> CreateEmbeddingGenerator { get; init; } = embeddings.Create;
    public Func<string, CancellationToken, Task<ICodeGraphResolver>> CreateResolverAsync { get; init; } =
        async (repositoryRoot, cancellationToken) =>
            (await codeGraph.ForRepositoryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false)).Resolver;
}

internal sealed class DocsAnalysisRuntime(
    string repositoryRoot,
    KyberWeaveConfig config,
    OntologyConfig ontology,
    ICodeGraphResolver resolver,
    IAnalysisPersistence? persistence,
    IEmbeddingGenerator? embeddingGenerator) : IDisposable
{
    private readonly IDisposable? _persistenceOwner = persistence as IDisposable;
    private readonly IDisposable? _embeddingOwner = embeddingGenerator as IDisposable;
    private bool _disposed;

    public string RepositoryRoot { get; } = Path.GetFullPath(repositoryRoot);
    public KyberWeaveConfig Config { get; } = config;
    public OntologyConfig Ontology { get; } = ontology;
    public ICodeGraphResolver Resolver { get; } = resolver;
    public IAnalysisPersistence? Persistence { get; } = persistence;
    private IEmbeddingGenerator? EmbeddingGenerator { get; } = embeddingGenerator;

    public DocumentationAnalyzer CreateAnalyzer() => new(
        new ClaimExtractor(),
        [new GraphClaimCandidateSource(), new SparseLexicalCandidateSource()],
        EmbeddingGenerator is null || Persistence is null
            ? null
            : new EmbeddingCoordinator(EmbeddingGenerator, Persistence),
        Persistence);

    public void Dispose()
    {
        if (_disposed) return;
        _embeddingOwner?.Dispose();
        _persistenceOwner?.Dispose();
        _disposed = true;
    }
}
