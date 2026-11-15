using KyberDash.Contracts.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Bounded memoized token counter backed by <see cref="ICanonStoreRepository"/>.
/// </summary>
public class CachedTokenCounter
{
    private readonly ICanonStoreRepository store;
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
    /// Initializes a new <see cref="CachedTokenCounter"/> for <paramref name="model"/>.
    /// </summary>
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
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        this.store = store;
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

        long? cached = store.GetCachedTokenCount(key);
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
                store.PutCachedTokenCounts(batch, model);
            }
        }

        return computed;
    }

    /// <summary>Flushes buffered cache misses into the canonical store.</summary>
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
            store.PutCachedTokenCounts(batch, model);
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
}
