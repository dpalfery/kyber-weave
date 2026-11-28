using System.Diagnostics.CodeAnalysis;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Review;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Repository-backed implementation used by the parameterless CLI commands.</summary>
internal sealed class RepositoryDocsAnalysisCommandService : IDocsAnalysisCommandService
{
    private readonly DocsAnalysisCompositionFactories _factories;
    private readonly IManagedGlossaryFactory _glossaries;
    private readonly IDocumentSourceFactory _documents;
    private readonly IKyberWeaveConfigRepository _configs;

    public RepositoryDocsAnalysisCommandService(
        DocsAnalysisCompositionFactories factories,
        IManagedGlossaryFactory glossaries,
        IDocumentSourceFactory documents,
        IKyberWeaveConfigRepository configs)
    {
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _glossaries = glossaries ?? throw new ArgumentNullException(nameof(glossaries));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));
    }

    public async Task<DocumentationAnalysisResult> AnalyzeAsync(
        DocsIntegrityCheckSettings settings,
        CancellationToken cancellationToken = default)
    {
        using AnalysisExecution execution = await RunAnalysisAsync(settings, cancellationToken).ConfigureAwait(false);
        return execution.Result;
    }

    public async Task<ReviewExportResult> ExportReviewAsync(
        DocsReviewExportSettings settings,
        CancellationToken cancellationToken = default)
    {
        using AnalysisExecution execution = await RunAnalysisAsync(settings, cancellationToken).ConfigureAwait(false);
        ThrowIfOperational(execution.Result.Diagnostics);
        IAnalysisPersistence persistence = execution.Runtime.Persistence ?? UnavailableAnalysisPersistence.Instance;
        ReviewExportResult exported = new DocumentationReviewExchange(
            persistence,
            execution.Runtime.Config.DocsAnalysis.VerdictConfidence)
            .Export(execution.Result.Candidates);
        MergeDiagnostics(exported.Diagnostics, execution.Result.Diagnostics);
        return exported;
    }

    public async Task<ReviewImportResult> ImportReviewAsync(
        DocsReviewImportSettings settings,
        string json,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        using AnalysisExecution execution = await RunAnalysisAsync(settings, cancellationToken).ConfigureAwait(false);
        ThrowIfOperational(execution.Result.Diagnostics);
        IAnalysisPersistence persistence = execution.Runtime.Persistence ?? UnavailableAnalysisPersistence.Instance;
        ReviewImportResult imported = new DocumentationReviewExchange(
            persistence,
            execution.Runtime.Config.DocsAnalysis.VerdictConfidence)
            .Import(json, execution.Result.Candidates);
        MergeDiagnostics(imported.Diagnostics, execution.Result.Diagnostics);
        return imported;
    }

    public async Task<GlossaryUpdateResult> UpdateGlossaryAsync(
        DocsGlossarySettings settings,
        CancellationToken cancellationToken = default)
    {
        using AnalysisExecution execution = await RunAnalysisAsync(settings, cancellationToken).ConfigureAwait(false);
        ThrowIfOperational(execution.Result.Diagnostics);
        IReadOnlyList<GlossaryProposal> proposals = Proposals(execution.Result.Candidates);
        IManagedGlossary service = _glossaries.Open(execution.Runtime.RepositoryRoot, execution.Runtime.Config);
        GlossaryUpdateResult glossary = settings.Write ? service.Write(proposals) : service.Preview(proposals);
        MergeDiagnostics(glossary.Diagnostics, execution.Result.Diagnostics);
        return glossary;
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to AnalysisExecution on success and the catch path disposes on failure.")]
    private async Task<AnalysisExecution> RunAnalysisAsync(
        DocsSettings settings,
        CancellationToken cancellationToken)
    {
        DiagnosticReport compositionDiagnostics = new DiagnosticReport();
        DocsAnalysisRuntime? runtime = await DocsCommandComposition.TryCreateAnalysisRuntimeAsync(
            _configs,
            settings,
            compositionDiagnostics,
            _factories,
            cancellationToken).ConfigureAwait(false);
        if (runtime is null)
        {
            throw new InvalidOperationException(string.Join(
                " ",
                compositionDiagnostics.Items.Select(item => $"{item.Code}: {item.Message}")));
        }

        try
        {
            DocumentSet documents = _documents.Create(runtime.RepositoryRoot, runtime.Ontology).Load();
            DocGraphProjection graph = DocGraphProjectionBuilder.Build(
                documents,
                runtime.Resolver,
                runtime.Config.DocsAnalysis.Search.MaxCodeNeighbors);
            ManagedGlossaryLoadResult glossary = _glossaries.Open(runtime.RepositoryRoot, runtime.Config).Load();
            DocumentationAnalysisResult analyzed = await runtime.CreateAnalyzer().AnalyzeAsync(
                documents,
                graph,
                runtime.Config.DocsAnalysis,
                glossary.AnalysisGlossary,
                cancellationToken).ConfigureAwait(false);
            analyzed.Diagnostics.AddRange(compositionDiagnostics.Items);
            return new AnalysisExecution(runtime, analyzed);
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<GlossaryProposal> Proposals(
        IReadOnlyList<AnalysisCandidate> candidates)
    {
        List<GlossaryProposal> proposals = new List<GlossaryProposal>();
        foreach (AnalysisCandidate candidate in candidates.Where(candidate =>
                     candidate.Kind == AnalysisRuleKind.Terminology
                     && !string.IsNullOrWhiteSpace(candidate.Term)))
        {
            string term = candidate.Term
                ?? throw new InvalidOperationException("A terminology candidate has no term.");

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
                .GroupBy(claim => claim.Component, StringComparer.Ordinal)
                .Select(group => new GlossaryProposal(
                    term,
                    string.Empty,
                    [$"component:{group.Key}"],
                    [],
                    group.Select(claim => claim.ContentHash).ToArray())));
        }

        return proposals;
    }

    private static void ThrowIfOperational(DiagnosticReport diagnostics)
    {
        Diagnostic[] operational = diagnostics.Items.Where(item =>
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
        foreach (Diagnostic item in source.Items)
        {
            if (!target.Items.Contains(item)) target.Add(item);
        }

        foreach (KeyValuePair<string, object?> metric in source.Metrics)
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
