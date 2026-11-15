using System.Security.Cryptography;
using System.Text;

namespace KyberWeave.Core.Dash;

/// <summary>Derived token count tagged with the tokenizer model identity.</summary>
/// <param name="Count">Derived lower-bound token count.</param>
/// <param name="Model">Model/tokenizer name against which the text was counted.</param>
/// <param name="Derived">Always <see langword="true"/> so derived figures stay distinct from harness counters.</param>
public sealed record TokenCount(
    long Count,
    string Model,
    bool Derived = true);

/// <summary>Cache hit/miss statistics reported by <see cref="CachedTokenCounter"/>.</summary>
/// <param name="Hits">Lookups served from the in-process memo or SQLite cache.</param>
/// <param name="Misses">Lookups that computed a new count and queued persistence.</param>
/// <param name="Uncached">Lookups shorter than the cache threshold counted directly in memory.</param>
public sealed record CachedCounterStats(int Hits, int Misses, int Uncached);

/// <summary>Synchronous memoized token counter backed by the SQLite <c>token_cache</c> table.</summary>
public sealed class CachedTokenCounter
{
    private readonly CanonStore store;
    private readonly string model;
    private readonly Func<string, long> countTokens;
    private readonly int minLength;
    private readonly int maxMemoEntries;
    private readonly Dictionary<string, long> memo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> pending = new(StringComparer.Ordinal);
    private int hits;
    private int misses;
    private int uncached;

    internal CachedTokenCounter(
        CanonStore store,
        string model,
        Func<string, long> countTokens,
        int minLength,
        int maxMemoEntries)
    {
        this.store = store;
        this.model = model;
        this.countTokens = countTokens;
        this.minLength = minLength;
        this.maxMemoEntries = maxMemoEntries;
    }

    /// <summary>Counts tokens in <paramref name="text"/>, consulting in-process and SQLite caches.</summary>
    public long Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length < minLength)
        {
            uncached++;
            return countTokens(text);
        }

        string key = CanonTokenCache.CacheKey(text, model);
        if (memo.TryGetValue(key, out long memoized))
        {
            hits++;
            return memoized;
        }

        long? stored = store.GetCachedTokenCount(key);
        if (stored.HasValue)
        {
            hits++;
            if (memo.Count < maxMemoEntries)
                memo[key] = stored.Value;
            return stored.Value;
        }

        misses++;
        long computed = countTokens(text);
        if (computed < 0)
            throw new InvalidOperationException($"Tokenizer returned a negative count ({computed}) for model {model}.");

        if (memo.Count < maxMemoEntries)
            memo[key] = computed;
        pending[key] = computed;
        if (pending.Count >= CanonTokenCache.FlushThreshold)
            Flush();

        return computed;
    }

    /// <summary>Persists any buffered cache misses into SQLite in one transaction.</summary>
    public void Flush()
    {
        if (pending.Count == 0)
            return;

        store.PutCachedTokenCounts(pending, model);
        pending.Clear();
    }

    /// <summary>Returns cache hit, miss, and bypass statistics for this counter instance.</summary>
    public CachedCounterStats Stats() => new(hits, misses, uncached);
}

/// <summary>
/// Store-backed token-count memoization utilities mirroring <c>dash/src/canon/tokens.ts</c>.
/// </summary>
public static class CanonTokenCache
{
    /// <summary>Minimum string length consulted in SQLite <c>token_cache</c>.</summary>
    public const int MinCachedTextLength = 512;

    /// <summary>Maximum number of digest keys held in an in-process memo dictionary.</summary>
    public const int MaxMemoEntries = 500_000;

    /// <summary>Pending miss count that triggers an automatic SQLite batch flush.</summary>
    public const int FlushThreshold = 20_000;

    /// <summary>Tokenizer name reported when using the 4-char heuristic fallback.</summary>
    public const string ApproximateTokenizerName = "approximate/chars-per-4";

    /// <summary>
    /// Computes the deterministic <c>&lt;model&gt;:&lt;sha256&gt;</c> memo key matching
    /// <c>dash/src/canon/tokens.ts:cacheKey</c>.
    /// </summary>
    public static string CacheKey(string text, string model)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return $"{model}:{Convert.ToHexStringLower(digest)}";
    }

    /// <summary>Character-count approximation (<c>ceil(length / 4)</c>) matching <c>approximateO200kBase</c>.</summary>
    public static long ApproximateO200kBase(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text.Length + 3L) / 4L;
    }

    /// <summary>Tokenizes <paramref name="text"/> against <paramref name="model"/> with SQLite memoization.</summary>
    public static TokenCount Tokenize(
        string text,
        string model,
        CanonStore store,
        Func<string, long>? countTokens = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(store);

        string key = CacheKey(text, model);
        long? cached = store.GetCachedTokenCount(key);
        if (cached.HasValue)
            return new TokenCount(cached.Value, model, Derived: true);

        Func<string, long> counter = countTokens ?? ApproximateO200kBase;
        long computed = counter(text);
        if (computed < 0)
            throw new InvalidOperationException($"Tokenizer returned a negative count ({computed}) for model {model}.");

        store.PutCachedTokenCounts(
            new Dictionary<string, long>(StringComparer.Ordinal) { [key] = computed },
            model);
        return new TokenCount(computed, model, Derived: true);
    }

    /// <summary>Creates a bounded memoized token counter backed by <paramref name="store"/>.</summary>
    public static CachedTokenCounter CreateCachedCounter(
        CanonStore store,
        string model,
        Func<string, long>? countTokens = null,
        int minLength = MinCachedTextLength,
        int maxMemoEntries = MaxMemoEntries)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return new CachedTokenCounter(store, model, countTokens ?? ApproximateO200kBase, minLength, maxMemoEntries);
    }
}
