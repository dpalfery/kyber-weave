using KyberDash.Application.Tests.Fakes;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Tokenization;

/// <summary>
/// Unit tests for <see cref="CachedTokenCounter"/>: every count carries its tokenizer's label,
/// cache keys are scoped by that label and the model named on the call, and a changed tokenizer
/// misses every earlier entry instead of serving another vocabulary's counts.
/// </summary>
/// <remarks>
/// The tests run against <see cref="FakeTextTokenizer"/>, which counts one token per
/// whitespace-separated word, so no figure here can pass on the retired characters-over-four
/// approximation.
/// </remarks>
public class CachedTokenCounterTests
{
    /// <summary>Text longer than <see cref="CanonTokenCache.MinCachedTextLength"/> in whole words.</summary>
    private static string LongWords(int words) => string.Join(" ", Enumerable.Repeat("token", words));

    /// <summary>
    /// Every count returns the tokenizer's own label and model-ownership flag, whether it was
    /// counted fresh or served from the memo.
    /// </summary>
    [Fact]
    public void Count_ReturnsTheTokenizerLabelForEachModel()
    {
        InMemoryRelationalStore store = new();
        FakeTextTokenizer fake = new("labelled_vocab", isModelOwn: true);
        CachedTokenCounter counter = new(store, fake);

        TokenCount uncached = counter.Count("hello world", "gpt-4o");
        TokenCount counted = counter.Count(LongWords(200), "gpt-4o");
        TokenCount memoized = counter.Count(LongWords(200), "gpt-4o");

        Assert.Equal(new TokenCount(2, "labelled_vocab", true), uncached);
        Assert.Equal(new TokenCount(200, "labelled_vocab", true), counted);
        Assert.Equal(counted, memoized);
    }

    /// <summary>
    /// A blank model never throws: the counter hands it to the tokenizer, whose stand-in rule
    /// labels the count, and the entry persists under the empty model slot.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Count_BlankModelCountsThroughTheTokenizerWithoutThrowing(string? model)
    {
        InMemoryRelationalStore store = new();
        FakeTextTokenizer fake = new();
        CachedTokenCounter counter = new(store, fake);

        TokenCount count = counter.Count(LongWords(200), model);

        Assert.Equal("fake_tokenizer", count.Tokenizer);
        Assert.Equal(200, count.Count);
        counter.Flush();
        Assert.Equal(count.Count, store.GetCachedTokenCount(CanonTokenCache.CacheKey(LongWords(200), model, "fake_tokenizer")));
    }

    /// <summary>
    /// A tokenizer change misses the cache: the label scopes the key, so the second counter
    /// recomputes and persists its own row, while the original tokenizer still hits.
    /// </summary>
    [Fact]
    public void Count_TokenizerChangeMissesEarlierEntries()
    {
        InMemoryRelationalStore store = new();
        string text = LongWords(200);

        CachedTokenCounter firstCounter = new(store, new FakeTextTokenizer("vocab_one"));
        TokenCount first = firstCounter.Count(text, "model-a");
        firstCounter.Flush();
        Assert.Equal("vocab_one", first.Tokenizer);

        CachedTokenCounter secondCounter = new(store, new FakeTextTokenizer("vocab_two"));
        TokenCount second = secondCounter.Count(text, "model-a");
        secondCounter.Flush();

        Assert.Equal("vocab_two", second.Tokenizer);
        Assert.Equal(1, secondCounter.Stats().Misses);
        Assert.Equal(0, secondCounter.Stats().Hits);
        Assert.Equal(2, store.CountRows("token_cache"));
        Assert.Equal(first.Count, store.GetCachedTokenCount(CanonTokenCache.CacheKey(text, "model-a", "vocab_one")));
        Assert.Equal(second.Count, store.GetCachedTokenCount(CanonTokenCache.CacheKey(text, "model-a", "vocab_two")));

        CachedTokenCounter returningCounter = new(store, new FakeTextTokenizer("vocab_one"));
        _ = returningCounter.Count(text, "model-a");
        Assert.Equal(0, returningCounter.Stats().Misses);
        Assert.Equal(1, returningCounter.Stats().Hits);
    }

    /// <summary>
    /// Counts above the cache threshold memoize, persist through <see cref="IOperationalStore"/>
    /// on flush, and a second counter reads the persisted row back without recounting the text.
    /// </summary>
    [Fact]
    public void Count_MemoizesAndPersistsAboveMinLengthThreshold()
    {
        Assert.Equal(512, CanonTokenCache.MinCachedTextLength);
        InMemoryRelationalStore store = new();
        FakeTextTokenizer fake = new();
        CachedTokenCounter counter = new(store, fake);

        string shortText = new('x', CanonTokenCache.MinCachedTextLength - 1);
        TokenCount shortCount1 = counter.Count(shortText, "model-a");
        TokenCount shortCount2 = counter.Count(shortText, "model-a");
        Assert.Equal(shortCount1, shortCount2);
        Assert.Equal(2, fake.Models.Count);
        Assert.Equal(2, counter.Stats().Uncached);
        Assert.Null(store.GetCachedTokenCount(CanonTokenCache.CacheKey(shortText, "model-a", "fake_tokenizer")));

        string longText = LongWords(200);
        string expectedKey = CanonTokenCache.CacheKey(longText, "model-a", "fake_tokenizer");

        TokenCount computed = counter.Count(longText, "model-a");
        TokenCount memoHit = counter.Count(longText, "model-a");
        Assert.Equal(new TokenCount(200, "fake_tokenizer", false), computed);
        Assert.Equal(computed, memoHit);

        // One real count plus one empty-string label probe is all the tokenizer sees for the model.
        Assert.Equal(4, fake.Models.Count);
        Assert.Equal(1, counter.Stats().Misses);
        Assert.Equal(1, counter.Stats().Hits);

        counter.Flush();
        Assert.Equal(computed.Count, store.GetCachedTokenCount(expectedKey));
        Assert.Equal(1, store.CountRows("token_cache"));
        Assert.Equal(1, store.CountRows("token_cache_model"));

        FakeTextTokenizer secondFake = new();
        CachedTokenCounter persistedReader = new(store, secondFake);
        TokenCount sqliteHit = persistedReader.Count(longText, "model-a");
        Assert.Equal(computed, sqliteHit);
        Assert.Single(secondFake.Models);
        Assert.Equal(1, persistedReader.Stats().Hits);
    }

    /// <summary>Buffered misses reach the store only on flush, and flushing again changes nothing.</summary>
    [Fact]
    public void Flush_PersistsBufferedMissesIntoTheStore()
    {
        InMemoryRelationalStore store = new();
        CachedTokenCounter counter = new(store, new FakeTextTokenizer(), maxMemoEntries: 0);

        string text = new('b', CanonTokenCache.MinCachedTextLength);
        TokenCount counted = counter.Count(text, "model-x");
        Assert.Equal(1, counted.Count);
        Assert.Null(store.GetCachedTokenCount(CanonTokenCache.CacheKey(text, "model-x", "fake_tokenizer")));

        counter.Flush();
        Assert.Equal(counted.Count, store.GetCachedTokenCount(CanonTokenCache.CacheKey(text, "model-x", "fake_tokenizer")));
        counter.Flush();
        Assert.Equal(counted.Count, store.GetCachedTokenCount(CanonTokenCache.CacheKey(text, "model-x", "fake_tokenizer")));
    }
}
