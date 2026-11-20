using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Parity, absent-row, default-value, and pre-SQLite validation guard tests for
/// <see cref="IOperationalStore"/> across <see cref="SqliteRelationalStore"/> and
/// <see cref="InMemoryRelationalStore"/>.
/// </summary>
public class OperationalStoreTests : IDisposable
{
    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SqliteRelationalStore_IsPublicUnsealed_AndImplementsIOperationalStore()
    {
        Type storeType = typeof(SqliteRelationalStore);

        Assert.True(storeType.IsPublic);
        Assert.False(storeType.IsSealed);
        Assert.True(typeof(IOperationalStore).IsAssignableFrom(storeType));
        Assert.False(typeof(OperationalStoreTests).IsSealed);
    }

    [Fact]
    public void OperationalStore_EmptyDefaultsAndAbsentRowSemantics_MatchBetweenSqliteAndInMemory()
    {
        string dbPath = Path.Combine(directory.Path, "empty-ops", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();

        StoreStateView? initialSqliteState = sqlite.GetStoreState();
        Assert.Equal(new StoreStateView(SqliteRelationalStore.SchemaVersion, SqliteRelationalStore.DetectorVersion), initialSqliteState);
        Assert.Null(memory.GetStoreState());

        ScriptedSqliteRelationalStore scriptedAbsent = new(
            Path.Combine(directory.Path, "scripted-absent", "canon.db"),
            new SqliteCircuitBreaker());
        scriptedAbsent.EnqueueResponse(string.Empty);
        Assert.Null(scriptedAbsent.GetStoreState());

        sqlite.ClearRecordedSql();

        Assert.Equal(new SettingsView(false, false, 5, false, 4747), sqlite.ReadSettings());
        Assert.Equal(memory.ReadSettings(), sqlite.ReadSettings());

        Assert.Equal(0L, sqlite.GetWriteMarker());
        Assert.Equal(memory.GetWriteMarker(), sqlite.GetWriteMarker());

        Assert.Null(sqlite.GetLastCleanAtUtc());
        Assert.Equal(memory.GetLastCleanAtUtc(), sqlite.GetLastCleanAtUtc());

        Assert.Null(sqlite.GetContentRetentionDays());
        Assert.Equal(memory.GetContentRetentionDays(), sqlite.GetContentRetentionDays());

        Assert.Null(sqlite.GetContentPurgeMarkUtc());
        Assert.Equal(memory.GetContentPurgeMarkUtc(), sqlite.GetContentPurgeMarkUtc());

        Assert.Null(sqlite.GetProjectionState());
        Assert.Equal(memory.GetProjectionState(), sqlite.GetProjectionState());

        Assert.Null(sqlite.GetProjectionDirtyState());
        Assert.Equal(memory.GetProjectionDirtyState(), sqlite.GetProjectionDirtyState());

        Assert.Empty(sqlite.ListSessionKeyAliases());
        Assert.Empty(sqlite.ListSessionKeyAliases("sess-missing"));
        Assert.Equal(memory.ListSessionKeyAliases(), sqlite.ListSessionKeyAliases());

        Assert.Null(sqlite.GetRefreshRun("rr-missing"));
        Assert.Empty(sqlite.ListRefreshRuns());
        Assert.Equal(memory.ListRefreshRuns(), sqlite.ListRefreshRuns());

        Assert.Null(sqlite.GetSourceCheckpoint("claude-code", "src-missing"));
        Assert.Empty(sqlite.ListSourceCheckpoints());
        Assert.Empty(sqlite.ListSourceCheckpoints("claude-code"));

        Assert.Null(sqlite.GetIngestProblem(999));
        Assert.Empty(sqlite.ListIngestProblems());
        Assert.Equal(0, sqlite.CountIngestProblems());
        Assert.Equal(0, sqlite.CountIngestProblems("claude-code"));

        Assert.Null(sqlite.GetQuarantinedSpan("span-missing"));
        Assert.Empty(sqlite.ListQuarantinedSpans());
        Assert.Equal(0, sqlite.CountQuarantinedSpans());
        Assert.Equal(0, sqlite.CountQuarantinedSpans("claude-code"));

        Assert.Null(sqlite.GetCachedTokenCount("hash-missing"));

        StoredModelCatalogSnapshot emptyCatalog = sqlite.GetModelContextWindowCatalog();
        Assert.Equal("empty", emptyCatalog.Status);
        Assert.Empty(emptyCatalog.Vendors);
        Assert.Empty(emptyCatalog.Entries);
        Assert.Equal(memory.GetModelContextWindowCatalog().Status, emptyCatalog.Status);

        Assert.Empty(sqlite.ListMetricPoints());
        Assert.Empty(sqlite.ListMetricPoints("tokens.total", 5));

        Assert.Empty(sqlite.ListIngestLog());
        Assert.Empty(sqlite.ListIngestLog("/tmp/missing.jsonl"));

        Assert.Empty(sqlite.ListRecordProvenance("claude-code", "src-missing"));

        AssertAllScriptsUseTabbedHexWithoutJson(sqlite.ExecutedSql);
    }

    [Fact]
    public void OperationalStore_SettingsAndStateSingletons_RoundTripAndOverwriteInSqliteAndInMemory()
    {
        string dbPath = Path.Combine(directory.Path, "singletons", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        sqlite.ClearRecordedSql();

        SettingsView firstSettings = new(true, false, 15, true, 5151);
        SettingsView secondSettings = new(false, true, 60, false, 8080);
        ApplyBoth(sqlite, memory, s => s.SaveSettings(firstSettings));
        Assert.Equal(firstSettings, sqlite.ReadSettings());
        Assert.Equal(memory.ReadSettings(), sqlite.ReadSettings());

        ApplyBoth(sqlite, memory, s => s.SaveSettings(secondSettings));
        Assert.Equal(secondSettings, sqlite.ReadSettings());
        Assert.Equal(memory.ReadSettings(), sqlite.ReadSettings());

        StoreStateView updatedState = new(1, 9);
        ApplyBoth(sqlite, memory, s => s.SetStoreState(updatedState));
        Assert.Equal(updatedState, sqlite.GetStoreState());
        Assert.Equal(memory.GetStoreState(), sqlite.GetStoreState());

        ApplyBoth(sqlite, memory, s => s.SetWriteMarker(17));
        ApplyBoth(sqlite, memory, s => s.SetWriteMarker(42));
        Assert.Equal(42L, sqlite.GetWriteMarker());
        Assert.Equal(memory.GetWriteMarker(), sqlite.GetWriteMarker());

        ApplyBoth(sqlite, memory, s => s.SetLastCleanAtUtc("2026-11-19T08:00:00Z"));
        ApplyBoth(sqlite, memory, s => s.SetLastCleanAtUtc("2026-11-19T09:30:00Z"));
        Assert.Equal("2026-11-19T09:30:00Z", sqlite.GetLastCleanAtUtc());
        Assert.Equal(memory.GetLastCleanAtUtc(), sqlite.GetLastCleanAtUtc());

        ApplyBoth(sqlite, memory, s => s.SetContentRetentionDays(30));
        ApplyBoth(sqlite, memory, s => s.SetContentRetentionDays(14));
        Assert.Equal(14, sqlite.GetContentRetentionDays());
        Assert.Equal(memory.GetContentRetentionDays(), sqlite.GetContentRetentionDays());

        ApplyBoth(sqlite, memory, s => s.SetContentPurgeMarkUtc("2026-10-19T08:00:00Z"));
        Assert.Equal("2026-10-19T08:00:00Z", sqlite.GetContentPurgeMarkUtc());
        Assert.Equal(memory.GetContentPurgeMarkUtc(), sqlite.GetContentPurgeMarkUtc());

        ApplyBoth(sqlite, memory, s => s.SetProjectionState(new ProjectionStateView(7, 42)));
        Assert.Equal(new ProjectionStateView(7, 42), sqlite.GetProjectionState());
        Assert.Equal(memory.GetProjectionState(), sqlite.GetProjectionState());

        ApplyBoth(sqlite, memory, s => s.SetProjectionDirtyState(new DirtyProjectionState(10, 42, ["s1", "s2"], false)));
        DirtyProjectionState? keyedDirty = sqlite.GetProjectionDirtyState();
        DirtyProjectionState? memoryKeyedDirty = memory.GetProjectionDirtyState();
        Assert.NotNull(keyedDirty);
        Assert.NotNull(memoryKeyedDirty);
        Assert.Equal(10L, keyedDirty.FromSeq);
        Assert.Equal(42L, keyedDirty.ToSeq);
        Assert.False(keyedDirty.IsWildcard);
        Assert.Equal(["s1", "s2"], keyedDirty.Keys);
        Assert.Equal(memoryKeyedDirty.FromSeq, keyedDirty.FromSeq);
        Assert.Equal(memoryKeyedDirty.ToSeq, keyedDirty.ToSeq);
        Assert.Equal(memoryKeyedDirty.IsWildcard, keyedDirty.IsWildcard);
        Assert.Equal(memoryKeyedDirty.Keys, keyedDirty.Keys);

        ApplyBoth(sqlite, memory, s => s.SetProjectionDirtyState(new DirtyProjectionState(43, 50, null, true)));
        DirtyProjectionState? wildcardDirty = sqlite.GetProjectionDirtyState();
        DirtyProjectionState? memoryWildcardDirty = memory.GetProjectionDirtyState();
        Assert.NotNull(wildcardDirty);
        Assert.NotNull(memoryWildcardDirty);
        Assert.True(wildcardDirty.IsWildcard);
        Assert.Null(wildcardDirty.Keys);
        Assert.Equal(memoryWildcardDirty.FromSeq, wildcardDirty.FromSeq);
        Assert.Equal(memoryWildcardDirty.ToSeq, wildcardDirty.ToSeq);
        Assert.Equal(memoryWildcardDirty.IsWildcard, wildcardDirty.IsWildcard);

        ApplyBoth(sqlite, memory, s => s.ClearProjectionDirtyState());
        Assert.Null(sqlite.GetProjectionDirtyState());
        Assert.Null(memory.GetProjectionDirtyState());

        AssertAllScriptsUseTabbedHexWithoutJson(sqlite.ExecutedSql);
    }

    [Fact]
    public void OperationalStore_SessionKeyAliasesRefreshRunsAndCheckpoints_RoundTripUpdateAndFilter()
    {
        string dbPath = Path.Combine(directory.Path, "aliases-runs-checkpoints", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        sqlite.ClearRecordedSql();

        ApplyBoth(sqlite, memory, s => s.ReplaceSessionKeyAliases(
        [
            new SessionKeyAlias("alias-1", "sess-1"),
            new SessionKeyAlias("alias-2", "sess-1"),
            new SessionKeyAlias("alias-3", "sess-2"),
        ]));
        Assert.Equal(memory.ListSessionKeyAliases(), sqlite.ListSessionKeyAliases());
        Assert.Equal(memory.ListSessionKeyAliases("sess-1"), sqlite.ListSessionKeyAliases("sess-1"));

        ApplyBoth(sqlite, memory, s => s.ReplaceSessionKeyAliases([new SessionKeyAlias("alias-4", "sess-3")]));
        Assert.Single(sqlite.ListSessionKeyAliases());
        Assert.Empty(sqlite.ListSessionKeyAliases("sess-1"));
        Assert.Equal(memory.ListSessionKeyAliases(), sqlite.ListSessionKeyAliases());

        RefreshRunRow rr1 = new("rr-1", "2026-11-19T08:00:00Z", "2026-11-19T08:01:00Z", "ok", 123, "manual", "1 unit\tdone", 2);
        RefreshRunRow rr2 = new("rr-2", "2026-11-19T09:00:00Z", null, "running", 456, "timer", null, null);
        ApplyBoth(sqlite, memory, s => s.UpsertRefreshRun(rr1));
        ApplyBoth(sqlite, memory, s => s.UpsertRefreshRun(rr2));

        Assert.Equal(rr1, sqlite.GetRefreshRun("rr-1"));
        Assert.Equal(rr2, sqlite.GetRefreshRun("rr-2"));
        Assert.Equal(memory.ListRefreshRuns(1), sqlite.ListRefreshRuns(1));
        Assert.Equal(memory.ListRefreshRuns(10), sqlite.ListRefreshRuns(10));

        RefreshRunRow rr1ClearedOptionals = new("rr-1", "2026-11-19T08:00:00Z", null, "failed", 789, "manual", null, null);
        ApplyBoth(sqlite, memory, s => s.UpsertRefreshRun(rr1ClearedOptionals));
        Assert.Equal(rr1ClearedOptionals, sqlite.GetRefreshRun("rr-1"));
        Assert.Equal(memory.GetRefreshRun("rr-1"), sqlite.GetRefreshRun("rr-1"));

        SourceCheckpoint cp1 = new(
            "claude-code",
            "src-1",
            "anthropic",
            "jsonl",
            "1.0",
            "jsonl",
            string.Empty,
            string.Empty,
            "2026-11-19T00:00:00Z",
            "2026-11-19T08:00:00Z",
            "2026-11-19T08:00:00Z",
            "2026-11-19T08:00:00Z",
            "ok",
            "WARN_NONE",
            2,
            14);
        SourceCheckpoint cp2 = new(
            "codex",
            "src-2",
            "openai",
            "jsonl",
            "1.1",
            "jsonl",
            "projects/main",
            "rev-2",
            "2026-11-19T01:00:00Z",
            "2026-11-19T08:30:00Z",
            "2026-11-19T08:30:00Z",
            null,
            "error",
            "E_IO",
            1,
            0);

        ApplyBoth(sqlite, memory, s => s.UpsertSourceCheckpoint(cp1));
        ApplyBoth(sqlite, memory, s => s.UpsertSourceCheckpoint(cp2));
        Assert.Equal(cp1, sqlite.GetSourceCheckpoint("claude-code", "src-1"));
        Assert.Equal(cp2, sqlite.GetSourceCheckpoint("codex", "src-2"));
        Assert.Equal(memory.ListSourceCheckpoints(), sqlite.ListSourceCheckpoints());
        Assert.Equal(memory.ListSourceCheckpoints("claude-code"), sqlite.ListSourceCheckpoints("claude-code"));

        SourceCheckpoint cp1Updated = cp1 with { LastSuccessUtc = null, LastErrorCode = null, RecordCount = 20 };
        ApplyBoth(sqlite, memory, s => s.UpsertSourceCheckpoint(cp1Updated));
        Assert.Equal(cp1Updated, sqlite.GetSourceCheckpoint("claude-code", "src-1"));
        Assert.Equal(memory.GetSourceCheckpoint("claude-code", "src-1"), sqlite.GetSourceCheckpoint("claude-code", "src-1"));

        AssertAllScriptsUseTabbedHexWithoutJson(sqlite.ExecutedSql);
    }

    [Fact]
    public void OperationalStore_IngestProblemsAndQuarantinedSpans_RoundTripFilterPaginateWithoutRawPayload()
    {
        foreach (string tableName in new[]
        {
            "quarantined_span",
            "quarantined_span_namespace",
            "quarantined_span_source",
            "quarantined_span_name",
            "quarantined_span_timestamp",
            "quarantined_span_harness",
        })
        {
            TableDefinition def = RelationalSchemaCatalog.GetTable(tableName);
            foreach (ColumnDefinition col in def.Columns)
            {
                Assert.DoesNotContain("payload", col.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("raw", col.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        string dbPath = Path.Combine(directory.Path, "problems-quarantine", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        sqlite.ClearRecordedSql();

        CollectedProblem p1 = new("pk-1", "error", "E1", "bad line 1", "span-1", "line:1", "sess-1", "claude-code", "2026-11-19T08:00:00Z", "stack-1");
        CollectedProblem p2 = new("pk-2", "warning", "W1", "bad line 2", "span-2", null, null, "claude-code", null, null);
        CollectedProblem p3 = new("pk-3", "error", "E2", "bad line 3", null, "line:9", "sess-2", "codex", "2026-11-19T08:02:00Z", null);

        sqlite.WriteProblem(p1);
        memory.WriteProblem(p1);
        sqlite.WriteProblem(p2);
        memory.WriteProblem(p2);
        sqlite.WriteProblem(p3);
        memory.WriteProblem(p3);

        Assert.Equal(3, sqlite.CountIngestProblems());
        Assert.Equal(2, sqlite.CountIngestProblems("claude-code"));
        Assert.Equal(1, sqlite.CountIngestProblems("codex"));
        Assert.Equal(memory.CountIngestProblems("claude-code"), sqlite.CountIngestProblems("claude-code"));

        IReadOnlyList<StoredIngestProblem> allProblems = sqlite.ListIngestProblems();
        Assert.Equal(memory.ListIngestProblems(), allProblems);
        Assert.Equal(allProblems[1], sqlite.GetIngestProblem(allProblems[1].ProblemId));
        Assert.Equal(memory.ListIngestProblems(harnessId: "claude-code", limit: 1, offset: 1), sqlite.ListIngestProblems(harnessId: "claude-code", limit: 1, offset: 1));
        Assert.Equal(memory.ListIngestProblems(spanId: "span-1"), sqlite.ListIngestProblems(spanId: "span-1"));
        Assert.Empty(sqlite.ListIngestProblems(limit: 0));

        CollectedProblem p1Updated = new("pk-1", "warning", "E1_UPDATED", "updated msg", null, null, null, null, null, null);
        sqlite.WriteProblem(p1Updated);
        memory.WriteProblem(p1Updated);
        StoredIngestProblem? reloadedP1 = sqlite.GetIngestProblem(allProblems[0].ProblemId);
        Assert.NotNull(reloadedP1);
        Assert.Equal(p1Updated, reloadedP1.Problem);
        Assert.Equal(memory.GetIngestProblem(allProblems[0].ProblemId), reloadedP1);

        StoredQuarantinedSpan q1 = new("q-1", "unclaimed", ["custom.ns", "vendor.ext"], "otlp", "custom.op", "2026-11-19T08:00:00Z", "claude-code");
        StoredQuarantinedSpan q2 = new("q-2", "unsupported_version", ["gen_ai"], null, "span.op", null, "claude-code");
        StoredQuarantinedSpan q3 = new("q-3", "non_model", [], "file", null, "2026-11-19T08:05:00Z", "codex");

        ApplyBoth(sqlite, memory, s => s.PutQuarantinedSpan(q1));
        ApplyBoth(sqlite, memory, s => s.PutQuarantinedSpan(q2));
        ApplyBoth(sqlite, memory, s => s.PutQuarantinedSpan(q3));

        Assert.Equal(3, sqlite.CountQuarantinedSpans());
        Assert.Equal(2, sqlite.CountQuarantinedSpans("claude-code"));
        Assert.Equal(memory.CountQuarantinedSpans("claude-code"), sqlite.CountQuarantinedSpans("claude-code"));

        AssertQuarantinedSpanEqual(memory.GetQuarantinedSpan("q-1"), sqlite.GetQuarantinedSpan("q-1"));
        AssertQuarantinedSpanEqual(memory.GetQuarantinedSpan("q-3"), sqlite.GetQuarantinedSpan("q-3"));

        AssertQuarantinedSpanListEqual(memory.ListQuarantinedSpans(), sqlite.ListQuarantinedSpans());
        AssertQuarantinedSpanListEqual(
            memory.ListQuarantinedSpans(harnessId: "claude-code", limit: 1, offset: 1),
            sqlite.ListQuarantinedSpans(harnessId: "claude-code", limit: 1, offset: 1));
        Assert.Empty(sqlite.ListQuarantinedSpans(limit: 0));

        StoredQuarantinedSpan q1Updated = new("q-1", "reclassified", ["only.ns"], null, null, null, null);
        ApplyBoth(sqlite, memory, s => s.PutQuarantinedSpan(q1Updated));
        AssertQuarantinedSpanEqual(memory.GetQuarantinedSpan("q-1"), sqlite.GetQuarantinedSpan("q-1"));
        Assert.Equal(1, sqlite.CountQuarantinedSpans("claude-code"));

        AssertAllScriptsUseTabbedHexWithoutJson(sqlite.ExecutedSql);
    }

    [Fact]
    public void OperationalStore_TokenCacheCatalogMetricPointsIngestLogAndProvenance_RoundTripAndFilter()
    {
        string dbPath = Path.Combine(directory.Path, "cache-catalog-metrics", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        sqlite.ClearRecordedSql();

        ApplyBoth(sqlite, memory, s => s.PutCachedTokenCounts(
        [
            new StoredTokenCacheEntry("hash-1", 123, "claude-sonnet-4-5"),
            new StoredTokenCacheEntry("hash-2", 456, null),
        ]));
        Assert.Equal(123L, sqlite.GetCachedTokenCount("hash-1"));
        Assert.Equal(456L, sqlite.GetCachedTokenCount("hash-2"));

        ApplyBoth(sqlite, memory, s => s.PutCachedTokenCounts([new StoredTokenCacheEntry("hash-1", 789, null)]));
        Assert.Equal(789L, sqlite.GetCachedTokenCount("hash-1"));
        Assert.Equal(memory.GetCachedTokenCount("hash-1"), sqlite.GetCachedTokenCount("hash-1"));

        StoredModelCatalogSnapshot firstCatalog = new(
            "fresh",
            [
                new StoredModelCatalogVendorStatus("anthropic", "ok", "2026-11-19T08:00:00Z", null),
                new StoredModelCatalogVendorStatus("openai", "degraded", null, "timeout"),
            ],
            [
                new StoredModelContextWindowEntry("claude-sonnet-4-5", "claude-sonnet-4-5", 200000, "anthropic", new Uri("https://docs.anthropic.com/models"), "2026-11-19T08:00:00Z", "rev-1"),
                new StoredModelContextWindowEntry("gpt-5", "gpt-5", 128000, "openai", new Uri("https://platform.openai.com/docs/models"), "2026-11-19T08:00:00Z", null),
            ]);
        ApplyBoth(sqlite, memory, s => s.ReplaceModelContextWindowCatalog(firstCatalog));
        AssertCatalogEqual(memory.GetModelContextWindowCatalog(), sqlite.GetModelContextWindowCatalog());

        StoredModelCatalogSnapshot secondCatalog = new(
            "stale",
            [new StoredModelCatalogVendorStatus("anthropic", "ok", "2026-11-19T09:00:00Z", null)],
            [new StoredModelContextWindowEntry("claude-opus-4-1", "claude-opus-4-1", 200000, "anthropic", new Uri("https://docs.anthropic.com/opus"), "2026-11-19T09:00:00Z", null)]);
        ApplyBoth(sqlite, memory, s => s.ReplaceModelContextWindowCatalog(secondCatalog));
        AssertCatalogEqual(memory.GetModelContextWindowCatalog(), sqlite.GetModelContextWindowCatalog());

        StoredMetricPoint mp1 = new("mp-1", "tokens.total", "sum", "tokens", 2, "1000", "900", "2026-11-19T08:00:00Z", 55.5, 3, 55.5, "otlp");
        StoredMetricPoint mp2 = new("mp-2", "latency.ms", "gauge", null, null, null, null, null, 12.25, null, null, null);
        ApplyBoth(sqlite, memory, s => s.PutMetricPoints([mp1, mp2]));
        Assert.Equal(memory.ListMetricPoints(), sqlite.ListMetricPoints());
        Assert.Equal(memory.ListMetricPoints("tokens.total"), sqlite.ListMetricPoints("tokens.total"));
        Assert.Equal(memory.ListMetricPoints(limit: 1), sqlite.ListMetricPoints(limit: 1));
        Assert.Empty(sqlite.ListMetricPoints(limit: 0));

        StoredMetricPoint mp1Cleared = new("mp-1", "tokens.total", "sum");
        ApplyBoth(sqlite, memory, s => s.PutMetricPoints([mp1Cleared]));
        Assert.Equal(memory.ListMetricPoints("tokens.total"), sqlite.ListMetricPoints("tokens.total"));

        ApplyBoth(sqlite, memory, s => s.RecordIngest("/tmp/a.jsonl", 5, "2026-11-19T08:00:00Z"));
        ApplyBoth(sqlite, memory, s => s.RecordIngest("/tmp/b.jsonl", 0, "2026-11-19T08:05:00Z"));
        Assert.Equal(memory.ListIngestLog(), sqlite.ListIngestLog());
        Assert.Equal(memory.ListIngestLog("/tmp/a.jsonl"), sqlite.ListIngestLog("/tmp/a.jsonl"));

        CollectedSource sourceA = new("claude-code", SourceKind.File, "/tmp/prov-a.jsonl");
        CollectedRecord recA = CreateMinimalRecord("span-prov-a", "claude-code");
        CollectedBatch batchA = new(sourceA, [], [recA], []);
        sqlite.WriteBatch(batchA);
        memory.WriteBatch(batchA);

        RecordProvenanceRow provWithOptionals = new("span-prov-a", "claude-code", "src-a", "nat-sess-a", "nat-rec-a", "rev-a", "1.0", "2026-11-19T08:00:00Z", "line:1");
        ApplyBoth(sqlite, memory, s => s.PutRecordProvenance([provWithOptionals]));
        Assert.Equal(memory.ListRecordProvenance("claude-code", "src-a"), sqlite.ListRecordProvenance("claude-code", "src-a"));

        RecordProvenanceRow provMinimal = new("span-prov-a", "claude-code", "src-a", null, null, string.Empty, string.Empty, "2026-11-19T08:01:00Z", string.Empty);
        ApplyBoth(sqlite, memory, s => s.PutRecordProvenance([provMinimal]));
        Assert.Equal(memory.ListRecordProvenance("claude-code", "src-a"), sqlite.ListRecordProvenance("claude-code", "src-a"));

        string sourceIdA = Assert.Single(sqlite.Sources("claude-code")).SourceId;
        sqlite.DeleteSource(sourceIdA);
        memory.DeleteSource(sourceIdA);
        Assert.Empty(sqlite.ListRecordProvenance("claude-code", "src-a"));
        Assert.Empty(memory.ListRecordProvenance("claude-code", "src-a"));

        AssertAllScriptsUseTabbedHexWithoutJson(sqlite.ExecutedSql);
    }

    [Theory]
    [InlineData("{\"kind\":\"object\"}")]
    [InlineData(" \n\t [1, 2, 3]\t ")]
    public void OperationalStore_WriteGuards_RefuseJsonObjectOrArrayTextBeforeCallingSqlite(string jsonText)
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        store.ClearRecordedSql();

        AssertRefusesJson(() => store.SetLastCleanAtUtc(jsonText), store);
        AssertRefusesJson(() => store.SetContentPurgeMarkUtc(jsonText), store);
        AssertRefusesJson(() => store.SetProjectionDirtyState(new DirtyProjectionState(1, 2, [jsonText], false)), store);
        AssertRefusesJson(() => store.ReplaceSessionKeyAliases([new SessionKeyAlias(jsonText, "sess-1")]), store);
        AssertRefusesJson(() => store.ReplaceSessionKeyAliases([new SessionKeyAlias("alias-1", jsonText)]), store);
        AssertRefusesJson(() => store.UpsertRefreshRun(new RefreshRunRow("rr-1", "2026-11-19T08:00:00Z", null, "ok", 1, "manual", jsonText, null)), store);
        AssertRefusesJson(() => store.UpsertSourceCheckpoint(new SourceCheckpoint("claude-code", "src-1", "p", "parser", "1", "jsonl", jsonText, "rev", "2026-11-19T00:00:00Z", "2026-11-19T08:00:00Z", "2026-11-19T08:00:00Z", null, "ok", null, 1, 1)), store);
        AssertRefusesJson(() => store.PutQuarantinedSpan(new StoredQuarantinedSpan("q-1", jsonText, ["ns"])), store);
        AssertRefusesJson(() => store.PutQuarantinedSpan(new StoredQuarantinedSpan("q-1", "unclaimed", [jsonText])), store);
        AssertRefusesJson(() => store.PutCachedTokenCounts([new StoredTokenCacheEntry("h-1", 10, jsonText)]), store);
        AssertRefusesJson(() => store.ReplaceModelContextWindowCatalog(new StoredModelCatalogSnapshot(jsonText, [], [])), store);
        AssertRefusesJson(() => store.ReplaceModelContextWindowCatalog(new StoredModelCatalogSnapshot("fresh", [new StoredModelCatalogVendorStatus("anthropic", "ok", null, jsonText)], [])), store);
        AssertRefusesJson(() => store.PutMetricPoints([new StoredMetricPoint("mp-1", "tokens.total", jsonText)]), store);
        AssertRefusesJson(() => store.RecordIngest(jsonText, 1, "2026-11-19T08:00:00Z"), store);
        AssertRefusesJson(() => store.PutRecordProvenance([new RecordProvenanceRow("span-1", "claude-code", "src-1", null, null, "rev", "1.0", "2026-11-19T08:00:00Z", jsonText)]), store);

        Assert.Empty(store.ExecutedSql);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void OperationalStore_ConstraintGuards_RejectInvalidInputsBeforeCallingSqlite()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        store.ClearRecordedSql();

        AssertConstraintViolated(() => store.SaveSettings(new SettingsView(false, false, 0, false, 4747)), store);
        AssertConstraintViolated(() => store.SaveSettings(new SettingsView(false, false, 1441, false, 4747)), store);
        AssertConstraintViolated(() => store.SaveSettings(new SettingsView(false, false, 5, false, 0)), store);
        AssertConstraintViolated(() => store.SaveSettings(new SettingsView(false, false, 5, false, 65536)), store);

        AssertConstraintViolated(() => store.SetStoreState(new StoreStateView(0, 1)), store);
        AssertConstraintViolated(() => store.SetStoreState(new StoreStateView(1, 0)), store);
        AssertConstraintViolated(() => store.SetWriteMarker(-1), store);
        AssertConstraintViolated(() => store.SetLastCleanAtUtc(string.Empty), store);
        AssertConstraintViolated(() => store.SetContentRetentionDays(0), store);
        AssertConstraintViolated(() => store.SetContentPurgeMarkUtc(string.Empty), store);
        AssertConstraintViolated(() => store.SetProjectionState(new ProjectionStateView(1, -1)), store);
        AssertConstraintViolated(() => store.SetProjectionDirtyState(new DirtyProjectionState(-1, 5, null, true)), store);
        AssertConstraintViolated(() => store.SetProjectionDirtyState(new DirtyProjectionState(1, -1, null, true)), store);
        AssertConstraintViolated(() => store.SetProjectionDirtyState(new DirtyProjectionState(1, 5, ["s1", "s1"], false)), store);
        AssertConstraintViolated(() => store.SetProjectionDirtyState(new DirtyProjectionState(1, 5, [string.Empty], false)), store);

        AssertConstraintViolated(() => store.ReplaceSessionKeyAliases([new SessionKeyAlias("a1", "s1"), new SessionKeyAlias("a1", "s2")]), store);
        AssertConstraintViolated(() => store.ReplaceSessionKeyAliases([new SessionKeyAlias(string.Empty, "s1")]), store);

        AssertConstraintViolated(() => store.UpsertRefreshRun(new RefreshRunRow("rr-1", "2026-11-19T08:00:00Z", null, "ok", 1, "manual", null, 0)), store);
        AssertConstraintViolated(() => store.UpsertRefreshRun(new RefreshRunRow(string.Empty, "2026-11-19T08:00:00Z", null, "ok", 1, "manual", null, null)), store);

        AssertConstraintViolated(() => store.UpsertSourceCheckpoint(new SourceCheckpoint("claude-code", "src-1", "p", "parser", "1", "jsonl", string.Empty, string.Empty, "2026-11-19T00:00:00Z", "2026-11-19T08:00:00Z", "2026-11-19T08:00:00Z", null, "ok", null, -1, 0)), store);
        AssertConstraintViolated(() => store.UpsertSourceCheckpoint(new SourceCheckpoint("claude-code", "src-1", "p", "parser", "1", "jsonl", string.Empty, string.Empty, "2026-11-19T00:00:00Z", "2026-11-19T08:00:00Z", "2026-11-19T08:00:00Z", null, "ok", null, 0, -1)), store);

        AssertConstraintViolated(() => store.PutQuarantinedSpan(new StoredQuarantinedSpan("q-1", "reason", ["ns", "ns"])), store);
        AssertConstraintViolated(() => store.PutQuarantinedSpan(new StoredQuarantinedSpan(string.Empty, "reason", [])), store);

        AssertConstraintViolated(() => store.PutCachedTokenCounts([new StoredTokenCacheEntry("h-1", -1, null)]), store);
        AssertConstraintViolated(() => store.PutCachedTokenCounts([new StoredTokenCacheEntry(string.Empty, 10, null)]), store);

        AssertConstraintViolated(() => store.ReplaceModelContextWindowCatalog(new StoredModelCatalogSnapshot(
            "fresh",
            [
                new StoredModelCatalogVendorStatus("anthropic", "ok"),
                new StoredModelCatalogVendorStatus("anthropic", "error"),
            ],
            [])), store);
        AssertConstraintViolated(() => store.ReplaceModelContextWindowCatalog(new StoredModelCatalogSnapshot(
            "fresh",
            [],
            [
                new StoredModelContextWindowEntry("m-1", "m-1", 0, "anthropic", new Uri("https://example.com"), "2026-11-19T08:00:00Z"),
            ])), store);
        AssertConstraintViolated(() => store.ReplaceModelContextWindowCatalog(new StoredModelCatalogSnapshot(
            "fresh",
            [],
            [
                new StoredModelContextWindowEntry("m-1", "m-1", 1000, "anthropic", new Uri("https://example.com"), "2026-11-19T08:00:00Z"),
                new StoredModelContextWindowEntry("m-1", "m-2", 2000, "anthropic", new Uri("https://example.com"), "2026-11-19T08:00:00Z"),
            ])), store);

        AssertConstraintViolated(() => store.PutMetricPoints([new StoredMetricPoint("mp-1", "n", "sum", Value: double.NaN)]), store);
        AssertConstraintViolated(() => store.PutMetricPoints([new StoredMetricPoint("mp-1", "n", "sum", PointSum: double.PositiveInfinity)]), store);

        AssertConstraintViolated(() => store.RecordIngest("/tmp/a.jsonl", -1, "2026-11-19T08:00:00Z"), store);
        AssertConstraintViolated(() => store.RecordIngest(string.Empty, 1, "2026-11-19T08:00:00Z"), store);

        AssertConstraintViolated(() => store.PutRecordProvenance([new RecordProvenanceRow(string.Empty, "claude-code", "src-1", null, null, string.Empty, string.Empty, "2026-11-19T08:00:00Z", string.Empty)]), store);

        Assert.Empty(store.ExecutedSql);
        Assert.False(Directory.Exists(rootDir));
    }

    private static void ApplyBoth(
        SqliteRelationalStore sqlite,
        InMemoryRelationalStore memory,
        Action<IOperationalStore> action)
    {
        action(sqlite);
        action(memory);
    }

    private static void AssertQuarantinedSpanEqual(StoredQuarantinedSpan? expected, StoredQuarantinedSpan? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.SpanId, actual.SpanId);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.ObservedNamespaces, actual.ObservedNamespaces);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.TimestampUtc, actual.TimestampUtc);
        Assert.Equal(expected.HarnessId, actual.HarnessId);
    }

    private static void AssertQuarantinedSpanListEqual(
        IReadOnlyList<StoredQuarantinedSpan> expected,
        IReadOnlyList<StoredQuarantinedSpan> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            AssertQuarantinedSpanEqual(expected[index], actual[index]);
        }
    }

    private static void AssertCatalogEqual(StoredModelCatalogSnapshot expected, StoredModelCatalogSnapshot actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Vendors, actual.Vendors);
        Assert.Equal(expected.Entries, actual.Entries);
    }

    private static void AssertAllScriptsUseTabbedHexWithoutJson(IReadOnlyList<string> scripts)
    {
        Assert.NotEmpty(scripts);
        foreach (string sql in scripts)
        {
            Assert.DoesNotContain("json_extract(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("json_object(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("json_array(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("json_each(", sql, StringComparison.OrdinalIgnoreCase);
            string normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (normalized.StartsWith("PRAGMA foreign_keys = ON;\nBEGIN IMMEDIATE;\n", StringComparison.Ordinal))
            {
                Assert.EndsWith("\nCOMMIT;\n", normalized, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains(".headers off", normalized, StringComparison.Ordinal);
                Assert.Contains(".mode tabs", normalized, StringComparison.Ordinal);
            }
        }
    }

    private static void AssertRefusesJson(Action action, RecordingSqliteRelationalStore store)
    {
        DashValidationException ex = Assert.Throws<DashValidationException>(action);
        Assert.Equal(SqliteRelationalStore.JsonTextRefusedCode, ex.Code);
        Assert.Empty(store.ExecutedSql);
    }

    private static void AssertConstraintViolated(Action action, RecordingSqliteRelationalStore store)
    {
        DashValidationException ex = Assert.Throws<DashValidationException>(action);
        Assert.Equal(SqliteRelationalStore.ConstraintViolatedCode, ex.Code);
        Assert.Empty(store.ExecutedSql);
    }

    private static string NewNonExistentRoot() =>
        Path.Combine(Path.GetTempPath(), $"kyberdash-t9-nowrite-{Guid.NewGuid():N}");

    private static CollectedRecord CreateMinimalRecord(string recordId, string harnessId) =>
        new(
            RecordId: recordId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat",
            StartedAt: DateTimeOffset.Parse("2026-11-19T08:00:00Z", CultureInfo.InvariantCulture),
            Status: RecordStatus.Ok,
            SessionKey: null,
            ParentRecordId: null,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null));

    private class SeamSqliteRelationalStore : SqliteRelationalStore
    {
        private readonly List<string> executedSql = [];

        public SeamSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
            : base(databasePath, circuitBreaker)
        {
        }

        public IReadOnlyList<string> ExecutedSql => executedSql;

        public void ClearRecordedSql() => executedSql.Clear();

        protected override string ExecuteSqliteCore(string sql)
        {
            executedSql.Add(sql);
            return base.ExecuteSqliteCore(sql);
        }
    }

    private class ScriptedSqliteRelationalStore : SqliteRelationalStore
    {
        private readonly Queue<string> responses = new();

        public ScriptedSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
            : base(databasePath, circuitBreaker, initialize: false)
        {
        }

        public void EnqueueResponse(string response) => responses.Enqueue(response);

        protected override string ExecuteSqliteCore(string sql) =>
            responses.Count > 0 ? responses.Dequeue() : string.Empty;
    }

    private class RecordingSqliteRelationalStore : SqliteRelationalStore
    {
        private readonly List<string> executedSql = [];

        public RecordingSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
            : base(databasePath, circuitBreaker)
        {
        }

        public IReadOnlyList<string> ExecutedSql => executedSql;

        public void ClearRecordedSql() => executedSql.Clear();

        protected override string ExecuteSqliteCore(string sql)
        {
            executedSql.Add(sql);
            return string.Empty;
        }
    }
}
