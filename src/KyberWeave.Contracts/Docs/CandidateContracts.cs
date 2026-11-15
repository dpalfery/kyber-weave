using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Graph;

namespace KyberWeave.Core.Docs.Analysis.Candidates;

/// <summary>Immutable input shared by candidate-source implementations.</summary>
public sealed record ClaimCandidateSourceRequest(
    IReadOnlyList<Claim> Claims,
    DocGraphProjection Graph,
    DocsAnalysisSearchConfig Search);

/// <summary>Infrastructure-neutral port for bounded claim candidate generation.</summary>
public interface IClaimCandidateSource
{
    CandidateSourceKind Kind { get; }

    ClaimCandidateSourceResult FindCandidates(ClaimCandidateSourceRequest request);
}
