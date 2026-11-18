using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Model;

namespace KyberWeave.Core.Docs.Analysis.Embeddings;

/// <summary>One contextual claim text requiring an embedding.</summary>
public sealed record EmbeddingWorkItem(string ContextualHash, string Input);

/// <summary>Provider-reported token counts for local cost visibility.</summary>
public sealed record EmbeddingUsage(int PromptTokens = 0, int TotalTokens = 0)
{
    /// <summary>No provider tokens were spent.</summary>
    public static EmbeddingUsage None { get; } = new();
}

/// <summary>Validated provider vectors and aggregate usage for one generation request.</summary>
public sealed record EmbeddingGenerationResult(
    IReadOnlyList<StoredEmbedding> Embeddings,
    EmbeddingUsage Usage);

/// <summary>Cache-aware embedding resolution result.</summary>
public sealed record EmbeddingResolutionResult(
    IReadOnlyList<StoredEmbedding> Embeddings,
    DiagnosticReport Diagnostics,
    int CacheHits,
    int CacheMisses,
    EmbeddingUsage Usage);
