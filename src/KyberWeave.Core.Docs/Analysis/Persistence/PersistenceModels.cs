using KyberWeave.Core.Docs.Analysis.Model;

namespace KyberWeave.Core.Docs.Analysis.Persistence;

/// <summary>A line-addressable claim occurrence retained for review evidence.</summary>
public sealed record PersistedClaim(
    string Id,
    string ContentHash,
    string ContextualHash,
    string DocumentIdentity,
    string FilePath,
    int StartLine,
    int EndLine,
    string Text);

/// <summary>The content and rubric identity against which a reviewer verdict was made.</summary>
public sealed record PersistedCandidateFingerprint(
    string CandidateId,
    AnalysisRuleKind Kind,
    string? NormalizedTerm,
    string CandidateSetHash,
    string AnalyzerVersion,
    string RubricVersion,
    IReadOnlyList<string> ClaimContentHashes);
