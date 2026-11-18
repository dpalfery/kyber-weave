using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;

namespace KyberWeave.Core.Docs.Analysis.Model;

/// <summary>The documentation condition represented by an analysis candidate.</summary>
public enum AnalysisRuleKind
{
    Duplicate,
    Conflict,
    Terminology
}

/// <summary>A reviewer's disposition for an analysis candidate.</summary>
public enum AnalysisVerdictLabel
{
    Duplicate,
    Conflict,
    DistinctSenses,
    Benign,
    Uncertain
}

/// <summary>A durable reviewer decision associated with a content-addressed candidate.</summary>
public sealed record AnalysisVerdict(
    string CandidateId,
    AnalysisVerdictLabel Label,
    double Confidence,
    string Rationale,
    IReadOnlyList<string>? EvidenceIds = null,
    string? RecommendedCanonicalLocation = null,
    IReadOnlyList<ProposedGlossarySense>? ProposedGlossarySenses = null);

/// <summary>A glossary sense proposed by an external reviewer.</summary>
public sealed record ProposedGlossarySense(
    string Term,
    string Definition,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Aliases);

/// <summary>An approved, scope-qualified meaning for a glossary term.</summary>
public sealed record ApprovedGlossarySense(
    string Id,
    string Term,
    string Definition,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Aliases);

/// <summary>Approved glossary data used to suppress fully explained terminology findings.</summary>
public sealed record AnalysisGlossary(IReadOnlyList<ApprovedGlossarySense> Senses);

/// <summary>One clustered analysis finding and its evidence claims.</summary>
public sealed record AnalysisCandidate(
    string Id,
    AnalysisRuleKind Kind,
    IReadOnlyList<Claim> Claims,
    CandidateScore Score,
    bool IsExact = false,
    string? Term = null,
    IReadOnlyList<CandidateSourceKind>? Sources = null,
    AnalysisVerdict? Verdict = null)
{
    public IReadOnlyList<CandidateSourceKind> Sources { get; init; } = Sources ?? [];
}

/// <summary>Local-only measurements explaining analysis cost and truncation.</summary>
public sealed record AnalysisMetrics(
    int ExtractedClaims,
    int GraphComparisons,
    int LexicalComparisons,
    int EmbeddingComparisons,
    int GraphCandidates,
    int LexicalCandidates,
    int EmbeddingCandidates,
    bool Truncated);

/// <summary>The candidates, diagnostics, and cost measurements from one analysis pass.</summary>
public sealed record DocumentationAnalysisResult(
    IReadOnlyList<AnalysisCandidate> Candidates,
    DiagnosticReport Diagnostics,
    AnalysisMetrics Metrics);

/// <summary>Identity of one cached embedding.</summary>
public sealed record EmbeddingCacheKey(
    string ContextualHash,
    string ProviderFingerprint,
    string Model,
    int? Dimensions,
    string Encoding = "float");

/// <summary>A normalized vector ready for exact cosine comparison.</summary>
public sealed record StoredEmbedding(EmbeddingCacheKey Key, IReadOnlyList<float> Vector);
