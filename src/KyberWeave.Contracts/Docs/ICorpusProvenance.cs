namespace KyberWeave.Contracts.Docs;

/// <summary>Formats the provenance header every docs tool leads with.</summary>
public interface ICorpusProvenance
{
    /// <summary>The one-line provenance header for a response over <paramref name="documentCount"/> documents.</summary>
    /// <param name="repositoryRoot">The bound repository root.</param>
    /// <param name="documentCount">The live document count for the corpus, or null if unavailable.</param>
    /// <param name="corpusStamp">The host's corpus stamp, used with the git fingerprint to expire the cache.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    Task<string> LineAsync(
        string repositoryRoot,
        int? documentCount,
        int corpusStamp,
        CancellationToken cancellationToken = default);
}
