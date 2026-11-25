using System.Security.Cryptography;
using System.Text;

namespace KyberDash.Domain;

/// <summary>A token count derived from text, labelled with the tokenizer that produced it (decision D17).</summary>
/// <param name="Count">Number of tokens the tokenizer gives the text.</param>
/// <param name="Tokenizer">Name of the tokenizer that counted the text, such as <c>o200k_base</c>.</param>
/// <param name="IsModelOwn">
/// <see langword="true"/> when <paramref name="Tokenizer"/> is the model's own published tokenizer;
/// <see langword="false"/> when it is the named standard stand-in (D19).
/// </param>
public record TokenCount(
    long Count,
    string Tokenizer,
    bool IsModelOwn);

/// <summary>Cache hit/miss statistics reported by a cached token counter.</summary>
/// <param name="Hits">Lookups served from the in-process memo or SQLite cache.</param>
/// <param name="Misses">Lookups that computed a new count and queued persistence.</param>
/// <param name="Uncached">Lookups shorter than the cache threshold counted directly in memory.</param>
public record CachedCounterStats(int Hits, int Misses, int Uncached);

/// <summary>Pure token-count key derivation and 4-char heuristic fallback utilities.</summary>
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

    /// <summary>Computes the deterministic <c>&lt;model&gt;:&lt;sha256&gt;</c> memo key.</summary>
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
}
