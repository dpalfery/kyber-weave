using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Embeddings;

namespace KyberWeave.Contracts.Services;

/// <summary>Turns claim text into validated, cached embedding vectors for an analysis pass.</summary>
public interface IEmbeddingResolver
{
    /// <summary>Resolves a vector for every work item, reading the cache before the provider.</summary>
    /// <param name="workItems">The claim texts to embed.</param>
    /// <param name="config">The embedding mode, model and dimensions to resolve under.</param>
    /// <returns>The vectors in request order, with diagnostics and cost measurements.</returns>
    EmbeddingResolutionResult Resolve(
        IReadOnlyCollection<EmbeddingWorkItem> workItems,
        DocsAnalysisEmbeddingConfig config);
}
