using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// <see cref="SqliteRelationalStore.ListSessionAggregates"/> must batch its per-session reads:
/// one <c>sqlite3</c> batch for sessions, one for all records, one for all projections, and at
/// most one fallback batch for turns of sessions without a projection, regardless of session count.
/// </summary>
public class SqliteRelationalStoreListAggregatesTests : IDisposable
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
    /// Verifies listing aggregates across projected and bare sessions issues a bounded number of
    /// <c>sqlite3</c> batches and matches the per-session aggregate for every session.
    /// </summary>
    [Fact]
    public void ListSessionAggregates_BatchesPerSessionReadsIntoABoundedNumberOfSqliteBatches()
    {
        string dbPath = Path.Combine(directory.Path, "aggregates", "canon.db");
        CountingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        SeedSessions(store);

        store.ResetCount();
        IReadOnlyList<SessionAggregateView> aggregates = store.ListSessionAggregates(HarnessId);
        int batches = store.ExecuteSqliteCoreCount;

        Assert.Equal(5, aggregates.Count);
        Assert.Equal(
            ["sess-agg-0", "sess-agg-1", "sess-agg-2", "sess-agg-3", "sess-agg-4"],
            aggregates.Select(static a => a.SessionKey).Order(StringComparer.Ordinal));
        Assert.True(batches <= 5, $"Expected at most 5 sqlite3 batches, observed {batches}.");

        foreach (SessionAggregateView aggregate in aggregates)
        {
            SessionAggregateView? single = store.GetSessionAggregate(aggregate.SessionKey);
            Assert.Equivalent(aggregate, single, strict: true);
        }
    }

    private static void SeedSessions(SqliteRelationalStore store)
    {
        DateTimeOffset t0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        List<CollectedSession> sessions = [];
        List<CollectedRecord> records = [];
        for (int i = 0; i < 5; i++)
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

        public int ExecuteSqliteCoreCount { get; private set; }

        public void ResetCount() => ExecuteSqliteCoreCount = 0;

        protected override string ExecuteSqliteCore(string sql)
        {
            ExecuteSqliteCoreCount++;
            return base.ExecuteSqliteCore(sql);
        }
    }
}
