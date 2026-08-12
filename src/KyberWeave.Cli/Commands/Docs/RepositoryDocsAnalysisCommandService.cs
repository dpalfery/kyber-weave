using System.Diagnostics.CodeAnalysis;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Review;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Parsing;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Repository-backed implementation used by the parameterless CLI commands.</summary>
internal sealed class RepositoryDocsAnalysisCommandService : IDocsAnalysisCommandService
{
    private readonly DocsAnalysisCompositionFactories _factories;

    public RepositoryDocsAnalysisCommandService()
        : this(new DocsAnalysisCompositionFactories()) { }

    internal RepositoryDocsAnalysisCommandService(DocsAnalysisCompositionFactories factories) =>
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));

    public DocumentationAnalysisResult Analyze(DocsAnalyzeSettings settings)
    {
        using var execution = RunAnalysis(settings);
        return execution.Result;
    }

    public ReviewExportResult ExportReview(DocsReviewExportSettings settings)
    {
        using var execution = RunAnalysis(settings);
        ThrowIfOperational(execution.Result.Diagnostics);
        var persistence = execution.Runtime.Persistence ?? UnavailableAnalysisPersistence.Instance;
        var exported = new DocumentationReviewExchange(
            persistence,
            execution.Runtime.Config.DocsAnalysis.VerdictConfidence)
            .Export(execution.Result.Candidates);
        MergeDiagnostics(exported.Diagnostics, execution.Result.Diagnostics);
        return exported;
    }

    public ReviewImportResult ImportReview(DocsReviewImportSettings settings, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var execution = RunAnalysis(settings);
        ThrowIfOperational(execution.Result.Diagnostics);
        var persistence = execution.Runtime.Persistence ?? UnavailableAnalysisPersistence.Instance;
        var imported = new DocumentationReviewExchange(
            persistence,
            execution.Runtime.Config.DocsAnalysis.VerdictConfidence)
            .Import(json, execution.Result.Candidates);
        MergeDiagnostics(imported.Diagnostics, execution.Result.Diagnostics);
        return imported;
    }

    public GlossaryUpdateResult UpdateGlossary(DocsGlossarySettings settings)
    {
        using var execution = RunAnalysis(settings);
        ThrowIfOperational(execution.Result.Diagnostics);
        var proposals = Proposals(execution.Result.Candidates);
        var service = new ManagedGlossaryService(
            execution.Runtime.RepositoryRoot,
            execution.Runtime.Config,
            TimeProvider.System);
        var glossary = settings.Write ? service.Write(proposals) : service.Preview(proposals);
        MergeDiagnostics(glossary.Diagnostics, execution.Result.Diagnostics);
        return glossary;
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to AnalysisExecution on success and the catch path disposes on failure.")]
    private AnalysisExecution RunAnalysis(DocsSettings settings)
    {
        var compositionDiagnostics = new DiagnosticReport();
        if (!DocsCommandComposition.TryCreateAnalysisRuntime(
                settings,
                compositionDiagnostics,
                _factories,
                out var runtime))
        {
            throw new InvalidOperationException(string.Join(
                " ",
                compositionDiagnostics.Items.Select(item => $"{item.Code}: {item.Message}")));
        }

        var created = runtime ?? throw new InvalidOperationException(
            "Analysis composition succeeded without creating a runtime.");

        try
        {
            var documents = new DocumentLoader(created.RepositoryRoot, created.Ontology).Load();
            var graph = DocGraphProjection.Build(
                documents,
                created.Resolver,
                created.Config.DocsAnalysis.Search.MaxCodeNeighbors);
            var glossary = new ManagedGlossaryService(
                created.RepositoryRoot,
                created.Config,
                TimeProvider.System).Load();
            var analyzed = created.CreateAnalyzer().Analyze(
                documents,
                graph,
                created.Config.DocsAnalysis,
                glossary.AnalysisGlossary);
            analyzed.Diagnostics.AddRange(compositionDiagnostics.Items);
            return new AnalysisExecution(created, analyzed);
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<GlossaryProposal> Proposals(
        IReadOnlyList<AnalysisCandidate> candidates)
    {
        var proposals = new List<GlossaryProposal>();
        foreach (var candidate in candidates.Where(candidate =>
                     candidate.Kind == AnalysisRuleKind.Terminology
                     && !string.IsNullOrWhiteSpace(candidate.Term)))
        {
            if (candidate.Verdict?.ProposedGlossarySenses is { Count: > 0 } reviewed)
            {
                proposals.AddRange(reviewed.Select(sense => new GlossaryProposal(
                    sense.Term,
                    sense.Definition,
                    sense.Scopes,
                    sense.Aliases,
                    candidate.Claims.Select(claim => claim.ContentHash).ToArray())));
                continue;
            }

            proposals.AddRange(candidate.Claims
                .Where(claim => !string.IsNullOrWhiteSpace(claim.Component))
                .GroupBy(claim => claim.Component!, StringComparer.Ordinal)
                .Select(group => new GlossaryProposal(
                    candidate.Term!,
                    string.Empty,
                    [$"component:{group.Key}"],
                    [],
                    group.Select(claim => claim.ContentHash).ToArray())));
        }

        return proposals;
    }

    private static void ThrowIfOperational(DiagnosticReport diagnostics)
    {
        var operational = diagnostics.Items.Where(item =>
            item.Severity is Severity.Error or Severity.Critical
            && item.Code is (
                DocumentationAnalyzer.IgnoreMarkupRuleCode or
                DocumentationAnalyzer.EmbeddingUnavailableRuleCode)).ToArray();
        if (operational.Length == 0) return;

        throw new InvalidOperationException(string.Join(
            " ",
            operational.Select(item => $"{item.Code}: {item.Message}")));
    }

    private static void MergeDiagnostics(DiagnosticReport target, DiagnosticReport source)
    {
        foreach (var item in source.Items)
        {
            if (!target.Items.Contains(item)) target.Add(item);
        }

        foreach (var metric in source.Metrics)
        {
            target.AddMetric(metric.Key, metric.Value);
        }
    }

    private sealed record AnalysisExecution(
        DocsAnalysisRuntime Runtime,
        DocumentationAnalysisResult Result) : IDisposable
    {
        public void Dispose() => Runtime.Dispose();
    }

    private sealed class UnavailableAnalysisPersistence : IAnalysisPersistence
    {
        public static UnavailableAnalysisPersistence Instance { get; } = new();
        public bool IsAvailable => false;

        public IReadOnlyDictionary<string, AnalysisVerdict> LoadVerdicts(
            IReadOnlyCollection<string> candidateIds) =>
            new Dictionary<string, AnalysisVerdict>(StringComparer.Ordinal);

        public IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> LoadEmbeddings(
            IReadOnlyCollection<EmbeddingCacheKey> keys) =>
            new Dictionary<EmbeddingCacheKey, StoredEmbedding>();

        public void SaveEmbeddings(IReadOnlyCollection<StoredEmbedding> embeddings) =>
            throw new InvalidOperationException("The analysis cache is unavailable.");
    }
}
