using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Model;

namespace KyberWeave.Core.Docs.Analysis.Embeddings;

/// <summary>One contextual claim text requiring an embedding.</summary>
public sealed record EmbeddingWorkItem(string ContextualHash, string Input);

/// <summary>Provider-reported token counts for local cost visibility.</summary>
public sealed record EmbeddingUsage(int PromptTokens = 0, int TotalTokens = 0)
{
    public static EmbeddingUsage None { get; } = new();

    internal EmbeddingUsage Add(EmbeddingUsage other) =>
        new(checked(PromptTokens + other.PromptTokens), checked(TotalTokens + other.TotalTokens));
}

/// <summary>Validated provider vectors and aggregate usage for one generation request.</summary>
public sealed record EmbeddingGenerationResult(
    IReadOnlyList<StoredEmbedding> Embeddings,
    EmbeddingUsage Usage);

/// <summary>Cache-aware embedding resolution result.</summary>
public sealed record EmbeddingResolutionResult(
    IReadOnlyList<StoredEmbedding> Embeddings,
    DiagnosticReport Diagnostics,
    int CacheHits,
    int CacheMisses,
    EmbeddingUsage Usage)
{
    /// <summary>
    /// The resolution an analysis pass records when it cannot embed: a diagnostic that names
    /// the reason and no vectors.
    /// </summary>
    /// <param name="mode">The configured mode, which decides the diagnostic's severity.</param>
    /// <param name="reason">Why embeddings were skipped, as reported to the operator.</param>
    /// <returns>An empty resolution carrying one diagnostic.</returns>
    public static EmbeddingResolutionResult Unavailable(DocsAnalysisEmbeddingMode mode, string reason)
    {
        DiagnosticReport diagnostics = new DiagnosticReport();
        diagnostics.Add(new Diagnostic(
            DocumentationAnalysisRuleIds.EmbeddingUnavailableRuleCode,
            mode == DocsAnalysisEmbeddingMode.Required ? Severity.Error : Severity.Warning,
            $"Embedding analysis is unavailable: {reason}",
            "docs-analysis.embeddings",
            Hint: mode == DocsAnalysisEmbeddingMode.Required
                ? "Restore the configured local embedding endpoint and safe analysis cache, or set embeddings.mode to prefer/off."
                : "Lexical analysis will continue without embeddings."));
        return new EmbeddingResolutionResult([], diagnostics, 0, 0, EmbeddingUsage.None);
    }
}
