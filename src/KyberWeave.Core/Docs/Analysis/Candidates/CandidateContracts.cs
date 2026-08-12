using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Graph;

namespace KyberWeave.Core.Docs.Analysis.Candidates;

/// <summary>The bounded retrieval strategy that produced a claim pair.</summary>
public enum CandidateSourceKind
{
    Graph,
    Lexical,
    Embedding
}

/// <summary>Independent evidence scores retained for classification and review.</summary>
public sealed record CandidateScore(double Lexical, double? Semantic, double Graph);

/// <summary>Two claims shortlisted by one bounded candidate source.</summary>
public sealed record ClaimPairCandidate(
    Claim Left,
    Claim Right,
    CandidateSourceKind Source,
    CandidateScore Score);

/// <summary>Immutable input shared by candidate-source implementations.</summary>
public sealed record ClaimCandidateSourceRequest(
    IReadOnlyList<Claim> Claims,
    DocGraphProjection Graph,
    DocsAnalysisSearchConfig Search);

/// <summary>
/// Pairs returned by a source, the number of similarity comparisons it performed, and
/// whether its own configured capacity discarded otherwise eligible pairs.
/// </summary>
public sealed record ClaimCandidateSourceResult(
    IReadOnlyList<ClaimPairCandidate> Pairs,
    int ComparisonCount,
    bool Truncated = false);

/// <summary>Infrastructure-neutral port for bounded claim candidate generation.</summary>
public interface IClaimCandidateSource
{
    CandidateSourceKind Kind { get; }

    ClaimCandidateSourceResult FindCandidates(ClaimCandidateSourceRequest request);
}
