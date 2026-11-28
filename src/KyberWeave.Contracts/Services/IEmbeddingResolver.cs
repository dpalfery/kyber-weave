using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Embeddings;

namespace KyberWeave.Contracts.Services;

/// <summary>Turns claim text into validated, cached embedding vectors for an analysis pass.</summary>
public interface IEmbeddingResolver
{
    /// <summary>Resolves a vector for every work item without blocking a thread, reading the cache before the provider.</summary>
    /// <param name="workItems">The claim texts to embed.</param>
    /// <param name="config">The embedding mode, model and dimensions to resolve under.</param>
    /// <param name="cancellationToken">Cancels the provider calls the resolution needs.</param>
    /// <returns>The vectors in request order, with diagnostics and cost measurements.</returns>
    Task<EmbeddingResolutionResult> ResolveAsync(
        IReadOnlyCollection<EmbeddingWorkItem> workItems,
        DocsAnalysisEmbeddingConfig config,
        CancellationToken cancellationToken = default);
}
