using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Model;

namespace KyberWeave.Core.Docs.Analysis.Review;

/// <summary>Character limits applied to reviewer evidence exports.</summary>
public sealed record ReviewExportOptions(
    int MaxExcerptCharacters = 500,
    int CharacterBudget = 12_000);

/// <summary>One label and its stable judging definition.</summary>
public sealed record ReviewRubricLabel(AnalysisVerdictLabel Label, string Definition);

/// <summary>The judging rubric embedded in every candidate export.</summary>
public sealed record ReviewRubric(IReadOnlyList<ReviewRubricLabel> Labels);

/// <summary>One capped, line-addressable claim occurrence supplied to a reviewer.</summary>
public sealed record ReviewEvidenceItem(
    string Id,
    string ContentHash,
    string ContextualHash,
    string DocumentIdentity,
    string FilePath,
    int StartLine,
    int EndLine,
    string Excerpt);

/// <summary>One pending duplicate, conflict, or terminology review candidate.</summary>
public sealed record ReviewCandidateItem(
    string CandidateId,
    AnalysisRuleKind Kind,
    string? Term,
    CandidateScore Score,
    IReadOnlyList<CandidateSourceKind> Sources,
    IReadOnlyList<string> ClaimContentHashes,
    IReadOnlyList<ReviewEvidenceItem> Evidence);

/// <summary>Versioned candidate exchange document.</summary>
public sealed record ReviewCandidateBundle(
    string Schema,
    string AnalyzerVersion,
    string RubricVersion,
    string CandidateSetHash,
    ReviewRubric Rubric,
    IReadOnlyList<ReviewCandidateItem> Candidates);

/// <summary>Serialized candidate export and local-only budget measurements.</summary>
public sealed record ReviewExportResult(
    ReviewCandidateBundle Bundle,
    string Json,
    int ExportedExcerptCharacters,
    bool Truncated,
    DiagnosticReport? Diagnostics = null)
{
    /// <summary>Analysis warnings and local cost measurements associated with the export.</summary>
    public DiagnosticReport Diagnostics { get; } = Diagnostics ?? new DiagnosticReport();
}

/// <summary>One reviewer verdict echoed against the exported content identity.</summary>
public sealed record ReviewVerdictItem(
    string CandidateId,
    AnalysisVerdictLabel? Label,
    double? Confidence,
    string Rationale,
    IReadOnlyList<string> ClaimContentHashes,
    IReadOnlyList<string> EvidenceIds,
    string? RecommendedCanonicalLocation = null,
    IReadOnlyList<ProposedGlossarySense>? ProposedGlossarySenses = null);

/// <summary>Versioned reviewer verdict exchange document.</summary>
public sealed record ReviewVerdictBundle(
    string Schema,
    string AnalyzerVersion,
    string RubricVersion,
    string CandidateSetHash,
    IReadOnlyList<ReviewVerdictItem> Verdicts);

/// <summary>Atomic verdict-import outcome.</summary>
public sealed record ReviewImportResult(
    bool Success,
    int ImportedCount,
    DiagnosticReport Diagnostics);
