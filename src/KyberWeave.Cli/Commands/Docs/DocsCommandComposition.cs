using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Persistence;
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
        DocsSettings settings,
        DiagnosticReport report,
        out OntologyConfig ontology)
    {
        return TryResolveConfig(settings, report, out _, out ontology);
    }

    public static bool TryResolveConfig(
        DocsSettings settings,
        DiagnosticReport report,
        out KyberWeaveConfig config,
        out OntologyConfig ontology)
    {
        if (!CommandHelpers.TryLoadConfig(settings.Path, settings.Config, report, out config))
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
            config = new KyberWeaveConfig
            {
                Ontology = ontology,
                Harness = config.Harness,
                DocsAnalysis = config.DocsAnalysis.ResolveFor(ontology),
                Squad = config.Squad
            };
            return true;
        }
        catch (ArgumentException ex)
        {
            // A rejected --docs-root is the same class of operator error as a rejected
            // docs-root in the config file, and reports under the same code.
            report.Add(new Diagnostic(
                KyberWeaveConfigLoader.ConfigLoadErrorCode,
                Severity.Error,
                ex.Message,
                "--docs-root"));
            ontology = OntologyConfig.ProductDefaults;
            return false;
        }
    }

    public static bool TryCreateLoader(
        DocsSettings settings,
        DiagnosticReport report,
        out DocumentLoader? loader) =>
        TryCreateLoader(settings, report, out loader, out _);

    public static bool TryCreateLoader(
        DocsSettings settings,
        DiagnosticReport report,
        out DocumentLoader? loader,
        out OntologyConfig ontology)
        => TryCreateLoader(settings, report, out loader, out ontology, out _);

    public static bool TryCreateLoader(
        DocsSettings settings,
        DiagnosticReport report,
        out DocumentLoader? loader,
        out OntologyConfig ontology,
        out KyberWeaveConfig config)
    {
        if (!TryResolveConfig(settings, report, out config, out ontology))
        {
            loader = null;
            return false;
        }

        loader = new DocumentLoader(settings.Path, ontology);
        return true;
    }

    public static ICodeGraphResolver CreateResolver(DocsSettings settings) =>
        CodeGraphResolverAdapter.ForRepository(settings.Path);

    public static bool TryCreateAnalysisRuntime(
        DocsSettings settings,
        DiagnosticReport report,
        DocsAnalysisCompositionFactories factories,
        out DocsAnalysisRuntime? runtime)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(factories);

        runtime = null;
        if (!TryResolveConfig(settings, report, out KyberWeaveConfig config, out OntologyConfig ontology))
        {
            return false;
        }

        ICodeGraphResolver resolver = factories.CreateResolver(settings.Path);
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
        bool cacheSafe = factories.IsCacheSafe(settings.Path);
        if (!cacheSafe && mode != DocsAnalysisEmbeddingMode.Off)
        {
            AddEmbeddingAvailability(
                report,
                mode,
                "Analysis cache persistence is unsafe; no document text was sent to the embedding endpoint.");
            if (mode == DocsAnalysisEmbeddingMode.Required) return false;
        }

        IAnalysisPersistence? persistence = null;
        IEmbeddingGenerator? embeddingGenerator = null;
        if (cacheSafe)
        {
            persistence = factories.CreatePersistence(settings.Path);
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
                    if (mode == DocsAnalysisEmbeddingMode.Required) return false;
                }
            }
        }

        if (mode != DocsAnalysisEmbeddingMode.Off && persistence is not null)
            embeddingGenerator = factories.CreateEmbeddingGenerator();

        runtime = new DocsAnalysisRuntime(
            settings.Path,
            config,
            ontology,
            resolver,
            persistence,
            embeddingGenerator);
        return true;
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

internal sealed class DocsAnalysisCompositionFactories
{
    public Func<string, bool> IsCacheSafe { get; init; } = AnalysisCacheSafety.IsSafe;
    public Func<string, IAnalysisPersistence> CreatePersistence { get; init; } =
        repositoryRoot => new SqliteAnalysisPersistence(repositoryRoot);
    public Func<IEmbeddingGenerator> CreateEmbeddingGenerator { get; init; } =
        () => new OpenAiCompatibleEmbeddingGenerator();
    public Func<string, ICodeGraphResolver> CreateResolver { get; init; } =
        CodeGraphResolverAdapter.ForRepository;
}

internal sealed class DocsAnalysisRuntime : IDisposable
{
    private readonly IDisposable? _persistenceOwner;
    private readonly IDisposable? _embeddingOwner;
    private bool _disposed;

    public DocsAnalysisRuntime(
        string repositoryRoot,
        KyberWeaveConfig config,
        OntologyConfig ontology,
        ICodeGraphResolver resolver,
        IAnalysisPersistence? persistence,
        IEmbeddingGenerator? embeddingGenerator)
    {
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        Config = config;
        Ontology = ontology;
        Resolver = resolver;
        Persistence = persistence;
        EmbeddingGenerator = embeddingGenerator;
        _persistenceOwner = persistence as IDisposable;
        _embeddingOwner = embeddingGenerator as IDisposable;
    }

    public string RepositoryRoot { get; }
    public KyberWeaveConfig Config { get; }
    public OntologyConfig Ontology { get; }
    public ICodeGraphResolver Resolver { get; }
    public IAnalysisPersistence? Persistence { get; }
    public IEmbeddingGenerator? EmbeddingGenerator { get; }

    public DocumentationAnalyzer CreateAnalyzer() => new(
        new ClaimExtractor(),
        [new GraphClaimCandidateSource(), new SparseLexicalCandidateSource()],
        EmbeddingGenerator,
        Persistence);

    public void Dispose()
    {
        if (_disposed) return;
        _embeddingOwner?.Dispose();
        _persistenceOwner?.Dispose();
        _disposed = true;
    }
}
