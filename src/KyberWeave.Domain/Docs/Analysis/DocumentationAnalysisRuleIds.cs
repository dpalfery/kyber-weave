namespace KyberWeave.Core.Docs.Analysis;

/// <summary>The analyzer's rule ids and the identity of the analysis it produced.</summary>
/// <remarks>
/// Declared as constants so the id and the condition cannot drift apart, and separated from
/// the analyzer class because the review exchange and the embedding coordinator publish them
/// in a bundle they write themselves. Those are use cases above the engine, so reading the
/// values off the analyzer would point every one of them back at the type that runs the
/// analysis.
/// </remarks>
public static class DocumentationAnalysisRuleIds
{
    public const string DuplicateRuleCode = "KW-DOC-ANALYSIS-001";
    public const string ConflictRuleCode = "KW-DOC-ANALYSIS-002";
    public const string TerminologyRuleCode = "KW-DOC-ANALYSIS-003";
    public const string IgnoreMarkupRuleCode = "KW-DOC-ANALYSIS-004";
    public const string CodeGraphUnavailableRuleCode = "KW-DOC-ANALYSIS-005";
    public const string EmbeddingUnavailableRuleCode = "KW-DOC-ANALYSIS-006";

    /// <summary>The review exchange's rule id, raised when a bundle or verdict cannot be read.</summary>
    public const string ReviewRuleCode = "KW-DOC-REVIEW-001";

    public const string AnalyzerVersion = "analyzer/v1";
    public const string RubricVersion = "rubric/v1";
}
