using System.Collections.Concurrent;
using KyberDash.Contracts.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Bounded memoized token counter backed by <see cref="IOperationalStore"/> when one is supplied.
/// </summary>
/// <remarks>
/// A <see langword="null"/> store counts without persistence: misses are memoized in process only
/// and buffered counts are discarded on flush. That mode serves callers that have no operational
/// store of their own rather than silently inventing one.
/// <para>
/// Each call names the model the text was sent to, and the tokenizer names itself: every count
/// the counter returns is the <see cref="TokenCount"/> the tokenizer produced, and the same label
/// scopes the cache key, so an entry is stored under
/// <c>&lt;tokenizer&gt;:&lt;model&gt;:&lt;sha256&gt;</c> and a tokenizer change misses instead of
/// serving another vocabulary's counts. A blank model never throws; the tokenizer's labelled
/// stand-in counts it (D19).
/// </para>
/// </remarks>
public class CachedTokenCounter
{
    private readonly IOperationalStore? operationalStore;
    private readonly ITextTokenizer tokenizer;
    private readonly int minLength;
    private readonly int maxMemoEntries;
    private readonly ConcurrentDictionary<string, TokenCount> selections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenCount> memo;
    private readonly Dictionary<string, PendingCount> pending;
    private readonly Lock gate = new();

    /// <summary>
    /// Serialises persistence. Taken before <see cref="gate"/> and never while holding it, so a store
    /// write never blocks a concurrent count, and a flush returns only after every batch taken
    /// before it is written.
    /// </summary>
    private readonly Lock flushGate = new();
    private int hits;
    private int misses;
    private int uncached;

    /// <summary>
    /// Initializes a new <see cref="CachedTokenCounter"/> that counts with
    /// <paramref name="tokenizer"/> backed by <see cref="IOperationalStore"/>.
    /// </summary>
    /// <param name="store">Operational store backing cache persistence; <see langword="null"/> counts without persisting.</param>
    /// <param name="tokenizer">Local tokenizer that counts each miss and labels it (D17).</param>
    /// <param name="minLength">Minimum string length consulted in the persistent cache.</param>
    /// <param name="maxMemoEntries">Maximum number of digest keys held in the in-process memo.</param>
    public CachedTokenCounter(
        IOperationalStore? store,
        ITextTokenizer tokenizer,
        int minLength = CanonTokenCache.MinCachedTextLength,
        int maxMemoEntries = CanonTokenCache.MaxMemoEntries)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);

        operationalStore = store;
        this.tokenizer = tokenizer;
        this.minLength = minLength;
        this.maxMemoEntries = maxMemoEntries;
        memo = new Dictionary<string, TokenCount>(StringComparer.Ordinal);
        pending = new Dictionary<string, PendingCount>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Counts tokens in <paramref name="text"/> for <paramref name="model"/>, consulting memo,
    /// store, then the tokenizer, and returns the labelled count.
    /// </summary>
    /// <param name="text">Text to count.</param>
    /// <param name="model">Model the text was sent to; <see langword="null"/> or blank when unknown.</param>
    /// <returns>The tokenizer's count, labelled with the tokenizer that produced it.</returns>
    public TokenCount Count(string text, string? model)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length < minLength)
        {
            lock (gate)
            {
                uncached++;
            }

            return tokenizer.Count(text, model);
        }

        TokenCount selection = SelectionOf(model);
        string key = CanonTokenCache.CacheKey(text, model, selection.Tokenizer);
        lock (gate)
        {
            if (memo.TryGetValue(key, out TokenCount? remembered))
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
                    memo[key] = new TokenCount(cached.Value, selection.Tokenizer, selection.IsModelOwn);
                }
            }

            return new TokenCount(cached.Value, selection.Tokenizer, selection.IsModelOwn);
        }

        TokenCount computed = tokenizer.Count(text, model);
        if (computed.Count < 0)
        {
            throw new InvalidOperationException($"Tokenizer {computed.Tokenizer} returned a negative count ({computed.Count}) for model {CanonTokenCache.ModelSlot(model)}.");
        }

        bool flushDue;
        lock (gate)
        {
            misses++;
            if (memo.Count < maxMemoEntries)
            {
                memo[key] = computed;
            }

            pending[key] = new PendingCount(computed.Count, ModelIdOf(model));
            flushDue = pending.Count >= CanonTokenCache.FlushThreshold;
        }

        if (flushDue)
        {
            Flush();
        }

        return computed;
    }

    /// <summary>Flushes buffered cache misses into the backing store.</summary>
    /// <remarks>
    /// The buffer is taken under the counter's lock and written outside it, so concurrent counts
    /// never wait on the store. A failed write drops its batch and the exception propagates.
    /// </remarks>
    public void Flush()
    {
        lock (flushGate)
        {
            Dictionary<string, PendingCount> batch;
            lock (gate)
            {
                if (pending.Count == 0)
                {
                    return;
                }

                batch = new(pending, StringComparer.Ordinal);
                pending.Clear();
            }

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

    /// <summary>
    /// Resolves the label <paramref name="model"/>'s counts carry without counting the caller's
    /// text: the contract defines the empty string as zero tokens, so counting it names the
    /// model's tokenizer selection, once per model per counter.
    /// </summary>
    /// <param name="model">Model the text was sent to; <see langword="null"/> or blank when unknown.</param>
    private TokenCount SelectionOf(string? model) =>
        selections.GetOrAdd(CanonTokenCache.ModelSlot(model), slot => tokenizer.Count(string.Empty, slot));

    private long? ReadCachedTokenCount(string key) => operationalStore?.GetCachedTokenCount(key);

    private static string? ModelIdOf(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    private void FlushPendingBatch(Dictionary<string, PendingCount> batch)
    {
        if (operationalStore is null)
        {
            return;
        }

        List<StoredTokenCacheEntry> entries = new(batch.Count);
        foreach (KeyValuePair<string, PendingCount> entry in batch)
        {
            entries.Add(new StoredTokenCacheEntry(entry.Key, entry.Value.Count, entry.Value.ModelId));
        }

        operationalStore.PutCachedTokenCounts(entries);
    }

    /// <summary>A buffered miss awaiting persistence: its count and the model it was counted for.</summary>
    /// <param name="Count">Token count the tokenizer produced.</param>
    /// <param name="ModelId">Model the text was counted for; <see langword="null"/> when the model was blank.</param>
    private readonly record struct PendingCount(long Count, string? ModelId);
}
