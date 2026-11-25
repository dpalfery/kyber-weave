using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Sessions;

/// <summary>
/// The charts context payload answers the tokenizer label of every text-derived figure it
/// renders: one label names that tokenizer, more than one shows <c>mixed</c> with each entry and
/// a <see langword="null"/> <c>tokenizerIsModelOwn</c>, the session-level figures carry the union
/// of the turn labels, and a turn whose figures are all harness-reported carries no label keys
/// and stays shown as reported — never as not measured. A tool definition measured from stored
/// text rides its label through hydration into the turn's label.
/// </summary>
public sealed class ContextTurnTokenizerLabelTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SingleTokenizerTurn_CarriesItsLabelBesideItsFigures()
    {
        JsonElement context = ChartsContextOf(
            "sess-single",
            new FakeTextTokenizer("fake_tokenizer", isModelOwn: false),
            [PromptTurn("span-single", "sess-single", 0, Prompt("one two three"))]);

        JsonElement turn = Assert.Single(context.GetProperty("turns").EnumerateArray());
        Assert.Equal("fake_tokenizer", turn.GetProperty("tokenizer").GetString());
        JsonElement tokenizer = Assert.Single(turn.GetProperty("tokenizers").EnumerateArray());
        Assert.Equal("fake_tokenizer", tokenizer.GetProperty("name").GetString());
        Assert.False(tokenizer.GetProperty("isModelOwn").GetBoolean());
        Assert.False(turn.GetProperty("tokenizerIsModelOwn").GetBoolean());

        Assert.Equal("fake_tokenizer", context.GetProperty("tokenizer").GetString());
        Assert.Single(context.GetProperty("tokenizers").EnumerateArray());
        Assert.False(context.GetProperty("tokenizerIsModelOwn").GetBoolean());
    }

    [Fact]
    public void MixedFiguresTurn_ShowsMixedWithEveryTokenizerAndANullIsModelOwn()
    {
        JsonElement context = ChartsContextOf(
            "sess-mixed",
            new FakeTextTokenizer("fake_tokenizer", isModelOwn: true),
            [Turn(
                "span-mixed",
                "sess-mixed",
                0,
                "claude-sonnet-4-6",
                Prompt("one two three"),
                Withheld(1, new CollectedWithheldText(64, 64, 17, Tokenizer: "cl100k_base", TokenizerIsModelOwn: false)))]);

        JsonElement turn = Assert.Single(context.GetProperty("turns").EnumerateArray());
        Assert.Equal("mixed", turn.GetProperty("tokenizer").GetString());
        JsonElement[] tokenizers = [.. turn.GetProperty("tokenizers").EnumerateArray()];
        Assert.Equal(2, tokenizers.Length);
        Assert.Equal("cl100k_base", tokenizers[0].GetProperty("name").GetString());
        Assert.False(tokenizers[0].GetProperty("isModelOwn").GetBoolean());
        Assert.Equal("fake_tokenizer", tokenizers[1].GetProperty("name").GetString());
        Assert.True(tokenizers[1].GetProperty("isModelOwn").GetBoolean());
        Assert.Null(turn.GetProperty("tokenizerIsModelOwn").GetString());

        Assert.Equal("mixed", context.GetProperty("tokenizer").GetString());
        Assert.Equal(2, context.GetProperty("tokenizers").EnumerateArray().Count());
        Assert.Null(context.GetProperty("tokenizerIsModelOwn").GetString());
    }

    [Fact]
    public void SessionAcrossTokenizers_ShowsTheUnionOfTheTurnLabels()
    {
        JsonElement context = ChartsContextOf(
            "sess-across",
            new ModelChoosingTokenizer(),
            [
                Turn("span-across-a", "sess-across", 0, "gpt-4o", Prompt("one two three")),
                Turn("span-across-b", "sess-across", 1, "claude-sonnet-4-6", Prompt("four five six seven")),
            ]);

        JsonElement[] turns = [.. context.GetProperty("turns").EnumerateArray()];
        Assert.Equal(2, turns.Length);

        Assert.Equal("o200k_base", turns[0].GetProperty("tokenizer").GetString());
        Assert.True(turns[0].GetProperty("tokenizerIsModelOwn").GetBoolean());
        Assert.Equal("claude-standin", turns[1].GetProperty("tokenizer").GetString());
        Assert.False(turns[1].GetProperty("tokenizerIsModelOwn").GetBoolean());

        Assert.Equal("mixed", context.GetProperty("tokenizer").GetString());
        JsonElement[] union = [.. context.GetProperty("tokenizers").EnumerateArray()];
        Assert.Equal("claude-standin", union[0].GetProperty("name").GetString());
        Assert.False(union[0].GetProperty("isModelOwn").GetBoolean());
        Assert.Equal("o200k_base", union[1].GetProperty("name").GetString());
        Assert.True(union[1].GetProperty("isModelOwn").GetBoolean());
        Assert.Null(context.GetProperty("tokenizerIsModelOwn").GetString());
    }

    [Fact]
    public void ReportedOnlyTurn_ShowsAsReported_WithNoTokenizerLabels()
    {
        JsonElement context = ChartsContextOf(
            "sess-reported",
            new FakeTextTokenizer("fake_tokenizer", isModelOwn: false),
            [Turn(
                "span-reported",
                "sess-reported",
                0,
                "claude-sonnet-4-6",
                new CollectedContentPart(
                    Position: 0,
                    Bucket: ContentBucket.ConversationHistory,
                    PlainText: "reported text",
                    Withheld: null,
                    ReportedTokens: 40,
                    Server: null,
                    Role: "user",
                    Resident: null,
                    OriginalUtf8BytesWhenTruncated: null))]);

        Assert.True(context.GetProperty("measurable").GetBoolean());

        JsonElement turn = Assert.Single(context.GetProperty("turns").EnumerateArray());
        Assert.True(turn.TryGetProperty("inputTokens", out _));
        Assert.True(turn.TryGetProperty("reported_input", out _));
        Assert.Equal(40, turn.GetProperty("bucketedTokens").GetInt64());
        Assert.False(turn.TryGetProperty("tokenizer", out _));
        Assert.False(turn.TryGetProperty("tokenizers", out _));
        Assert.False(turn.TryGetProperty("tokenizerIsModelOwn", out _));

        Assert.False(context.TryGetProperty("tokenizer", out _));
        Assert.False(context.TryGetProperty("tokenizers", out _));
        Assert.False(context.TryGetProperty("tokenizerIsModelOwn", out _));
    }

    [Fact]
    public void StoredToolDefinitionLabel_RidesThroughHydrationIntoTheTurnLabel()
    {
        JsonElement context = ChartsContextOf(
            "sess-tool-def",
            new FakeTextTokenizer("fake_tokenizer", isModelOwn: false),
            [Record(
                "span-tool-def",
                "sess-tool-def",
                0,
                "claude-sonnet-4-6",
                Parts: [],
                ToolDefinitions:
                [
                    new CollectedToolDefinition(0, "labelled_tool", null, 120, Tokenizer: "o200k_base", TokenizerIsModelOwn: true),
                ])]);

        JsonElement turn = Assert.Single(context.GetProperty("turns").EnumerateArray());
        Assert.Equal(120, turn.GetProperty("builtinToolDefinitionTokens").GetInt64());
        Assert.Equal("o200k_base", turn.GetProperty("tokenizer").GetString());
        JsonElement tokenizer = Assert.Single(turn.GetProperty("tokenizers").EnumerateArray());
        Assert.Equal("o200k_base", tokenizer.GetProperty("name").GetString());
        Assert.True(tokenizer.GetProperty("isModelOwn").GetBoolean());
        Assert.True(turn.GetProperty("tokenizerIsModelOwn").GetBoolean());
    }

    private static JsonElement ChartsContextOf(
        string sessionKey,
        ITextTokenizer tokenizer,
        IReadOnlyList<CollectedRecord> records)
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        WriteSession(store, sessionKey, records);
        CanonSessionProjectorEngine projector = new(store, store, store, pricing, tokenizer);
        _ = new CanonProjectionService(store, store, store, projector, new CanonRollupsEngine(store, store, pricing))
            .ProjectAll();
        KyberBridgeEngine bridge = new(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing, tokenizer),
            new CanonRollupsEngine(store, store, pricing),
            clock: () => TurnBase.AddHours(2),
            instanceId: "test-instance");
        bridge.EnsureProjected();

        JsonElement charts = bridge.GetSessionCharts(sessionKey)!.Value;
        return charts.GetProperty("context");
    }

    /// <summary>Counts under the model's own tokenizer for <c>gpt*</c> models, a labelled stand-in otherwise.</summary>
    private sealed class ModelChoosingTokenizer : ITextTokenizer
    {
        public TokenCount Count(string text, string? model)
        {
            long words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength;
            return model is not null && model.StartsWith("gpt", StringComparison.Ordinal)
                ? new TokenCount(words, "o200k_base", true)
                : new TokenCount(words, "claude-standin", false);
        }
    }

    private static CollectedContentPart Prompt(string text) =>
        new(
            Position: 0,
            Bucket: ContentBucket.UserPrompt,
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: "user",
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static CollectedContentPart Withheld(int position, CollectedWithheldText withheld) =>
        new(
            Position: position,
            Bucket: ContentBucket.SystemPrompt,
            PlainText: null,
            Withheld: withheld,
            ReportedTokens: null,
            Server: null,
            Role: "system",
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static CollectedRecord PromptTurn(string spanId, string sessionKey, int minuteOffset, CollectedContentPart part) =>
        Turn(spanId, sessionKey, minuteOffset, "claude-sonnet-4-6", part);

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        int minuteOffset,
        string model,
        params CollectedContentPart[] parts) =>
        Record(spanId, sessionKey, minuteOffset, model, Parts: [.. parts], ToolDefinitions: []);

    private static CollectedRecord Record(
        string spanId,
        string sessionKey,
        int minuteOffset,
        string model,
        IReadOnlyList<CollectedContentPart> Parts,
        IReadOnlyList<CollectedToolDefinition> ToolDefinitions) =>
        new CollectedRecord(
            RecordId: spanId,
            HarnessId: "claude-code",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens:
            [
                new CollectedTokenCount(TokenClass.Input, 1_000),
                new CollectedTokenCount(TokenClass.CacheRead, 0),
                new CollectedTokenCount(TokenClass.CacheWrite, 0),
                new CollectedTokenCount(TokenClass.Output, 10),
            ],
            ReportedCost: null,
            Parts: Parts,
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: ToolDefinitions,
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: 1_000,
            ReportedOutputTokens: 10);

    private static void WriteSession(InMemoryRelationalStore store, string sessionKey, IReadOnlyList<CollectedRecord> records) =>
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: "claude-code",
                    NativeSessionId: sessionKey,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));
}
