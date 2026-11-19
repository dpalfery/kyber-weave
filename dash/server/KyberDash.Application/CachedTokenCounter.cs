using KyberDash.Contracts.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Bounded memoized token counter backed by <see cref="IOperationalStore"/>.
/// </summary>
public class CachedTokenCounter
{
    private readonly IOperationalStore? operationalStore;
    private readonly ICanonStoreRepository? legacyStore;
    private readonly string model;
    private readonly Func<string, long> countTokens;
    private readonly int minLength;
    private readonly int maxMemoEntries;
    private readonly Dictionary<string, long> memo;
    private readonly Dictionary<string, long> pending;
    private readonly Lock gate = new();
    private int hits;
    private int misses;
    private int uncached;

    /// <summary>
    /// Initializes a new <see cref="CachedTokenCounter"/> for <paramref name="model"/> backed by <see cref="IOperationalStore"/>.
    /// </summary>
    /// <param name="store">Operational store backing cache persistence.</param>
    /// <param name="model">Model identifier scoping deterministic cache keys.</param>
    /// <param name="countTokens">Token counting function used on cache misses.</param>
    /// <param name="minLength">Minimum string length consulted in the persistent cache.</param>
    /// <param name="maxMemoEntries">Maximum number of digest keys held in the in-process memo.</param>
    public CachedTokenCounter(
        IOperationalStore store,
        string model,
        Func<string, long>? countTokens = null,
        int minLength = CanonTokenCache.MinCachedTextLength,
        int maxMemoEntries = CanonTokenCache.MaxMemoEntries)
        : this(model, countTokens, minLength, maxMemoEntries)
    {
        ArgumentNullException.ThrowIfNull(store);
        operationalStore = store;
    }

    /// <summary>
    /// Initializes a new <see cref="CachedTokenCounter"/> for <paramref name="model"/> backed by the transitional <see cref="ICanonStoreRepository"/>.
    /// </summary>
    /// <remarks>
    /// Retained temporarily until projector and ingest callers finish migrating off <see cref="ICanonStoreRepository"/>.
    /// </remarks>
    /// <param name="store">Canonical store repository backing cache persistence.</param>
    /// <param name="model">Model identifier scoping deterministic cache keys.</param>
    /// <param name="countTokens">Token counting function used on cache misses.</param>
    /// <param name="minLength">Minimum string length consulted in the persistent cache.</param>
    /// <param name="maxMemoEntries">Maximum number of digest keys held in the in-process memo.</param>
    public CachedTokenCounter(
        ICanonStoreRepository store,
        string model,
        Func<string, long>? countTokens = null,
        int minLength = CanonTokenCache.MinCachedTextLength,
        int maxMemoEntries = CanonTokenCache.MaxMemoEntries)
        : this(model, countTokens, minLength, maxMemoEntries)
    {
        ArgumentNullException.ThrowIfNull(store);
        legacyStore = store;
    }

    private CachedTokenCounter(
        string model,
        Func<string, long>? countTokens,
        int minLength,
        int maxMemoEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        this.model = model;
        this.countTokens = countTokens ?? CanonTokenCache.ApproximateO200kBase;
        this.minLength = minLength;
        this.maxMemoEntries = maxMemoEntries;
        memo = new Dictionary<string, long>(StringComparer.Ordinal);
        pending = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    /// <summary>Model identifier scoping deterministic cache keys.</summary>
    public string Model => model;

    /// <summary>Counts tokens in <paramref name="text"/>, consulting memo, store, then the counter.</summary>
    /// <param name="text">Text to count.</param>
    /// <returns>Token count for <paramref name="text"/> under <see cref="Model"/>.</returns>
    public long Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length < minLength)
        {
            lock (gate)
            {
                uncached++;
            }

            return countTokens(text);
        }

        string key = CanonTokenCache.CacheKey(text, model);
        lock (gate)
        {
            if (memo.TryGetValue(key, out long remembered))
            {
                hits++;
                return remembered;
            }
        }

        long? cached = ReadCachedTokenCount(key);
        if (cached.HasValue)
        {
            lock (gate)
            {
                hits++;
                if (memo.Count < maxMemoEntries)
                {
                    memo[key] = cached.Value;
                }
            }

            return cached.Value;
        }

        long computed = countTokens(text);
        if (computed < 0)
        {
            throw new InvalidOperationException($"Tokenizer returned a negative count ({computed}) for model {model}.");
        }

        lock (gate)
        {
            misses++;
            if (memo.Count < maxMemoEntries)
            {
                memo[key] = computed;
            }

            pending[key] = computed;
            if (pending.Count >= CanonTokenCache.FlushThreshold)
            {
                Dictionary<string, long> batch = new(pending, StringComparer.Ordinal);
                pending.Clear();
                FlushPendingBatch(batch);
            }
        }

        return computed;
    }

    /// <summary>Flushes buffered cache misses into the backing store.</summary>
    public void Flush()
    {
        lock (gate)
        {
            if (pending.Count == 0)
            {
                return;
            }

            Dictionary<string, long> batch = new(pending, StringComparer.Ordinal);
            pending.Clear();
            FlushPendingBatch(batch);
        }
    }

    /// <summary>Returns memoization hit/miss counters.</summary>
    /// <returns>Current hit, miss, and uncached counters.</returns>
    public CachedCounterStats Stats()
    {
        lock (gate)
        {
            return new CachedCounterStats(hits, misses, uncached);
        }
    }

    private long? ReadCachedTokenCount(string key) =>
        operationalStore is not null
            ? operationalStore.GetCachedTokenCount(key)
            : legacyStore is not null
                ? legacyStore.GetCachedTokenCount(key)
                : null;

    private void FlushPendingBatch(Dictionary<string, long> batch)
    {
        if (operationalStore is not null)
        {
            List<StoredTokenCacheEntry> entries = new(batch.Count);
            foreach (KeyValuePair<string, long> entry in batch)
            {
                entries.Add(new StoredTokenCacheEntry(entry.Key, entry.Value, ModelId: model));
            }

            operationalStore.PutCachedTokenCounts(entries);
            return;
        }

        if (legacyStore is not null)
        {
            legacyStore.PutCachedTokenCounts(batch, model);
        }
    }
}
