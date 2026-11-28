using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// <see cref="SqliteRelationalStore.SessionListFigures"/> must batch its per-session reads:
/// one <c>sqlite3</c> batch for sessions, one for the content-free record scalars, one for all
/// projections, and at most one fallback batch for turns of sessions without a projection,
/// regardless of session count up to one chunk of 240 sessions; past that, each further chunk adds
/// the same fixed number of statements, never one per session.
/// </summary>
public sealed class SqliteRelationalStoreListAggregatesTests : IDisposable
{
    private const string HarnessId = "perf-harness";

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Verifies the sessions-list read model issues a bounded number of <c>sqlite3</c> batches —
    /// one for sessions, content-free record scalars, projections, fallback turns, and ingest
    /// problems — and answers the same figures the per-session summary read answers for
    /// projected and bare sessions alike.
    /// </summary>
    [Fact]
    public void SessionListFigures_BatchTheListReadAndMatchPerSessionSummaryFigures()
    {
        string dbPath = Path.Combine(directory.Path, "list-figures", "canon.db");
        CountingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        SeedSessions(store);

        store.ResetCount();
        IReadOnlyList<SessionListFigure> figures = store.SessionListFigures(HarnessId);
        int batches = store.QueryRowsCount;

        Assert.Equal(5, figures.Count);
        Assert.Equal(
            ["sess-agg-0", "sess-agg-1", "sess-agg-2", "sess-agg-3", "sess-agg-4"],
            figures.Select(static figure => figure.SessionKey).Order(StringComparer.Ordinal));
        Assert.Equal(BatchesFor(240), batches);

        foreach (SessionListFigure figure in figures)
        {
            SessionSummaryFigure? summary = store.SessionSummaryFigures(figure.SessionKey);
            Assert.NotNull(summary);
            Assert.Equal(summary.SessionKey, figure.SessionKey);
            Assert.Equal(summary.HarnessId, figure.HarnessId);
            Assert.Equal(summary.TurnCount, figure.TurnCount);
            Assert.Equal(summary.RequestCount, figure.RequestCount);
            Assert.Equal(summary.TotalInput, figure.TotalInput);
            Assert.Equal(summary.TotalOutput, figure.TotalOutput);
            Assert.Equal(summary.Models, figure.Models);
            Assert.Equivalent(summary.ReportedCost, figure.ReportedCost, strict: true);
            Assert.Equivalent(summary.PublishedCost, figure.PublishedCost, strict: true);
            Assert.Equivalent(summary.CostBasisMismatch, figure.CostBasisMismatch, strict: true);
            Assert.Equal(summary.Problems.Count, figure.ProblemsCount);
        }

        // Projected sessions resolve turns through stored turns and bare ones through the
        // LLM fallback; both answer one turn here.
        Assert.Equal(1, figures.Single(static figure => figure.SessionKey == "sess-agg-0").TurnCount);
        Assert.Equal(1, figures.Single(static figure => figure.SessionKey == "sess-agg-2").TurnCount);
    }

    /// <summary>
    /// Verifies the list read's query count is flat within one 240-session chunk and grows by the
    /// same step for each further chunk: a per-session query would grow with every session instead.
    /// </summary>
    [Fact]
    public void SessionListFigures_IssueAFixedQueryCountPerChunkOfSessions()
    {
        // Two of the seeded sessions have a projection, so the fallback-turn read covers two fewer
        // keys; 242, 482 and 722 sessions put both key lists at a chunk boundary together.
        int few = BatchesFor(5);
        int oneChunk = BatchesFor(240);
        int twoChunks = BatchesFor(242);
        int threeChunks = BatchesFor(482);
        int fourChunks = BatchesFor(722);

        Assert.Equal(few, oneChunk);
        Assert.True(twoChunks > oneChunk, $"A second chunk issued no extra statement ({twoChunks}).");
        Assert.Equal(threeChunks - twoChunks, fourChunks - threeChunks);
    }

    /// <summary>
    /// Verifies a keyed read of more sessions than one chunk answers every session in storage order,
    /// the order one statement over all of them returns.
    /// </summary>
    [Fact]
    public void SessionListFigures_ForMoreKeysThanOneChunkKeepStorageOrder()
    {
        string dbPath = Path.Combine(directory.Path, "list-figures-keyed", "canon.db");
        CountingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        SeedSessions(store, 300);
        IReadOnlyList<SessionListFigure> scoped = store.SessionListFigures(HarnessId);

        IReadOnlyList<SessionListFigure> keyed = store.SessionListFigures([.. scoped.Select(static figure => figure.SessionKey).Reverse()]);

        Assert.Equal(scoped.Select(static figure => figure.SessionKey), keyed.Select(static figure => figure.SessionKey));
    }

    private int BatchesFor(int sessionCount)
    {
        string dbPath = Path.Combine(directory.Path, $"list-figures-{sessionCount}", "canon.db");
        CountingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        SeedSessions(store, sessionCount);
        store.ResetCount();
        Assert.Equal(sessionCount, store.SessionListFigures(HarnessId).Count);
        return store.QueryRowsCount;
    }

    private static void SeedSessions(SqliteRelationalStore store, int sessionCount = 5)
    {
        DateTimeOffset t0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        List<CollectedSession> sessions = [];
        List<CollectedRecord> records = [];
        for (int i = 0; i < sessionCount; i++)
        {
            string sessionKey = $"sess-agg-{i}";
            sessions.Add(new CollectedSession(sessionKey, HarnessId, $"native-{i}", null, null, []));
            records.Add(new CollectedRecord(
                RecordId: $"span-agg-{i}",
                HarnessId: HarnessId,
                Op: RecordOp.LlmInvoke,
                Kind: RecordKind.Client,
                Name: $"llm_call_{i}",
                StartedAt: t0.AddSeconds(i),
                Status: RecordStatus.Ok,
                SessionKey: sessionKey,
                ParentRecordId: null,
                TraceId: $"trace-{i}",
                Model: "perf-model",
                EndedAt: t0.AddSeconds(i + 1),
                ReportedDurationMs: 1000,
                Tokens: [new CollectedTokenCount(TokenClass.Input, 10), new CollectedTokenCount(TokenClass.Output, 5)],
                ReportedCost: null,
                Parts: [],
                Tool: null,
                Agent: null,
                SkillsInvoked: [],
                AgentInstanceId: null,
                ToolDefinitions: [],
                Signals: new CollectedRecordSignals(null, null, null, null),
                ReportedInputTokens: null,
                ReportedOutputTokens: null));
        }

        store.WriteBatch(new CollectedBatch(new CollectedSource(HarnessId, SourceKind.Otlp, "perf"), sessions, records, []));

        store.ReplaceSessionProjections(
        [
            new StoredSessionProjection(
                SessionId: "sess-agg-0",
                Label: "zero",
                IsSubagent: false,
                SpanCount: 1,
                StartedAtUtc: t0.ToString("O"),
                EndedAtUtc: t0.AddSeconds(1).ToString("O"),
                ParentSessionId: null,
                AgentName: null,
                IncludedInParent: false,
                RequestSpanIds: ["span-agg-0"],
                Turns: [new StoredSessionTurn("sess-agg-0", 0, "span-agg-0")]),
            new StoredSessionProjection(
                SessionId: "sess-agg-1",
                Label: "one",
                IsSubagent: false,
                SpanCount: 1,
                StartedAtUtc: t0.AddSeconds(1).ToString("O"),
                EndedAtUtc: t0.AddSeconds(2).ToString("O"),
                ParentSessionId: null,
                AgentName: null,
                IncludedInParent: false,
                RequestSpanIds: ["span-agg-1"],
                Turns: [new StoredSessionTurn("sess-agg-1", 0, "span-agg-1")]),
        ]);

    }

    private sealed class CountingSqliteRelationalStore : SqliteRelationalStore
    {
        public CountingSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
            : base(databasePath, circuitBreaker)
        {
        }

        public int QueryRowsCount { get; private set; }

        public void ResetCount() => QueryRowsCount = 0;

        protected override IReadOnlyList<T> QueryRows<T>(
            string sql,
            IReadOnlyDictionary<string, object?> parameters,
            Func<SqliteDataReader, T> map)
        {
            QueryRowsCount++;
            return base.QueryRows(sql, parameters, map);
        }
    }
}
