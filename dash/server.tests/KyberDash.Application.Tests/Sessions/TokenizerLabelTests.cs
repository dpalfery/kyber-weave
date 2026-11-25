using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Sessions;

/// <summary>
/// A token count derived from text carries the tokenizer that produced it (decision D17): the
/// measurement hands the label to the withheld part, the store keeps it, hydration projects it
/// beside the count, and the bridge's content payloads answer <c>tokenizer</c> and
/// <c>tokenizerIsModelOwn</c> beside <c>tokens</c>. A stored text-derived count with no label
/// reads as not measured, so it is never shown as a count.
/// </summary>
public sealed class TokenizerLabelTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheWithheldTextCarriesTheMeasuringTokenizerAndWhetherItIsTheModelsOwn()
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(
            """{"a": "one two three"}""",
            new FakeTextTokenizer("o200k_base", isModelOwn: false),
            "claude-sonnet-4-6");

        CollectedWithheldText withheld = measurement.ToWithheldText();

        Assert.Equal("o200k_base", withheld.Tokenizer);
        Assert.False(withheld.TokenizerIsModelOwn);
        Assert.Equal(measurement.MeasuredTokens, withheld.LabeledTokens);

        ToolContentMeasurement own = ToolContentMeasurer.Measure(
            """{"a": 1}""",
            new FakeTextTokenizer("model_bpe", isModelOwn: true),
            "some-model");
        Assert.True(own.MeasuredTokenizerIsModelOwn);
        Assert.True(own.ToWithheldText().TokenizerIsModelOwn);
    }

    [Fact]
    public void AnUnlabelledWithheldCountReadsAsNotMeasured()
    {
        CollectedWithheldText unlabelled = new(Utf8Bytes: 40, Characters: 40, MeasuredTokens: 9);

        Assert.Null(unlabelled.Tokenizer);
        Assert.Null(unlabelled.LabeledTokens);
    }

    [Fact]
    public void HydrationProjectsTheLabelBesideTheCountAndDropsAnUnlabelledCount()
    {
        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(Turn("span-hydrate", "sess-hydrate", 0, LabelledParts()));

        ContentPart labelled = hydrated.Parts!.Single(static p => p.Order == 0);
        Assert.Equal(17, labelled.Tokens);
        Assert.Equal("o200k_base", labelled.Tokenizer);
        Assert.False(labelled.TokenizerIsModelOwn);

        ContentPart unlabelled = hydrated.Parts!.Single(static p => p.Order == 1);
        Assert.Null(unlabelled.Tokens);
        Assert.Null(unlabelled.Tokenizer);
        Assert.Null(unlabelled.TokenizerIsModelOwn);

        ContentPart reported = hydrated.Parts!.Single(static p => p.Order == 2);
        Assert.Equal(5, reported.Tokens);
        Assert.Null(reported.Tokenizer);
        Assert.Null(reported.TokenizerIsModelOwn);

        string json = JsonSerializer.Serialize(labelled);
        Assert.Contains("\"tokenizer\":\"o200k_base\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tokenizerIsModelOwn\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInMemoryStoreKeepsTheLabelAndAnUnlabelledPartStaysUnlabelled()
    {
        InMemoryRelationalStore store = new();
        WriteSession(store, "sess-store", [Turn("span-store", "sess-store", 0, LabelledParts())]);

        IReadOnlyList<CollectedContentPart> parts = store.FindRecordParts("span-store");

        CollectedWithheldText labelled = parts.Single(static p => p.Position == 0).Withheld!;
        Assert.Equal("o200k_base", labelled.Tokenizer);
        Assert.False(labelled.TokenizerIsModelOwn);
        Assert.Equal(17, labelled.LabeledTokens);

        CollectedWithheldText unlabelled = parts.Single(static p => p.Position == 1).Withheld!;
        Assert.Null(unlabelled.Tokenizer);
        Assert.Null(unlabelled.LabeledTokens);
    }

    [Fact]
    public void TheTurnContentPayloadCarriesTheTokenizerBesideEachTextDerivedCount()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-label-api";
        WriteSession(store, sessionKey, [Turn("span-label-api", sessionKey, 0, LabelledParts())]);
        _ = new CanonProjectionService(
            store,
            store,
            store,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing)).ProjectAll();
        KyberBridgeEngine bridge = new(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing),
            clock: () => TurnBase.AddHours(2),
            instanceId: "test-instance");
        bridge.EnsureProjected();

        JsonElement content = bridge.GetTurnContent(sessionKey, 0)!.Value;
        JsonElement[] parts = [.. content.GetProperty("parts").EnumerateArray()];
        Assert.Equal(3, parts.Length);

        JsonElement labelled = parts[0];
        Assert.Equal(17, labelled.GetProperty("tokens").GetInt64());
        Assert.Equal("o200k_base", labelled.GetProperty("tokenizer").GetString());
        Assert.False(labelled.GetProperty("tokenizerIsModelOwn").GetBoolean());

        JsonElement unlabelled = parts[1];
        Assert.False(unlabelled.TryGetProperty("tokens", out _));
        Assert.False(unlabelled.TryGetProperty("tokenizer", out _));
        Assert.False(unlabelled.TryGetProperty("tokenizerIsModelOwn", out _));

        JsonElement reported = parts[2];
        Assert.Equal(5, reported.GetProperty("tokens").GetInt64());
        Assert.False(reported.TryGetProperty("tokenizer", out _));
        Assert.False(reported.TryGetProperty("tokenizerIsModelOwn", out _));
    }

    private static IReadOnlyList<CollectedContentPart> LabelledParts() =>
    [
        Withheld(0, new CollectedWithheldText(64, 64, 17, Tokenizer: "o200k_base", TokenizerIsModelOwn: false)),
        Withheld(1, new CollectedWithheldText(40, 40, 9)),
        new CollectedContentPart(
            Position: 2,
            Bucket: ContentBucket.UserPrompt,
            PlainText: "reported text",
            Withheld: null,
            ReportedTokens: 5,
            Server: null,
            Role: "user",
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null),
    ];

    private static CollectedContentPart Withheld(int position, CollectedWithheldText withheld) =>
        new(
            Position: position,
            Bucket: ContentBucket.ToolResultContent,
            PlainText: null,
            Withheld: withheld,
            ReportedTokens: null,
            Server: null,
            Role: "tool",
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

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

    private static CollectedRecord Turn(string spanId, string sessionKey, int minuteOffset, IReadOnlyList<CollectedContentPart> parts) =>
        new(
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
            Model: "claude-sonnet-4-6",
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
            Parts: parts,
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: 1_000,
            ReportedOutputTokens: 10);
}
