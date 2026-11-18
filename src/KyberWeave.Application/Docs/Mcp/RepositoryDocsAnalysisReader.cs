using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Mcp;

/// <summary>
/// Composes the read-only documentation analyzer from the repository's current config.
/// </summary>
/// <remarks>
/// Configuration and documents are loaded for each call so a long-lived MCP process sees
/// edits without owning a second staleness cache. The persistence adapter remains responsible
/// for refusing tracked or otherwise unsafe cache paths before embeddings can send prose.
/// </remarks>
public sealed class RepositoryDocsAnalysisReader : IDocsAnalysisReader
{
    private readonly string _repositoryRoot;
    private readonly IDocsHostConfiguration _hostConfiguration;
    private readonly ICodeGraphPortsFactory _codeGraph;
    private readonly IManagedGlossaryFactory _glossaries;
    private readonly IDocumentSourceFactory _documents;

    /// <summary>A reader over <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The bound repository root.</param>
    /// <param name="hostConfiguration">Loads the repository's configuration for each call.</param>
    /// <param name="codeGraph">Opens the CodeGraph index of the repository.</param>
    /// <param name="glossaries">Opens the managed glossary of the repository.</param>
    /// <param name="documents">Opens the documentation corpus of the repository.</param>
    /// <param name="paths">Resolves the repository root to an absolute path.</param>
    /// <param name="environment">Supplies the current directory a relative root resolves against.</param>
    public RepositoryDocsAnalysisReader(
        string repositoryRoot,
        IDocsHostConfiguration hostConfiguration,
        ICodeGraphPortsFactory codeGraph,
        IManagedGlossaryFactory glossaries,
        IDocumentSourceFactory documents,
        IPathResolver paths,
        IProcessEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        _repositoryRoot = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        _hostConfiguration = hostConfiguration ?? throw new ArgumentNullException(nameof(hostConfiguration));
        _codeGraph = codeGraph ?? throw new ArgumentNullException(nameof(codeGraph));
        _glossaries = glossaries ?? throw new ArgumentNullException(nameof(glossaries));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
    }

    /// <inheritdoc />
    public DocumentationAnalysisResult Analyze(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KyberWeaveConfig config = LoadConfig();
        DocumentSet documents = _documents.Create(_repositoryRoot, config.Ontology).Load();
        cancellationToken.ThrowIfCancellationRequested();
        ICodeGraphResolver codeGraph = _codeGraph.ForRepository(_repositoryRoot).Resolver;
        DocGraphProjection graph = DocGraphProjectionBuilder.Build(
            documents,
            codeGraph,
            config.DocsAnalysis.Search.MaxCodeNeighbors);
        cancellationToken.ThrowIfCancellationRequested();
        SqliteAnalysisPersistence persistence = new SqliteAnalysisPersistence(_repositoryRoot);
        using OpenAiCompatibleEmbeddingGenerator? embeddingGenerator = config.DocsAnalysis.Embeddings.Mode == DocsAnalysisEmbeddingMode.Off
            ? null
            : new OpenAiCompatibleEmbeddingGenerator();
        IEmbeddingResolver? embeddingResolver = embeddingGenerator is null
            ? null
            : new EmbeddingCoordinator(embeddingGenerator, persistence);
        DocumentationAnalyzer analyzer = new DocumentationAnalyzer(
            new ClaimExtractor(),
            [new GraphClaimCandidateSource(), new SparseLexicalCandidateSource()],
            embeddingResolver,
            persistence);
        ManagedGlossaryLoadResult glossary = _glossaries.Open(_repositoryRoot, config).Load();

        DocumentationAnalysisResult result = analyzer.Analyze(
            documents,
            graph,
            config.DocsAnalysis,
            glossary.AnalysisGlossary,
            cancellationToken);
        AddCodeGraphUnavailable(result.Diagnostics, codeGraph);
        return result;
    }

    /// <inheritdoc />
    public GlossaryLookupResult LookupGlossary(string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        KyberWeaveConfig config = LoadConfig();
        return _glossaries.Open(_repositoryRoot, config).Lookup(term);
    }

    private KyberWeaveConfig LoadConfig()
    {
        DiagnosticReport report = new();
        if (_hostConfiguration.TryLoad(_repositoryRoot, report, out KyberWeaveConfig config))
        {
            return config;
        }

        Diagnostic failure = report.Items[0];
        throw new InvalidDataException(
            $"{failure.Code}: Failed to load " +
            $"'{failure.FilePath ?? "kyber-weave.yml"}': {failure.Message}.");
    }

    private static void AddCodeGraphUnavailable(
        DiagnosticReport diagnostics,
        ICodeGraphResolver codeGraph)
    {
        if (codeGraph.IsAvailable || diagnostics.Items.Any(item =>
                item.Code == DocumentationAnalyzer.CodeGraphUnavailableRuleCode))
        {
            return;
        }

        diagnostics.Add(new Diagnostic(
            DocumentationAnalyzer.CodeGraphUnavailableRuleCode,
            Severity.Warning,
            codeGraph.UnavailableReason ?? "The CodeGraph index is unavailable.",
            "CodeGraph",
            codeGraph.DatabasePath,
            "Analysis continues with document relationships and bounded lexical search."));
    }
}
