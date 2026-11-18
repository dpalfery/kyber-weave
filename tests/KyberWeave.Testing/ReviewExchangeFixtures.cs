using System.Text.Json;
using System.Text.Json.Serialization;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Review;

namespace KyberWeave.Tests;

/// <summary>
/// The candidates, claims and verdict bundles the documentation review exchange tests build, shared by the in-memory
/// cases and the real-SQLite cases so both arrange the same input.
/// </summary>
internal static class ReviewExchangeFixtures
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal static AnalysisCandidate Candidate(
        string id,
        AnalysisRuleKind kind,
        bool isExact = false,
        string? term = null,
        string claimText = "The runner emits the configured documentation review evidence.",
        IReadOnlyList<CandidateSourceKind>? sources = null,
        CandidateScore? score = null)
    {
        Claim[] claims =
        [
            Claim(id + "-left", "hash-" + id + "-left", claimText, 10),
            Claim(id + "-right", "hash-" + id + "-right", claimText + " Related context.", 20)
        ];
        return new AnalysisCandidate(
            id,
            kind,
            claims,
            score ?? new CandidateScore(0.72, null, 1),
            isExact,
            term,
            sources ?? [CandidateSourceKind.Graph]);
    }

    internal static Claim Claim(string id, string contentHash, string text, int line) =>
        new(
            ClaimKind.Paragraph,
            text,
            "Behavior\n" + text,
            contentHash,
            "context-" + id,
            "docs/" + id,
            "Runtime",
            "Behavior",
            "/repo/docs/" + id + ".md",
            line,
            line + 1,
            IgnoreRule.None);


    internal static ReviewVerdictBundle VerdictBundle(
        ReviewCandidateBundle export,
        params ReviewVerdictItem[] verdicts) =>
        new(
            "kyber-weave.docs-review.verdicts/v1",
            export.AnalyzerVersion,
            export.RubricVersion,
            export.CandidateSetHash,
            verdicts);

    internal static ReviewVerdictItem VerdictItem(
        ReviewCandidateItem candidate,
        AnalysisVerdictLabel label,
        double confidence = 0.90,
        IReadOnlyList<ProposedGlossarySense>? senses = null) =>
        new(
            candidate.CandidateId,
            label,
            confidence,
            "Reviewer evaluated every supplied evidence location.",
            candidate.ClaimContentHashes,
            candidate.Evidence.Select(evidence => evidence.Id).ToArray(),
            null,
            senses);

    internal static string Serialize(ReviewVerdictBundle bundle) =>
        JsonSerializer.Serialize(bundle, JsonOptions);


    internal static string Join(DiagnosticReport diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Items);


    internal static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
