using System.Text.Json;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Model;

namespace KyberWeave.Core.Docs.Analysis.Embeddings;

/// <summary>Applies embedding mode and safe-cache policy around a provider.</summary>
public sealed class EmbeddingCoordinator(IEmbeddingGenerator generator, IAnalysisPersistence persistence) : IEmbeddingResolver
{
    private readonly IEmbeddingGenerator _generator = generator ?? throw new ArgumentNullException(nameof(generator));
    private readonly IAnalysisPersistence _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));

    /// <summary>Wraps a provider and a cache as the analysis pass's resolver.</summary>
    /// <param name="generator">The configured provider.</param>
    /// <param name="persistence">The analysis cache.</param>
    /// <returns>A resolver over both.</returns>
    public static IEmbeddingResolver Create(IEmbeddingGenerator generator, IAnalysisPersistence persistence) =>
        new EmbeddingCoordinator(generator, persistence);

    /// <inheritdoc />
    public async Task<EmbeddingResolutionResult> ResolveAsync(
        IReadOnlyCollection<EmbeddingWorkItem> workItems,
        DocsAnalysisEmbeddingConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        ArgumentNullException.ThrowIfNull(config);

        if (config.Mode == DocsAnalysisEmbeddingMode.Off)
            return Empty();
        if (!_persistence.IsAvailable)
        {
            return EmbeddingResolutionResults.Unavailable(config.Mode,
                "Embedding generation was skipped because the analysis cache is not safely ignored.");
        }

        try
        {
            return await ResolveFromCacheOrProviderAsync(workItems, config, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return EmbeddingResolutionResults.Unavailable(config.Mode, ex.Message);
        }
        catch (IOException ex)
        {
            return EmbeddingResolutionResults.Unavailable(config.Mode, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return EmbeddingResolutionResults.Unavailable(config.Mode, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return EmbeddingResolutionResults.Unavailable(config.Mode, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return EmbeddingResolutionResults.Unavailable(config.Mode, ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            return EmbeddingResolutionResults.Unavailable(config.Mode, ex.Message);
        }
        catch (JsonException ex)
        {
            return EmbeddingResolutionResults.Unavailable(config.Mode, ex.Message);
        }
        catch (OverflowException ex)
        {
            return EmbeddingResolutionResults.Unavailable(config.Mode, ex.Message);
        }
        catch (FormatException)
        {
            // Header parsing is the only provider-boundary FormatException expected here.
            // Use a fixed reason so malformed secret text can never reach diagnostics.
            return EmbeddingResolutionResults.Unavailable(config.Mode,
                "The configured embedding credential is not a valid HTTP bearer token.");
        }
    }

    private async Task<EmbeddingResolutionResult> ResolveFromCacheOrProviderAsync(
        IReadOnlyCollection<EmbeddingWorkItem> workItems,
        DocsAnalysisEmbeddingConfig config,
        CancellationToken cancellationToken)
    {
        PreparedResolution? prepared = PrepareResolution(workItems, config);
        if (prepared is null)
            return Empty();

        EmbeddingGenerationResult? generated = prepared.Misses.Count > 0
            ? await _generator.GenerateAsync(prepared.Misses, prepared.Inputs, config, cancellationToken)
                .ConfigureAwait(false)
            : null;
        return FinalizeResolution(prepared, generated);
    }

    /// <summary>
    /// Runs the pure parts of a resolution: the contextual-hash and model checks, the cache read,
    /// and the miss computation that decides what the provider must supply.
    /// </summary>
    /// <returns>The resolution's state, or null when there is nothing to resolve.</returns>
    private PreparedResolution? PrepareResolution(
        IReadOnlyCollection<EmbeddingWorkItem> workItems,
        DocsAnalysisEmbeddingConfig config)
    {
        EmbeddingWorkItem[] orderedWork = workItems.ToArray();
        if (orderedWork.Length == 0)
            return null;
        if (orderedWork.Any(item => string.IsNullOrWhiteSpace(item.ContextualHash)))
            throw new ArgumentException("Embedding work items require a contextual hash.", nameof(workItems));

        string model = string.IsNullOrWhiteSpace(config.Model)
            ? throw new ArgumentException("An embedding model is required.", nameof(config))
            : config.Model;
        string provider = _generator.GetProviderFingerprint(config);
        EmbeddingCacheKey[] orderedKeys = orderedWork
            .Select(item => new EmbeddingCacheKey(
                item.ContextualHash,
                provider,
                model,
                config.Dimensions))
            .ToArray();
        EmbeddingCacheKey[] uniqueKeys = orderedKeys.Distinct().ToArray();
        IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> cached = _persistence.LoadEmbeddings(uniqueKeys);
        EmbeddingCacheKey[] misses = uniqueKeys.Where(key => !cached.ContainsKey(key)).ToArray();
        Dictionary<EmbeddingCacheKey, string> firstInputByKey = orderedKeys
            .Select((key, index) => new { key, orderedWork[index].Input })
            .GroupBy(item => item.key)
            .ToDictionary(group => group.Key, group => group.First().Input);

        return new PreparedResolution(
            orderedKeys,
            misses,
            firstInputByKey,
            cached);
    }

    private EmbeddingResolutionResult FinalizeResolution(
        PreparedResolution prepared,
        EmbeddingGenerationResult? generated)
    {
        Dictionary<EmbeddingCacheKey, StoredEmbedding> generatedByKey = new Dictionary<EmbeddingCacheKey, StoredEmbedding>();
        EmbeddingUsage usage = EmbeddingUsage.None;

        if (generated is not null)
        {
            if (generated.Embeddings.Count != prepared.Misses.Count)
                throw new InvalidDataException("The embedding provider returned an incomplete result set.");

            generatedByKey = generated.Embeddings.ToDictionary(embedding => embedding.Key);
            if (generatedByKey.Count != prepared.Misses.Count
                || prepared.Misses.Any(key => !generatedByKey.ContainsKey(key)))
            {
                throw new InvalidDataException("The embedding provider returned mismatched cache keys.");
            }

            _persistence.SaveEmbeddings(generated.Embeddings);
            usage = generated.Usage;
        }

        StoredEmbedding[] ordered = prepared.OrderedKeys.Select(key => prepared.Cached.TryGetValue(key, out StoredEmbedding? hit)
            ? hit
            : generatedByKey[key]).ToArray();
        return new EmbeddingResolutionResult(
            ordered,
            new DiagnosticReport(),
            prepared.OrderedKeys.Count(prepared.Cached.ContainsKey),
            prepared.OrderedKeys.Count(key => !prepared.Cached.ContainsKey(key)),
            usage);
    }

    /// <summary>
    /// The state a resolution reaches once its pure parts ran: every requested key, the keys the
    /// cache did not answer, the first input for each missed key, and what the cache returned.
    /// </summary>
    private sealed record PreparedResolution(
        IReadOnlyList<EmbeddingCacheKey> OrderedKeys,
        IReadOnlyList<EmbeddingCacheKey> Misses,
        IReadOnlyDictionary<EmbeddingCacheKey, string> FirstInputByKey,
        IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> Cached)
    {
        /// <summary>The texts to send for the missed keys, in miss order.</summary>
        public string[] Inputs => Misses.Select(key => FirstInputByKey[key]).ToArray();
    }

    private static EmbeddingResolutionResult Empty() =>
        new([], new DiagnosticReport(), 0, 0, EmbeddingUsage.None);
}
