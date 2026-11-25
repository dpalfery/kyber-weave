using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Parity tests for a span written by more than one source, as a forked Claude Code transcript and its
/// parent are, and for <see cref="IRelationalStoreWriter.DeleteProblem"/>: every case runs against
/// <see cref="SqliteRelationalStore"/> over a real database and against <see cref="InMemoryRelationalStore"/>.
/// </summary>
public class SharedSpanWriteParityTests : IDisposable
{
    private const string SpanId = "synth:claude-code:parent-session:msg_1";
    private const string SessionKey = "parent-session";
    private const string ForkLocator = "/home/dev/.claude/projects/repo/fork-session.jsonl";

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A second source writing a stored span replaces it, child rows included, and the first source
    /// writing it again takes it back: one record and one copy of its children throughout.
    /// </summary>
    /// <param name="kind">Store under test.</param>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void SpanWrittenByTwoSources_KeepsOneRecordWithTheLatestChildren(string kind)
    {
        StoreUnderTest store = Create(kind);

        store.Writer.WriteBatch(Batch("/home/dev/.claude/projects/repo/parent-session.jsonl", inputTokens: 10));
        store.Writer.WriteBatch(Batch("/home/dev/.claude/projects/repo/fork-session.jsonl", inputTokens: 20));

        AssertSingleRecordWithInputTokens(store, 20);

        store.Writer.WriteBatch(Batch("/home/dev/.claude/projects/repo/parent-session.jsonl", inputTokens: 10));

        AssertSingleRecordWithInputTokens(store, 10);
    }

    /// <summary>A span repeated inside one batch is still refused, and the store keeps what it had.</summary>
    /// <param name="kind">Store under test.</param>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void SpanRepeatedInsideOneBatch_IsRefused_AndTheStoreIsUnchanged(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteBatch(Batch("/home/dev/.claude/projects/repo/parent-session.jsonl", inputTokens: 10));

        CollectedBatch duplicated = new(
            Source: new CollectedSource("claude-code", SourceKind.File, "/home/dev/.claude/projects/repo/fork-session.jsonl"),
            Sessions: [],
            Records: [Record(20), Record(30)],
            Problems: []);

        DashValidationException refused = Assert.Throws<DashValidationException>(() => store.Writer.WriteBatch(duplicated));

        Assert.Equal(SqliteRelationalStore.ConstraintViolatedCode, refused.Code);
        AssertSingleRecordWithInputTokens(store, 10);
    }

    /// <summary>
    /// <see cref="IRelationalStoreWriter.ReplaceSource"/> whose write is refused leaves the source's stored
    /// records as they were, so a span it shares with another source is not lost to a failed re-import.
    /// </summary>
    /// <param name="kind">Store under test.</param>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void ReplaceSource_WhoseWriteIsRefused_KeepsTheStoredRecords(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteBatch(Batch(ForkLocator, inputTokens: 20));
        long markerBefore = store.Operational.GetWriteMarker();

        CollectedBatch refused = new(
            Source: new CollectedSource("claude-code", SourceKind.File, ForkLocator),
            Sessions: [],
            Records: [Record(30), Record(40)],
            Problems: []);

        Assert.Throws<DashValidationException>(() => store.Writer.ReplaceSource(refused));

        AssertSingleRecordWithInputTokens(store, 20);
        Assert.Equal(markerBefore, store.Operational.GetWriteMarker());
    }

    /// <summary>
    /// <see cref="IRelationalStoreWriter.ReplaceSource"/> drops the records the source no longer has, keeps one
    /// source row, and marks every session dirty, as <see cref="IRelationalStoreWriter.DeleteSource"/> does.
    /// </summary>
    /// <param name="kind">Store under test.</param>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void ReplaceSource_DropsRecordsTheSourceNoLongerHas_AndMarksEverySessionDirty(string kind)
    {
        StoreUnderTest store = Create(kind);
        CollectedRecord extra = Record(5) with { RecordId = SpanId + "-extra" };
        store.Writer.WriteBatch(Batch(ForkLocator, inputTokens: 20) with { Records = [Record(20), extra] });
        Assert.Equal(2, store.Reader.CountRecords("claude-code"));

        store.Writer.ReplaceSource(Batch(ForkLocator, inputTokens: 25));

        AssertSingleRecordWithInputTokens(store, 25);
        Assert.Single(store.Reader.Sources("claude-code"));
        DirtyProjectionState? dirty = store.Operational.GetProjectionDirtyState();
        Assert.NotNull(dirty);
        Assert.True(dirty.IsWildcard);
    }

    /// <summary><see cref="IRelationalStoreWriter.DeleteProblem"/> removes the keyed problem and ignores a missing key.</summary>
    /// <param name="kind">Store under test.</param>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("memory")]
    public void DeleteProblem_RemovesTheKeyedProblem_AndIgnoresAMissingKey(string kind)
    {
        StoreUnderTest store = Create(kind);
        store.Writer.WriteProblem(Problem("source-unreadable:claude-code:/a.jsonl"));
        store.Writer.WriteProblem(Problem("source-unreadable:claude-code:/b.jsonl"));

        store.Writer.DeleteProblem("source-unreadable:claude-code:/a.jsonl");
        store.Writer.DeleteProblem("source-unreadable:claude-code:/missing.jsonl");

        StoredIngestProblem left = Assert.Single(store.Operational.ListIngestProblems("claude-code"));
        Assert.Equal("source-unreadable:claude-code:/b.jsonl", left.Problem.ProblemKey);
    }

    private static void AssertSingleRecordWithInputTokens(StoreUnderTest store, long inputTokens)
    {
        Assert.Equal(1, store.Reader.CountRecords("claude-code"));
        CollectedRecord record = Assert.Single(store.Reader.SessionRecordsContentFree(SessionKey));
        Assert.Equal(SpanId, record.RecordId);
        CollectedTokenCount tokens = Assert.Single(record.Tokens);
        Assert.Equal(inputTokens, tokens.Count);
    }

    private StoreUnderTest Create(string kind)
    {
        if (string.Equals(kind, "sqlite", StringComparison.Ordinal))
        {
            SqliteRelationalStore sqlite = new(
                Path.Combine(directory.Path, $"shared-span-{Guid.NewGuid():N}.db"),
                new SqliteCircuitBreaker());
            return new StoreUnderTest(sqlite, sqlite, sqlite);
        }

        if (string.Equals(kind, "memory", StringComparison.Ordinal))
        {
            InMemoryRelationalStore memory = new();
            return new StoreUnderTest(memory, memory, memory);
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown store kind.");
    }

    private static CollectedBatch Batch(string locator, long inputTokens) => new(
        Source: new CollectedSource("claude-code", SourceKind.File, locator),
        Sessions: [new CollectedSession(SessionKey, "claude-code", SessionKey, Workspace: null, Title: null, ReportedUsage: [])],
        Records: [Record(inputTokens)],
        Problems: []);

    private static CollectedRecord Record(long inputTokens) => new(
        RecordId: SpanId,
        HarnessId: "claude-code",
        Op: RecordOp.LlmInvoke,
        Kind: RecordKind.Internal,
        Name: "claude:claude-sonnet-4-5",
        StartedAt: new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero),
        Status: RecordStatus.Ok,
        SessionKey: SessionKey,
        ParentRecordId: null,
        TraceId: null,
        Model: null,
        EndedAt: null,
        ReportedDurationMs: null,
        Tokens: [new CollectedTokenCount(TokenClass.Input, inputTokens)],
        ReportedCost: null,
        Parts: [],
        Tool: null,
        Agent: null,
        SkillsInvoked: [],
        AgentInstanceId: null,
        ToolDefinitions: [],
        Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null));

    private static CollectedProblem Problem(string key) => new(
        ProblemKey: key,
        Severity: "error",
        Code: "SOURCE_UNREADABLE",
        Message: "unreadable",
        SpanId: null,
        Location: null,
        SessionKey: null,
        HarnessId: "claude-code",
        TimestampUtc: "2026-11-25T11:00:00Z",
        Stack: null);

    private sealed record StoreUnderTest(
        IRelationalStoreWriter Writer,
        IRelationalStoreReader Reader,
        IOperationalStore Operational);
}
