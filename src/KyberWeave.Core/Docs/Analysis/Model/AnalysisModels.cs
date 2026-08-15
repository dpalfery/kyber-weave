using System.Security.Cryptography;
using System.Text;
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
public sealed record AnalysisGlossary(IReadOnlyList<ApprovedGlossarySense> Senses)
{
    /// <summary>True when each occurrence maps to exactly one approved sense.</summary>
    public bool Covers(string term, IReadOnlyList<Claim> claims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        ArgumentNullException.ThrowIfNull(claims);

        var senses = Senses
            .Where(sense => StringComparer.OrdinalIgnoreCase.Equals(sense.Term, term))
            .ToArray();
        if (senses.Length == 0) return false;

        return claims.All(claim => senses.Count(sense => AppliesTo(sense, claim)) == 1);
    }

    private static bool AppliesTo(ApprovedGlossarySense sense, Claim claim) =>
        sense.Scopes.Any(scope =>
            scope.StartsWith("component:", StringComparison.Ordinal)
                ? StringComparer.OrdinalIgnoreCase.Equals(scope["component:".Length..], claim.Component)
                : scope.StartsWith("code-ref:", StringComparison.Ordinal)
                  && claim.CodeRefs.Contains(scope["code-ref:".Length..], StringComparer.Ordinal));
}

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

/// <summary>Stable IDs for review decisions that survive source-file moves.</summary>
public static class AnalysisCandidateId
{
    public static string Compute(
        AnalysisRuleKind kind,
        string? normalizedTerm,
        IEnumerable<string> claimContentHashes,
        string analyzerVersion,
        string rubricVersion)
    {
        ArgumentNullException.ThrowIfNull(claimContentHashes);
        ArgumentException.ThrowIfNullOrWhiteSpace(analyzerVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(rubricVersion);

        var hashes = claimContentHashes.Order(StringComparer.Ordinal);
        var identity = string.Join('\n',
            kind.ToString().ToLowerInvariant(),
            normalizedTerm?.Trim().ToLowerInvariant() ?? string.Empty,
            analyzerVersion,
            rubricVersion,
            string.Join('\n', hashes));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}

/// <summary>Identity of one cached embedding.</summary>
public sealed record EmbeddingCacheKey(
    string ContextualHash,
    string ProviderFingerprint,
    string Model,
    int? Dimensions,
    string Encoding = "float");

/// <summary>A normalized vector ready for exact cosine comparison.</summary>
public sealed record StoredEmbedding(EmbeddingCacheKey Key, IReadOnlyList<float> Vector);
