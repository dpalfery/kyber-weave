using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Tokenization;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>
/// Unit tests for <see cref="LocalTextTokenizer"/> over the shipped vocabulary data packages.
/// </summary>
/// <remarks>
/// The vocabularies come from the <c>Microsoft.ML.Tokenizers.Data.*</c> packages loaded from the
/// assembly, so these tests make no network call. The pinned counts are the ones each vocabulary
/// gives; a text on which the two vocabularies disagree proves which one was selected, because
/// "hello world" counts the same in both.
/// </remarks>
public class LocalTextTokenizerTests
{
    private const string DivergentText = "こんにちは世界、今日はいい天気ですね。";

    private const long O200kDivergentCount = 9;

    private const long Cl100kDivergentCount = 16;

    private static readonly LocalTextTokenizer Tokenizer = LocalTextTokenizer.Shared;

    [Fact]
    public void HelloWorld_CountsAsBothVocabulariesDefineIt()
    {
        Assert.Equal(new TokenCount(2, "o200k_base", true), Tokenizer.Count("hello world", "gpt-4o"));
        Assert.Equal(new TokenCount(2, "cl100k_base", true), Tokenizer.Count("hello world", "gpt-4"));
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-5-codex")]
    public void CurrentOpenAiModels_SelectO200kBaseAsTheirOwn(string model)
    {
        TokenCount count = Tokenizer.Count(DivergentText, model);

        Assert.Equal("o200k_base", count.Tokenizer);
        Assert.True(count.IsModelOwn);
        Assert.Equal(O200kDivergentCount, count.Count);
    }

    [Fact]
    public void Gpt4_SelectsCl100kBaseAsItsOwn()
    {
        TokenCount count = Tokenizer.Count(DivergentText, "gpt-4");

        Assert.Equal("cl100k_base", count.Tokenizer);
        Assert.True(count.IsModelOwn);
        Assert.Equal(Cl100kDivergentCount, count.Count);
        Assert.NotEqual(O200kDivergentCount, count.Count);
    }

    [Theory]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("not-a-real-model-xyz")]
    [InlineData("text-davinci-002")]
    [InlineData("text-davinci-001")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ModelsWithNoPublishedTokenizer_UseTheLabelledStandIn(string? model)
    {
        TokenCount count = Tokenizer.Count(DivergentText, model);

        Assert.Equal(LocalTextTokenizer.StandardTokenizerName, count.Tokenizer);
        Assert.Equal("o200k_base", count.Tokenizer);
        Assert.False(count.IsModelOwn);
        Assert.Equal(O200kDivergentCount, count.Count);
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4")]
    [InlineData("claude-sonnet-4-20250514")]
    public void EmptyText_CountsZero(string model)
    {
        Assert.Equal(0, Tokenizer.Count(string.Empty, model).Count);
    }

    /// <summary>
    /// A blank model through the cached counter still counts under the labelled stand-in: the
    /// counter must never throw on a blank model, the label says stand-in, and the entry
    /// persists under the stand-in's name with an empty model slot.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankModels_CountUnderTheLabelledStandInThroughTheCachedCounter(string? model)
    {
        InMemoryRelationalStore store = new();
        CachedTokenCounter counter = new(store, Tokenizer);
        string text = string.Join(" ", Enumerable.Repeat("token", 200));

        TokenCount count = counter.Count(text, model);

        Assert.Equal(LocalTextTokenizer.StandardTokenizerName, count.Tokenizer);
        Assert.Equal(LocalTextTokenizer.O200kBaseName, count.Tokenizer);
        Assert.False(count.IsModelOwn);
        counter.Flush();
        Assert.Equal(count.Count, store.GetCachedTokenCount(CanonTokenCache.CacheKey(text, model, LocalTextTokenizer.O200kBaseName)));
    }

    /// <summary>
    /// Model names arrive from ingested data, so the per-model selection cache stops growing at its
    /// bound; a model past the bound is still resolved, and labelled, correctly.
    /// </summary>
    [Fact]
    public void DistinctModelsPastTheCacheBound_StillResolveWithoutGrowingTheCache()
    {
        LocalTextTokenizer tokenizer = new();
        TokenCount? last = null;
        for (int index = 0; index < LocalTextTokenizer.MaxCachedModels + 10; index++)
        {
            last = tokenizer.Count(DivergentText, $"gpt-4o-bound-{index}");
        }

        Assert.Equal(LocalTextTokenizer.MaxCachedModels, tokenizer.CachedModelCount);
        Assert.Equal(new TokenCount(O200kDivergentCount, "o200k_base", true), last);
    }

    [Fact]
    public void CountIsNotCharactersOverFour()
    {
        string text = "The quick brown fox jumps over the lazy dog. " + DivergentText;

        long count = Tokenizer.Count(text, "gpt-4o").Count;

        Assert.True(count > 0);
        Assert.NotEqual((text.Length + 3L) / 4L, count);
    }
}
