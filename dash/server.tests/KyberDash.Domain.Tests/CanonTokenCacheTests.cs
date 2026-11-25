using KyberDash.Domain;
using Xunit;

namespace KyberDash.Domain.Tests;

/// <summary>
/// Unit tests for <see cref="CanonTokenCache"/> key derivation: every cache key names the
/// tokenizer that produced the count and the model it was counted for, and a blank model is
/// always legal.
/// </summary>
public class CanonTokenCacheTests
{
    /// <summary>The key carries all three scopes: the tokenizer, the model, and the text digest.</summary>
    [Fact]
    public void CacheKey_IncludesTokenizerModelAndTextDigest()
    {
        string key = CanonTokenCache.CacheKey("count me", "gpt-4o", "o200k_base");

        Assert.StartsWith("o200k_base:gpt-4o:", key, StringComparison.Ordinal);
        Assert.Equal(key, CanonTokenCache.CacheKey("count me", "gpt-4o", "o200k_base"));
        Assert.NotEqual(key, CanonTokenCache.CacheKey("count me", "gpt-4o", "cl100k_base"));
        Assert.NotEqual(key, CanonTokenCache.CacheKey("count me", "gpt-4o-mini", "o200k_base"));
        Assert.NotEqual(key, CanonTokenCache.CacheKey("count me again", "gpt-4o", "o200k_base"));
    }

    /// <summary>
    /// A blank model never throws: it takes an empty slot, because such counts run under the
    /// tokenizer's labelled stand-in rather than failing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CacheKey_BlankModelTakesAnEmptySlotInsteadOfThrowing(string? model)
    {
        string key = CanonTokenCache.CacheKey("count me", model, "o200k_base");

        Assert.StartsWith("o200k_base::", key, StringComparison.Ordinal);
        Assert.Equal(CanonTokenCache.CacheKey("count me", null, "o200k_base"), key);
        Assert.Equal(CanonTokenCache.CacheKey("count me", "   ", "o200k_base"), key);
    }

    /// <summary>Surrounding whitespace does not split one model's entries across two slots.</summary>
    [Fact]
    public void CacheKey_TrimsSurroundingWhitespaceFromTheModelSlot()
    {
        Assert.Equal(
            CanonTokenCache.CacheKey("count me", "gpt-4o", "o200k_base"),
            CanonTokenCache.CacheKey("count me", "  gpt-4o  ", "o200k_base"));
    }
}
