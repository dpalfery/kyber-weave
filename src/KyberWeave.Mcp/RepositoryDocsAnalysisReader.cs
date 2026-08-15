using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;

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

    public RepositoryDocsAnalysisReader(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
    }

    /// <inheritdoc />
    public DocumentationAnalysisResult Analyze(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KyberWeaveConfig config = LoadConfig();
        DocumentSet documents = new DocumentLoader(_repositoryRoot, config.Ontology).Load();
        cancellationToken.ThrowIfCancellationRequested();
        CodeGraphResolverAdapter codeGraph = CodeGraphResolverAdapter.ForRepository(_repositoryRoot);
        DocGraphProjection graph = DocGraphProjection.Build(
            documents,
            codeGraph,
            config.DocsAnalysis.Search.MaxCodeNeighbors);
        cancellationToken.ThrowIfCancellationRequested();
        SqliteAnalysisPersistence persistence = new SqliteAnalysisPersistence(_repositoryRoot);
        using OpenAiCompatibleEmbeddingGenerator? embeddingGenerator = config.DocsAnalysis.Embeddings.Mode == DocsAnalysisEmbeddingMode.Off
            ? null
            : new OpenAiCompatibleEmbeddingGenerator();
        DocumentationAnalyzer analyzer = new DocumentationAnalyzer(
            new ClaimExtractor(),
            [new GraphClaimCandidateSource(), new SparseLexicalCandidateSource()],
            embeddingGenerator,
            persistence);
        ManagedGlossaryLoadResult glossary = new ManagedGlossaryService(
            _repositoryRoot,
            config,
            TimeProvider.System).Load();

        DocumentationAnalysisResult result = analyzer.Analyze(
            documents,
            graph,
            config.DocsAnalysis,
            glossary.AnalysisGlossary);
        AddCodeGraphUnavailable(result.Diagnostics, codeGraph);
        return result;
    }

    /// <inheritdoc />
    public GlossaryLookupResult LookupGlossary(string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        KyberWeaveConfig config = LoadConfig();
        return new ManagedGlossaryService(
            _repositoryRoot,
            config,
            TimeProvider.System).Lookup(term);
    }

    private KyberWeaveConfig LoadConfig()
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(_repositoryRoot);
        if (loaded.Success && loaded.Config is not null) return loaded.Config;

        throw new InvalidDataException(
            $"{KyberWeaveConfigLoader.ConfigLoadErrorCode}: Failed to load " +
            $"'{loaded.ConfigPath ?? "kyber-weave.yml"}': {loaded.Error ?? "unknown error"}.");
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
