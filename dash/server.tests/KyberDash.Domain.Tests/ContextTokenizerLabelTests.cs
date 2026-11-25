using KyberDash.Domain;
using Xunit;

namespace KyberDash.Domain.Tests;

/// <summary>
/// The context analysis keeps the tokenizer label of every text-derived count: a turn counted under
/// one tokenizer carries that label, a turn or session that sums counts from several tokenizers
/// carries each of them, and a harness-reported count adds no label.
/// </summary>
public class ContextTokenizerLabelTests
{
    private static readonly ContextCountOptions Options = new(ContextLimit: 10_000, ContextLimitSource: "telemetry");

    /// <summary>A turn whose texts are all counted under one model's tokenizer carries that one label.</summary>
    [Fact]
    public void SingleTokenizerTurn_CarriesThatTokenizer()
    {
        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            [Turn(new ContextPart("system_prompt", "one two three", Model: "gpt-4o"))],
            Options,
            CountByModel);

        TokenizerLabel label = Assert.Single(context.Turns[0].Tokenizers);
        Assert.Equal(new TokenizerLabel("o200k_base", true), label);
        Assert.Equal([label], context.Tokenizers);
    }

    /// <summary>A turn whose texts went to two models carries both tokenizers, in name order.</summary>
    [Fact]
    public void MixedModelTurn_CarriesEveryTokenizer()
    {
        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            [Turn(
                new ContextPart("system_prompt", "one two", Model: "gpt-4o"),
                new ContextPart("conversation_history", "three four five", Model: "claude-sonnet-4"))],
            Options,
            CountByModel);

        Assert.Equal(
            [new TokenizerLabel("claude-standin", false), new TokenizerLabel("o200k_base", true)],
            context.Turns[0].Tokenizers);
        Assert.Equal(5, context.Turns[0].BucketedTokens);
    }

    /// <summary>The session label is the union of its turns' labels, each turn keeping its own.</summary>
    [Fact]
    public void SessionAcrossTokenizers_UnitesTheTurnLabels()
    {
        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            [
                Turn(new ContextPart("system_prompt", "one two", Model: "gpt-4o")),
                Turn(new ContextPart("system_prompt", "one two", Model: "claude-sonnet-4")),
            ],
            Options,
            CountByModel);

        Assert.Equal([new TokenizerLabel("o200k_base", true)], context.Turns[0].Tokenizers);
        Assert.Equal([new TokenizerLabel("claude-standin", false)], context.Turns[1].Tokenizers);
        Assert.Equal(
            [new TokenizerLabel("claude-standin", false), new TokenizerLabel("o200k_base", true)],
            context.Tokenizers);
    }

    /// <summary>A harness-reported part count adds no tokenizer label; a measured part keeps its own.</summary>
    [Fact]
    public void ReportedCount_AddsNoLabel_MeasuredPartKeepsItsLabel()
    {
        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            [Turn(
                new ContextPart("system_prompt", string.Empty, Tokens: 40),
                new ContextPart("tool_definitions", string.Empty, Tokens: 12, Tokenizer: "cl100k_base", TokenizerIsModelOwn: false))],
            Options,
            CountByModel);

        Assert.Equal([new TokenizerLabel("cl100k_base", false)], context.Turns[0].Tokenizers);
    }

    /// <summary>Reducing a part keeps the label of the count it produced.</summary>
    [Fact]
    public void ReduceContextParts_KeepsTheCountsLabel()
    {
        IReadOnlyList<ContextPart> reduced = CanonContextAnalysis.ReduceContextParts(
            [new ContextPart("conversation_history", "a b <system-reminder>c d</system-reminder>", Model: "gpt-4o")],
            CountByModel);

        ContextPart part = Assert.Single(reduced);
        Assert.Equal("o200k_base", part.Tokenizer);
        Assert.True(part.TokenizerIsModelOwn);
    }

    private static ContextTurn Turn(params ContextPart[] parts) => new(parts, InputTokens: 1_000, FreshInput: 1_000);

    private static TokenCount CountByModel(string text, string? model)
    {
        long words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength;
        return model is not null && model.StartsWith("gpt", StringComparison.Ordinal)
            ? new TokenCount(words, "o200k_base", true)
            : new TokenCount(words, "claude-standin", false);
    }
}
