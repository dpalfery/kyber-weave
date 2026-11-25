using System.IO;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Both stores keep the tokenizer labels of a session-context turn's figures
/// (<c>session_context_turn_tokenizer</c>) and of a tool definition's measured schema tokens
/// (<c>tool_definition_tokenizer</c>), in tokenizer-name order, exactly as
/// <see cref="InMemoryRelationalStore"/> does. A turn with no label rows means its figures came
/// only from harness-reported counts, so it reads back with no labels and stays shown as
/// reported, never as not measured.
/// </summary>
public sealed class ContextTurnTokenizerStoreTests : IDisposable
{
    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose() => directory.Dispose();

    [Fact]
    public void BothStores_KeepPerTurnTokenizerLabelsInNameOrder_AndAUnlabelledTurnStaysUnlabelled()
    {
        SeamSqliteRelationalStore sqlite = new(Path.Combine(directory.Path, "turn-label", "canon.db"), new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        WriteSession(sqlite, "sess-turn-label", []);
        WriteSession(memory, "sess-turn-label", []);
        StoredSessionProjection projection = ProjectionWithLabelledTurns("sess-turn-label");

        sqlite.ReplaceSessionProjection(projection);
        memory.ReplaceSessionProjection(projection);

        StoredSessionProjection? sqliteRead = sqlite.GetSessionProjection("sess-turn-label");
        StoredSessionProjection? memoryRead = memory.GetSessionProjection("sess-turn-label");
        Assert.NotNull(sqliteRead);
        Assert.NotNull(memoryRead);

        List<StoredSessionContextTurn> turns = [.. sqliteRead.Context!.Turns];
        Assert.Equal(3, turns.Count);

        TokenizerLabel single = Assert.Single(turns[0].Tokenizers);
        Assert.Equal(new TokenizerLabel("o200k_base", true), single);

        Assert.Equal(
            [new TokenizerLabel("cl100k_base", false), new TokenizerLabel("o200k_base", true)],
            turns[1].Tokenizers);

        Assert.Empty(turns[2].Tokenizers);

        Assert.Equivalent(memoryRead.Context!.Turns, turns, strict: true);
    }

    [Fact]
    public void BothStores_KeepToolDefinitionTokenizerLabels_AndLeaveAnUnlabelledCountUnlabelled()
    {
        SeamSqliteRelationalStore sqlite = new(Path.Combine(directory.Path, "tool-label", "canon.db"), new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        CollectedRecord turn = ToolDefinitionTurn("span-tool-label", "sess-tool-label");
        WriteSession(sqlite, "sess-tool-label", [turn]);
        WriteSession(memory, "sess-tool-label", [turn]);

        IReadOnlyList<CollectedToolDefinition> sqliteDefs = sqlite.SessionRecordsForRebuild("sess-tool-label").Single().ToolDefinitions;
        IReadOnlyList<CollectedToolDefinition> memoryDefs = memory.SessionRecordsForRebuild("sess-tool-label").Single().ToolDefinitions;

        CollectedToolDefinition labelled = sqliteDefs.Single(static definition => definition.ToolName == "labelled_tool");
        Assert.Equal("o200k_base", labelled.Tokenizer);
        Assert.True(labelled.TokenizerIsModelOwn);

        CollectedToolDefinition unlabelled = sqliteDefs.Single(static definition => definition.ToolName == "unlabelled_tool");
        Assert.Null(unlabelled.Tokenizer);

        Assert.Equivalent(memoryDefs, sqliteDefs, strict: true);

        Assert.Equivalent(
            memory.ToolDefinitions(sessionKey: "sess-tool-label"),
            sqlite.ToolDefinitions(sessionKey: "sess-tool-label"),
            strict: true);
    }

    private static StoredSessionProjection ProjectionWithLabelledTurns(string sessionId) =>
        new(
            SessionId: sessionId,
            Label: "labelled turns",
            IsSubagent: false,
            SpanCount: 3,
            StartedAtUtc: ProjectionMinute(0),
            EndedAtUtc: ProjectionMinute(3),
            ParentSessionId: null,
            AgentName: null,
            IncludedInParent: false,
            RequestSpanIds: [],
            Turns: [],
            Context: new StoredSessionContext(
                ContextLimit: 10_000,
                ContextLimitSource: "telemetry",
                ResidualTotal: 0,
                FreshJumpFactor: 0,
                AccumulationRate: 0,
                ObservedBuckets: [],
                DerivedCounts: [],
                FlaggedTurnIndexes: [],
                Turns:
                [
                    new StoredSessionContextTurn(
                        Position: 0,
                        InputTokens: 900,
                        BucketedTokens: 40,
                        ResidualTokens: 860,
                        ResidualAttribution: "unattributed",
                        BuiltinToolDefinitionTokens: 0,
                        FreshInput: 900,
                        Buckets: [],
                        ServerDefinitions: [])
                    {
                        Tokenizers = [new TokenizerLabel("o200k_base", true)],
                    },
                    new StoredSessionContextTurn(
                        Position: 1,
                        InputTokens: 940,
                        BucketedTokens: 80,
                        ResidualTokens: 860,
                        ResidualAttribution: "unattributed",
                        BuiltinToolDefinitionTokens: 0,
                        FreshInput: 940,
                        Buckets: [],
                        ServerDefinitions: [])
                    {
                        Tokenizers =
                        [
                            new TokenizerLabel("o200k_base", true),
                            new TokenizerLabel("cl100k_base", false),
                        ],
                    },
                    new StoredSessionContextTurn(
                        Position: 2,
                        InputTokens: 980,
                        BucketedTokens: 0,
                        ResidualTokens: 980,
                        ResidualAttribution: "reported",
                        BuiltinToolDefinitionTokens: 0,
                        FreshInput: 980,
                        Buckets: [],
                        ServerDefinitions: []),
                ]));

    private static string ProjectionMinute(int minute) =>
        new DateTimeOffset(2026, 11, 24, 10, minute, 0, TimeSpan.Zero)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteSession(IRelationalStoreWriter store, string sessionKey, IReadOnlyList<CollectedRecord> records) =>
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions: [new CollectedSession(sessionKey, "claude-code", sessionKey, null, null, [])],
            Records: records,
            Problems: []));

    private static CollectedRecord ToolDefinitionTurn(string spanId, string sessionKey) =>
        new(
            RecordId: spanId,
            HarnessId: "claude-code",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: "claude-sonnet-4-6",
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions:
            [
                new CollectedToolDefinition(0, "labelled_tool", null, 120, Tokenizer: "o200k_base", TokenizerIsModelOwn: true),
                new CollectedToolDefinition(1, "unlabelled_tool", null, 80),
            ],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: null,
            ReportedOutputTokens: null);
}
