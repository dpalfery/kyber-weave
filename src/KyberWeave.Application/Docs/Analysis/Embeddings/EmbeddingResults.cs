using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Model;

namespace KyberWeave.Core.Docs.Analysis.Embeddings;

/// <summary>Factory methods for the resolution an embedding pass records.</summary>
internal static class EmbeddingResolutionResults
{
    /// <summary>
    /// The resolution an analysis pass records when it cannot embed: a diagnostic that names
    /// the reason and no vectors.
    /// </summary>
    /// <param name="mode">The configured mode, which decides the diagnostic's severity.</param>
    /// <param name="reason">Why embeddings were skipped, as reported to the operator.</param>
    /// <returns>An empty resolution carrying one diagnostic.</returns>
    internal static EmbeddingResolutionResult Unavailable(DocsAnalysisEmbeddingMode mode, string reason)
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
