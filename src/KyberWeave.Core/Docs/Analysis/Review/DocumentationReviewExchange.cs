using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Persistence;

namespace KyberWeave.Core.Docs.Analysis.Review;

/// <summary>
/// Exports bounded review evidence and atomically imports content-addressed verdicts.
/// </summary>
public sealed class DocumentationReviewExchange
{
    public const string CandidateSchema = "kyber-weave.docs-review.candidates/v1";
    public const string VerdictSchema = "kyber-weave.docs-review.verdicts/v1";
    public const string ReviewRuleCode = "KW-DOC-REVIEW-001";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly ReviewRubric Rubric = new(
    [
        new(AnalysisVerdictLabel.Duplicate, "The claims express substantively the same claim, not merely the same topic."),
        new(AnalysisVerdictLabel.Conflict, "The claims cannot both be true in the same scope and time."),
        new(AnalysisVerdictLabel.DistinctSenses, "The shared term denotes multiple concepts."),
        new(AnalysisVerdictLabel.Benign, "The overlap is intentional or the claims apply to compatible scopes."),
        new(AnalysisVerdictLabel.Uncertain, "The supplied evidence is insufficient for a durable disposition.")
    ]);

    private readonly IAnalysisPersistence _persistence;
    private readonly double _confidenceThreshold;

    public DocumentationReviewExchange(
        IAnalysisPersistence persistence,
        double confidenceThreshold = 0.80)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        if (!double.IsFinite(confidenceThreshold)
            || confidenceThreshold < 0
            || confidenceThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidenceThreshold),
                confidenceThreshold,
                "The verdict confidence threshold must be between zero and one.");
        }

        _confidenceThreshold = confidenceThreshold;
    }

    /// <summary>Exports pending review candidates without exact duplicate clusters.</summary>
    public ReviewExportResult Export(
        IReadOnlyList<AnalysisCandidate> currentCandidates,
        ReviewExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(currentCandidates);
        options ??= new ReviewExportOptions();
        Validate(options);

        var pending = PendingCandidates(currentCandidates);

        var remaining = options.CharacterBudget;
        var exportedCharacters = 0;
        var truncated = false;
        var items = new List<ReviewCandidateItem>(pending.Count);
        var emittedCandidates = new List<AnalysisCandidate>(pending.Count);
        foreach (var candidate in pending)
        {
            var orderedClaims = OrderedClaims(candidate.Claims);
            var requiredCharacters = orderedClaims.Sum(claim =>
                Math.Min(claim.Text.Length, options.MaxExcerptCharacters));
            if (remaining < orderedClaims.Count
                || (items.Count > 0 && requiredCharacters > remaining))
            {
                truncated = true;
                break;
            }

            var evidence = new List<ReviewEvidenceItem>(orderedClaims.Count);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < orderedClaims.Count; index++)
            {
                var claim = orderedClaims[index];
                occurrences.TryGetValue(claim.ContentHash, out var occurrence);
                occurrences[claim.ContentHash] = occurrence + 1;
                var reservedForRemainingEvidence = orderedClaims.Count - index - 1;
                var maximum = Math.Min(
                    options.MaxExcerptCharacters,
                    remaining - reservedForRemainingEvidence);
                var excerptLength = Math.Min(claim.Text.Length, maximum);
                var excerpt = claim.Text[..excerptLength];
                truncated |= excerptLength < claim.Text.Length;
                remaining -= excerptLength;
                exportedCharacters += excerptLength;
                evidence.Add(new ReviewEvidenceItem(
                    EvidenceId(candidate.Id, claim.ContentHash, occurrence),
                    claim.ContentHash,
                    claim.ContextualHash,
                    claim.DocumentIdentity,
                    claim.FilePath,
                    claim.StartLine,
                    claim.EndLine,
                    excerpt));
            }

            items.Add(new ReviewCandidateItem(
                candidate.Id,
                candidate.Kind,
                candidate.Term,
                candidate.Score,
                candidate.Sources.Order().ToArray(),
                evidence.Select(item => item.ContentHash).ToArray(),
                evidence));
            emittedCandidates.Add(candidate);
        }

        truncated |= emittedCandidates.Count < pending.Count;

        var bundle = new ReviewCandidateBundle(
            CandidateSchema,
            DocumentationAnalyzer.AnalyzerVersion,
            DocumentationAnalyzer.RubricVersion,
            CandidateSetHash(emittedCandidates),
            Rubric,
            items);
        return new ReviewExportResult(
            bundle,
            JsonSerializer.Serialize(bundle, SerializerOptions),
            exportedCharacters,
            truncated);
    }

    /// <summary>
    /// Validates the complete verdict bundle against the current candidate content before
    /// making one persistence call.
    /// </summary>
    public ReviewImportResult Import(
        string json,
        IReadOnlyList<AnalysisCandidate> currentCandidates)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(currentCandidates);

        ReviewVerdictBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<ReviewVerdictBundle>(json, SerializerOptions);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or ArgumentException)
        {
            return Failure("The verdict bundle is not valid JSON for the review schema.");
        }

        if (bundle is null) return Failure("The verdict bundle is empty.");
        var pending = PendingCandidates(currentCandidates);
        var candidatesById = pending.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var error = ValidateBundle(bundle, candidatesById);
        if (error is not null) return Failure(error);

        var verdicts = bundle.Verdicts.Select(item => new AnalysisVerdict(
            item.CandidateId,
            item.Label!.Value,
            item.Confidence!.Value,
            item.Rationale,
            item.EvidenceIds,
            item.RecommendedCanonicalLocation,
            item.ProposedGlossarySenses)).ToArray();
        var reviewedCandidates = bundle.Verdicts
            .Select(item => candidatesById[item.CandidateId])
            .ToArray();
        var claims = PersistedClaims(reviewedCandidates);
        var fingerprints = reviewedCandidates.Select(candidate => new PersistedCandidateFingerprint(
            candidate.Id,
            candidate.Kind,
            candidate.Term?.Trim().ToLowerInvariant(),
            bundle.CandidateSetHash,
            bundle.AnalyzerVersion,
            bundle.RubricVersion,
            candidate.Claims.Select(claim => claim.ContentHash).ToArray())).ToArray();

        try
        {
            _persistence.SaveReviewImport(claims, fingerprints, verdicts);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
        {
            return Failure($"The validated verdict bundle could not be persisted: {exception.Message}");
        }

        return new ReviewImportResult(true, verdicts.Length, new DiagnosticReport());
    }

    private static IReadOnlyList<PersistedClaim> PersistedClaims(
        IReadOnlyList<AnalysisCandidate> candidates)
    {
        var claims = new List<PersistedClaim>();
        foreach (var candidate in candidates)
        {
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var claim in OrderedClaims(candidate.Claims))
            {
                occurrences.TryGetValue(claim.ContentHash, out var occurrence);
                occurrences[claim.ContentHash] = occurrence + 1;
                claims.Add(new PersistedClaim(
                    EvidenceId(candidate.Id, claim.ContentHash, occurrence),
                    claim.ContentHash,
                    claim.ContextualHash,
                    claim.DocumentIdentity,
                    claim.FilePath,
                    claim.StartLine,
                    claim.EndLine,
                    claim.Text));
            }
        }

        return claims;
    }

    private string? ValidateBundle(
        ReviewVerdictBundle bundle,
        IReadOnlyDictionary<string, AnalysisCandidate> candidates)
    {
        if (!StringComparer.Ordinal.Equals(bundle.Schema, VerdictSchema))
            return $"Unsupported verdict schema '{bundle.Schema}'.";
        if (!StringComparer.Ordinal.Equals(bundle.AnalyzerVersion, DocumentationAnalyzer.AnalyzerVersion))
            return "The verdict bundle analyzer version is stale.";
        if (!StringComparer.Ordinal.Equals(bundle.RubricVersion, DocumentationAnalyzer.RubricVersion))
            return "The verdict bundle rubric version is stale.";
        if (bundle.Verdicts is null || bundle.Verdicts.Count == 0)
            return "The verdict bundle does not contain any verdicts.";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reviewedCandidates = new List<AnalysisCandidate>(bundle.Verdicts.Count);
        foreach (var verdict in bundle.Verdicts)
        {
            if (verdict is null) return "The verdict bundle contains an empty verdict.";
            if (string.IsNullOrWhiteSpace(verdict.CandidateId) || !seen.Add(verdict.CandidateId))
                return "Verdict candidate ids must be non-empty and unique.";
            if (!candidates.TryGetValue(verdict.CandidateId, out var candidate))
                return $"Verdict candidate '{verdict.CandidateId}' is not current.";
            reviewedCandidates.Add(candidate);
        }

        if (!StringComparer.Ordinal.Equals(
                bundle.CandidateSetHash,
                CandidateSetHash(reviewedCandidates)))
        {
            return "The verdict bundle candidate set is stale.";
        }

        foreach (var verdict in bundle.Verdicts)
        {
            var candidate = candidates[verdict.CandidateId];
            if (!HashesMatch(verdict.ClaimContentHashes, candidate.Claims))
                return $"Verdict candidate '{verdict.CandidateId}' has stale claim content.";
            if (verdict.Label is null || !LabelApplies(candidate.Kind, verdict.Label.Value))
                return $"Verdict label '{verdict.Label}' does not apply to a {candidate.Kind} candidate.";
            if (verdict.Confidence is null
                || !double.IsFinite(verdict.Confidence.Value)
                || verdict.Confidence.Value < 0
                || verdict.Confidence.Value > 1)
            {
                return $"Verdict candidate '{verdict.CandidateId}' has invalid confidence.";
            }
            if (string.IsNullOrWhiteSpace(verdict.Rationale))
                return $"Verdict candidate '{verdict.CandidateId}' requires a rationale.";
            if (!EvidenceReferencesMatch(verdict.EvidenceIds, candidate))
                return $"Verdict candidate '{verdict.CandidateId}' references unknown evidence.";
            if (!GlossarySensesAreValid(verdict, candidate))
                return $"Verdict candidate '{verdict.CandidateId}' has invalid glossary sense proposals.";
        }

        if (!_persistence.IsAvailable) return "The analysis cache is unavailable for verdict import.";
        return null;
    }

    private static IReadOnlyList<AnalysisCandidate> ReviewableCandidates(
        IEnumerable<AnalysisCandidate> candidates) =>
        candidates
            .Where(candidate => !candidate.IsExact)
            .OrderBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.Term, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();

    private IReadOnlyList<AnalysisCandidate> PendingCandidates(
        IEnumerable<AnalysisCandidate> candidates)
    {
        var reviewable = ReviewableCandidates(candidates);
        var verdicts = _persistence.IsAvailable
            ? _persistence.LoadVerdicts(reviewable.Select(candidate => candidate.Id).ToArray())
            : new Dictionary<string, AnalysisVerdict>(StringComparer.Ordinal);
        return reviewable
            .Where(candidate => IsPending(candidate.Id, verdicts))
            .ToArray();
    }

    private bool IsPending(
        string candidateId,
        IReadOnlyDictionary<string, AnalysisVerdict> verdicts) =>
        !verdicts.TryGetValue(candidateId, out var verdict)
        || verdict.Label == AnalysisVerdictLabel.Uncertain
        || verdict.Confidence < _confidenceThreshold;

    private static IReadOnlyList<Claim> OrderedClaims(IEnumerable<Claim> claims) =>
        claims.OrderBy(claim => claim.FilePath, StringComparer.Ordinal)
            .ThenBy(claim => claim.StartLine)
            .ThenBy(claim => claim.ContentHash, StringComparer.Ordinal)
            .ToArray();

    private static bool HashesMatch(
        IReadOnlyList<string>? hashes,
        IReadOnlyList<Claim> claims) =>
        hashes is not null
        && hashes.Order(StringComparer.Ordinal).SequenceEqual(
            claims.Select(claim => claim.ContentHash).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static bool EvidenceReferencesMatch(
        IReadOnlyList<string>? evidenceIds,
        AnalysisCandidate candidate)
    {
        if (evidenceIds is null || evidenceIds.Count == 0) return false;
        var expected = ExpectedEvidenceIds(candidate);
        return evidenceIds.All(expected.Contains);
    }

    private static IReadOnlySet<string> ExpectedEvidenceIds(AnalysisCandidate candidate)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in OrderedClaims(candidate.Claims))
        {
            occurrences.TryGetValue(claim.ContentHash, out var occurrence);
            occurrences[claim.ContentHash] = occurrence + 1;
            ids.Add(EvidenceId(candidate.Id, claim.ContentHash, occurrence));
        }

        return ids;
    }

    private static bool LabelApplies(AnalysisRuleKind kind, AnalysisVerdictLabel label) =>
        label is AnalysisVerdictLabel.Benign or AnalysisVerdictLabel.Uncertain
        || (kind == AnalysisRuleKind.Duplicate && label == AnalysisVerdictLabel.Duplicate)
        || (kind == AnalysisRuleKind.Conflict && label == AnalysisVerdictLabel.Conflict)
        || (kind == AnalysisRuleKind.Terminology && label == AnalysisVerdictLabel.DistinctSenses);

    private static bool GlossarySensesAreValid(
        ReviewVerdictItem verdict,
        AnalysisCandidate candidate)
    {
        if (verdict.ProposedGlossarySenses is null) return true;
        if (candidate.Kind != AnalysisRuleKind.Terminology
            || verdict.Label != AnalysisVerdictLabel.DistinctSenses
            || verdict.ProposedGlossarySenses.Count == 0)
        {
            return false;
        }

        return verdict.ProposedGlossarySenses.All(sense =>
            !string.IsNullOrWhiteSpace(sense.Term)
            && StringComparer.OrdinalIgnoreCase.Equals(sense.Term, candidate.Term)
            && !string.IsNullOrWhiteSpace(sense.Definition)
            && sense.Scopes is { Count: > 0 }
            && sense.Scopes.All(ValidScope)
            && sense.Aliases is not null
            && sense.Aliases.All(alias => !string.IsNullOrWhiteSpace(alias)));
    }

    private static bool ValidScope(string scope) =>
        (scope.StartsWith("component:", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(scope["component:".Length..]))
        || (scope.StartsWith("code-ref:", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(scope["code-ref:".Length..]));

    private static string CandidateSetHash(IEnumerable<AnalysisCandidate> candidates)
    {
        var candidateLines = candidates
                .Select(candidate => string.Join('|',
                    candidate.Id,
                    string.Join(',', candidate.Claims
                        .Select(claim => claim.ContentHash)
                        .Order(StringComparer.Ordinal))))
                .Order(StringComparer.Ordinal);
        var identity = string.Join('\n',
            new[]
            {
                DocumentationAnalyzer.AnalyzerVersion,
                DocumentationAnalyzer.RubricVersion
            }.Concat(candidateLines));
        return Hash(identity);
    }

    private static string EvidenceId(
        string candidateId,
        string contentHash,
        int occurrence) =>
        Hash(string.Join('|',
            candidateId,
            contentHash,
            occurrence));

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ReviewImportResult Failure(string message)
    {
        var diagnostics = new DiagnosticReport();
        diagnostics.Add(new Diagnostic(
            ReviewRuleCode,
            Severity.Error,
            message,
            "docs review import",
            Hint: "Export a fresh candidate bundle, review only its evidence ids, and import a complete verdicts/v1 document."));
        return new ReviewImportResult(false, 0, diagnostics);
    }

    private static void Validate(ReviewExportOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxExcerptCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.CharacterBudget);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
