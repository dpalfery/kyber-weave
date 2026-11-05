using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Persistence;

namespace KyberWeave.Core.Docs.Analysis;

/// <summary>Infrastructure-neutral port for an optional embedding provider.</summary>
public interface IEmbeddingGenerator
{
    /// <summary>
    /// Stable identity of the provider configuration used as part of the vector cache key.
    /// Credentials must never contribute to this value.
    /// </summary>
    string GetProviderFingerprint(DocsAnalysisEmbeddingConfig config);

    EmbeddingGenerationResult Generate(
        IReadOnlyCollection<EmbeddingCacheKey> keys,
        IReadOnlyCollection<string> inputs,
        DocsAnalysisEmbeddingConfig config);
}

/// <summary>Infrastructure-neutral cache for reviewer verdicts and normalized vectors.</summary>
public interface IAnalysisPersistence
{
    bool IsAvailable { get; }

    IReadOnlyDictionary<string, PersistedClaim> LoadClaims(
        IReadOnlyCollection<string> claimIds) =>
        new Dictionary<string, PersistedClaim>(StringComparer.Ordinal);

    void SaveClaims(IReadOnlyCollection<PersistedClaim> claims) =>
        throw new InvalidOperationException("This analysis persistence provider does not store claims.");

    IReadOnlyDictionary<string, PersistedCandidateFingerprint> LoadCandidateFingerprints(
        IReadOnlyCollection<string> candidateIds) =>
        new Dictionary<string, PersistedCandidateFingerprint>(StringComparer.Ordinal);

    void SaveCandidateFingerprints(
        IReadOnlyCollection<PersistedCandidateFingerprint> candidates) =>
        throw new InvalidOperationException("This analysis persistence provider does not store candidate fingerprints.");

    IReadOnlyDictionary<string, AnalysisVerdict> LoadVerdicts(
        IReadOnlyCollection<string> candidateIds);

    void SaveVerdicts(IReadOnlyCollection<AnalysisVerdict> verdicts) =>
        throw new InvalidOperationException("This analysis persistence provider is read-only for verdicts.");

    /// <summary>
    /// Persists the current review evidence and its validated verdicts as one logical import.
    /// Stores with transactional support should override this operation; the default preserves
    /// compatibility with verdict-only adapters.
    /// </summary>
    void SaveReviewImport(
        IReadOnlyCollection<PersistedClaim> claims,
        IReadOnlyCollection<PersistedCandidateFingerprint> candidates,
        IReadOnlyCollection<AnalysisVerdict> verdicts) => SaveVerdicts(verdicts);

    IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> LoadEmbeddings(
        IReadOnlyCollection<EmbeddingCacheKey> keys);

    void SaveEmbeddings(IReadOnlyCollection<StoredEmbedding> embeddings);
}
