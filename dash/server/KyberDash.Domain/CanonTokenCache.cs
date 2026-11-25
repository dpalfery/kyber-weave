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

    /// <summary>
    /// Computes the deterministic <c>&lt;tokenizer&gt;:&lt;model&gt;:&lt;sha256&gt;</c> memo key.
    /// </summary>
    /// <remarks>
    /// The tokenizer slot scopes the cache to the exact vocabulary that produced the count, so a
    /// tokenizer change misses every earlier entry instead of serving another vocabulary's counts.
    /// A blank model is legal and takes an empty slot: such counts run under the tokenizer's
    /// labelled stand-in (D19), never an exception.
    /// </remarks>
    /// <param name="text">Text the count was computed from.</param>
    /// <param name="model">Model the text was counted for; <see langword="null"/> or blank when unknown.</param>
    /// <param name="tokenizer">Name of the tokenizer that produced the count, such as <c>o200k_base</c>.</param>
    public static string CacheKey(string text, string? model, string tokenizer)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizer);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return $"{tokenizer}:{ModelSlot(model)}:{Convert.ToHexStringLower(digest)}";
    }

    /// <summary>
    /// Normalizes the model slot of a cache key: surrounding whitespace is trimmed and a blank
    /// model becomes the empty slot, so one model's entries never split across two keys.
    /// </summary>
    /// <param name="model">Model the text was counted for; <see langword="null"/> or blank when unknown.</param>
    public static string ModelSlot(string? model) =>
        string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();

    /// <summary>Character-count approximation (<c>ceil(length / 4)</c>) matching <c>approximateO200kBase</c>.</summary>
    public static long ApproximateO200kBase(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text.Length + 3L) / 4L;
    }
}
