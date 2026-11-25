using System.IO;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// The SQLite store keeps the tokenizer label beside a withheld part's measured token count
/// (<c>content_part_withheld_tokenizer</c>) and reads a part without one back unlabelled, exactly
/// as <see cref="InMemoryRelationalStore"/> does, through both the record read and the scoped content read.
/// </summary>
public sealed class TokenizerLabelStoreTests : IDisposable
{
    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose() => directory.Dispose();

    [Fact]
    public void BothStoresKeepTheTokenizerLabelAndLeaveAnUnlabelledCountUnlabelled()
    {
        SeamSqliteRelationalStore sqlite = new(Path.Combine(directory.Path, "label", "canon.db"), new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        CollectedRecord turn = Turn("span-label", "sess-label", 0, LabelledParts());
        WriteSession(sqlite, "sess-label", [turn]);
        WriteSession(memory, "sess-label", [turn]);

        IReadOnlyList<CollectedContentPart> sqliteRecordParts = sqlite.SessionRecordsForRebuild("sess-label").Single().Parts;
        IReadOnlyList<CollectedContentPart> sqliteSessionParts =
            [.. sqlite.TurnContentParts("sess-label", spanId: "span-label", full: true).Select(static scoped => scoped.Part)];

        Assert.Equal(memory.SessionRecordsForRebuild("sess-label").Single().Parts, sqliteRecordParts);
        Assert.Equal(
            memory.TurnContentParts("sess-label", spanId: "span-label", full: true).Select(static scoped => scoped.Part),
            sqliteSessionParts);

        CollectedWithheldText labelled = sqliteRecordParts.Single(static p => p.Position == 0).Withheld!;
        Assert.Equal("o200k_base", labelled.Tokenizer);
        Assert.False(labelled.TokenizerIsModelOwn);
        Assert.Equal(17, labelled.LabeledTokens);

        CollectedWithheldText unlabelled = sqliteSessionParts.Single(static p => p.Position == 1).Withheld!;
        Assert.Null(unlabelled.Tokenizer);
        Assert.Null(unlabelled.LabeledTokens);
    }

    private static IReadOnlyList<CollectedContentPart> LabelledParts() =>
    [
        Withheld(0, new CollectedWithheldText(64, 64, 17, Tokenizer: "o200k_base", TokenizerIsModelOwn: false)),
        Withheld(1, new CollectedWithheldText(40, 40, 9)),
    ];

    private static CollectedContentPart Withheld(int position, CollectedWithheldText withheld) =>
        new(position, ContentBucket.ToolResultContent, null, withheld, null, null, "tool", null, null);

    private static void WriteSession(IRelationalStoreWriter store, string sessionKey, IReadOnlyList<CollectedRecord> records) =>
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions: [new CollectedSession(sessionKey, "claude-code", sessionKey, null, null, [])],
            Records: records,
            Problems: []));

    private static CollectedRecord Turn(string spanId, string sessionKey, int minuteOffset, IReadOnlyList<CollectedContentPart> parts) =>
        new(
            RecordId: spanId,
            HarnessId: "claude-code",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: new DateTimeOffset(2026, 11, 24, 10, minuteOffset, 0, TimeSpan.Zero),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: "claude-sonnet-4-6",
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens: [],
            ReportedCost: null,
            Parts: parts,
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: null,
            ReportedOutputTokens: null);
}
