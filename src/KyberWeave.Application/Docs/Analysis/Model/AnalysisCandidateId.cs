using System.Security.Cryptography;
using System.Text;

namespace KyberWeave.Core.Docs.Analysis.Model;

/// <summary>Stable IDs for review decisions that survive source-file moves.</summary>
public static class AnalysisCandidateId
{
    /// <summary>Computes the content-addressed id of one analysis candidate.</summary>
    /// <param name="kind">The documentation condition the candidate represents.</param>
    /// <param name="normalizedTerm">The term, when the candidate is about one.</param>
    /// <param name="claimContentHashes">The content hashes of the candidate's evidence claims.</param>
    /// <param name="analyzerVersion">The analyzer version that produced the candidate.</param>
    /// <param name="rubricVersion">The review rubric version in force.</param>
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

#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
        IOrderedEnumerable<string> hashes = claimContentHashes.Order(StringComparer.Ordinal);
        string identity = string.Join('\n',
            kind.ToString().ToLowerInvariant(),
            normalizedTerm?.Trim().ToLowerInvariant() ?? string.Empty,
#pragma warning restore CA1308
            analyzerVersion,
            rubricVersion,
            string.Join('\n', hashes));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
