using Xunit;
using System.Text.Json;
using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Tests;

/// <summary>
/// Store-parity tests for <see cref="CanonStoreRepository"/>: schema v24, metadata, records, quarantine,
/// problems, ingest log, token cache, sessions and turns, runs, executions, rollups, findings, predictions,
/// checkpoints, retention purge, and wipe behaviour, all against a real <c>sqlite3</c> database.
/// </summary>
public class CanonStoreRepositoryParityTests : IDisposable
{
    private static readonly string[] ExpectedCanonicalTables =
    [
        "enriched_logs",
        "execution",
        "finding",
        "harness_rollup",
        "ingest_log",
        "metadata",
        "metric_point",
        "model_context_window_catalog",
        "model_context_window_catalog_vendor",
        "pending_logs",
        "prediction",
        "problems",
        "quarantine",
        "quarantined_logs",
        "record_provenance",
        "records",
        "refresh_run",
        "run",
        "session",
        "source_checkpoint",
        "token_cache",
    ];

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Constructor_CreatesNestedDirectoryAndSchemaV24()
    {
        string databasePath = Path.Combine(directory.Path, "nested", "deeper", "canon.db");

        CanonStoreRepository store = new(databasePath, new SqliteCircuitBreaker());

        Assert.Equal(Path.GetFullPath(databasePath), store.DatabasePath);
        Assert.Equal("24", store.GetMetadata("schema_version"));
        Assert.Equal(CanonSchema.DetectorVersion, store.GetDetectorVersion());
        Assert.True(store.HasCurrentDetectorVersion());
        Assert.Equal(0, store.RecordsWriteMarker());
        Assert.Equal(ExpectedCanonicalTables, store.ListTables());
    }

    [Fact]
    public void Constructor_AcceptsDashOptions()
    {
        string databasePath = Path.Combine(directory.Path, "options.db");

        CanonStoreRepository store = new(new KyberDash.Contracts.Services.DashOptions { DatabasePath = databasePath }, new SqliteCircuitBreaker());

        Assert.Equal(Path.GetFullPath(databasePath), store.DatabasePath);
        Assert.Equal("24", store.GetMetadata("schema_version"));
    }

    [Fact]
    public void Constructor_RefusesDatabaseWrittenByNewerSchema()
    {
        string databasePath = Path.Combine(directory.Path, "canon.db");
        CanonStoreRepository store = new(databasePath, new SqliteCircuitBreaker());
        store.SetMetadata("schema_version", "25");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => _ = new CanonStoreRepository(databasePath, new SqliteCircuitBreaker()));

        Assert.Equal(
            CanonSchema.SchemaVersionRefusal(Path.GetFullPath(databasePath), 25, 24),
            exception.Message);
    }

    [Fact]
    public void Constructor_RefusesNonNumericSchemaVersion()
    {
        string databasePath = Path.Combine(directory.Path, "canon.db");
        CanonStoreRepository store = new(databasePath, new SqliteCircuitBreaker());
        store.SetMetadata("schema_version", "not-a-number");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => _ = new CanonStoreRepository(databasePath, new SqliteCircuitBreaker()));

        Assert.Contains("invalid schema_version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureInitialized_IsIdempotent()
    {
        CanonStoreRepository store = Create();

        store.EnsureInitialized();
        store.EnsureInitialized();

        Assert.Equal("24", store.GetMetadata("schema_version"));
    }

    [Fact]
    public void Metadata_CompareAndSwapFollowsExpectedContracts()
    {
        CanonStoreRepository store = Create();

        Assert.Null(store.GetMetadata("missing"));
        Assert.True(store.CompareAndSwapMetadata("lease", expected: null, newValue: "owner-a"));
        Assert.False(store.CompareAndSwapMetadata("lease", expected: null, newValue: "owner-b"));
        Assert.True(store.CompareAndSwapMetadata("lease", expected: "owner-a", newValue: "owner-b"));
        Assert.Equal("owner-b", store.GetMetadata("lease"));
        Assert.True(store.CompareAndSwapMetadata("lease", expected: "owner-b", newValue: null));
        Assert.Null(store.GetMetadata("lease"));
        Assert.True(store.CompareAndSwapMetadata("lease", expected: null, newValue: null));

        store.SetMetadata("multiline\tkey", "line1\nline2\t'quoted'");
        Assert.Equal("line1\nline2\t'quoted'", store.GetMetadata("multiline\tkey"));

        store.BumpRecordsWriteMarker();
        store.BumpRecordsWriteMarker();
        Assert.Equal(2, store.RecordsWriteMarker());
    }

    [Fact]
    public void Records_RoundTripBytesThroughCompressedColumns()
    {
        CanonStoreRepository store = Create();
        CanonicalRecord record = TestData.Record("span-1");

        store.PutRecord(record);

        Assert.True(store.HasRecord("span-1"));
        Assert.False(store.HasRecord("span-missing"));
        CanonicalRecord stored = Assert.IsType<CanonicalRecord>(store.GetRecord("span-1"));
        Assert.Equal(record.SpanId, stored.SpanId);
        Assert.Equal(record.TraceId, stored.TraceId);
        Assert.Equal(record.Harness, stored.Harness);
        Assert.Equal(record.SessionId, stored.SessionId);
        Assert.Equal(record.Tokens, stored.Tokens);
        Assert.Equal(record.Cost, stored.Cost);
        Assert.Equal("hello", stored.Content["conversation_history"]);
        Assert.NotNull(stored.Parts);
        Assert.Single(stored.Parts!);
        Assert.NotNull(stored.Raw);
        Assert.Equal("\"anthropic\"", stored.Raw!["gen_ai.system"].GetRawText());
    }

    [Fact]
    public void Records_AreIdempotentAndCountable()
    {
        CanonStoreRepository store = Create();

        store.PutRecords([TestData.Record("span-1"), TestData.Record("span-2")]);
        store.PutRecord(TestData.Record("span-1"));

        Assert.Equal(2, store.CountRecords());
        Assert.Equal(["claude-code"], store.ListObservedHarnesses());
    }

    [Fact]
    public void Records_UpsertAdvancesWriteMarkerAndMarksSessionDirty()
    {
        CanonStoreRepository store = Create();

        store.PutRecord(TestData.Record("span-1", sessionId: "session-42"));

        Assert.Equal(1, store.RecordsWriteMarker());
        DirtyProjectionState dirty = store.GetDirtyProjectionState();
        Assert.False(dirty.IsWildcard);
        Assert.Equal(["session-42"], dirty.Keys);
        Assert.Equal(0, dirty.FromSeq);
        Assert.Equal(1, dirty.ToSeq);
    }

    [Fact]
    public void Records_WithoutSessionOrTraceMarkEverySessionDirty()
    {
        CanonStoreRepository store = Create();
        CanonicalRecord orphan = TestData.Record("span-orphan") with { SessionId = null, TraceId = null };

        store.PutRecord(orphan);

        Assert.True(store.GetDirtyProjectionState().IsWildcard);
    }

    [Fact]
    public void Records_ListBySessionKeyUsesSessionThenTrace()
    {
        CanonStoreRepository store = Create();
        CanonicalRecord traceOnly = TestData.Record("span-trace") with { SessionId = null };
        store.PutRecords([TestData.Record("span-1", sessionId: "session-1"), traceOnly]);

        IReadOnlyList<CanonicalRecord> bySession = store.ListRecordsBySessionKey("session-1");
        IReadOnlyList<CanonicalRecord> byTrace = store.ListRecordsBySessionKey("trace-span-trace");

        Assert.Equal(["span-1"], bySession.Select(static record => record.SpanId));
        Assert.Equal(["span-trace"], byTrace.Select(static record => record.SpanId));
        Assert.Equal(2, store.QueryAllRecords().Count);
    }

    [Fact]
    public void SetCosts_UpdatesCostBlockInPlace()
    {
        CanonStoreRepository store = Create();
        store.PutRecord(TestData.Record("span-1"));
        CostBlock updated = new(CostBasis.Harness, CostStatus.Priced, 9.5, "USD");

        store.SetCosts(new Dictionary<string, CostBlock>(StringComparer.Ordinal) { ["span-1"] = updated });

        CanonicalRecord stored = Assert.IsType<CanonicalRecord>(store.GetRecord("span-1"));
        Assert.Equal(updated, stored.Cost);
        Assert.Equal(2, store.RecordsWriteMarker());
    }

    [Fact]
    public void Quarantine_MovesSpanAwayFromRecordsAndProblems()
    {
        CanonStoreRepository store = Create();
        store.PutRecord(TestData.Record("span-1"));
        store.RecordProblem(new SpanProblemRow("span-1", "error", "KW-1", "bad span"));
        QuarantineRow row = new("span-1", ["gen_ai", "http"], "unclaimed", Source: "otlp", Name: "sp", Timestamp: "2026-01-01T00:00:00.000Z", RawPayload: "{}", Harness: "claude-code");

        store.QuarantineAndDelete(row);

        Assert.False(store.HasRecord("span-1"));
        Assert.Empty(store.GetProblems("span-1"));
        QuarantineRow? stored = store.GetQuarantineItem("span-1");
        Assert.NotNull(stored);
        Assert.Equal(row with { ObservedNamespaces = stored!.ObservedNamespaces }, stored);
        Assert.Equal(["span-1"], store.ListQuarantine().Select(static item => item.SpanId));
        Assert.Equal(["span-1"], store.GetQuarantined("claude-code").Select(static item => item.SpanId));
        Assert.Empty(store.GetQuarantined("codex-cli"));
    }

    [Fact]
    public void Problems_UpsertOnIdentityKeyInsteadOfDuplicating()
    {
        CanonStoreRepository store = Create();
        SpanProblemRow first = new("span-1", "error", "KW-1", "first", Location: "here", Harness: "claude-code");
        SpanProblemRow second = first with { Message = "second", Severity = "warning" };

        store.RecordProblem(first);
        store.RecordProblem(second);

        IReadOnlyList<SpanProblemRow> problems = store.GetProblems();
        SpanProblemRow stored = Assert.Single(problems);
        Assert.Equal("second", stored.Message);
        Assert.Equal("warning", stored.Severity);
        Assert.Equal("here", stored.Location);

        store.RecordProblems([new SpanProblemRow("span-2", "error", "KW-2", "other", Harness: "claude-code")]);
        Assert.Equal(2, store.GetProblems().Count);
        Assert.Single(store.GetProblems("span-2"));
        Assert.Equal(2, store.ListStoredProblems().Count);
        StoredProblemRow listed = Assert.Single(store.ListStoredProblems("claude-code", "span-1"));
        Assert.Equal("second", listed.Message);
        Assert.NotNull(store.GetStoredProblem(listed.Id));
        Assert.Null(store.GetStoredProblem(int.MaxValue));
    }

    [Fact]
    public void IngestLog_NormalizesSourceAndFilters()
    {
        CanonStoreRepository store = Create();

        store.RecordIngest("  claude-code  ", 3, "2026-01-01T00:00:00.000Z");
        store.RecordIngest("   ", 1, "2026-01-01T00:01:00.000Z");

        IReadOnlyList<IngestLogEntry> entries = store.GetIngestLog();
        Assert.Equal(["claude-code", "otlp"], entries.Select(static entry => entry.Source));
        Assert.Equal(3, entries[0].Count);
        Assert.Single(store.GetIngestLog("claude-code"));
    }

    [Fact]
    public void TokenCache_InsertIfAbsentKeepsFirstCount()
    {
        CanonStoreRepository store = Create();

        store.PutCachedTokenCounts(new Dictionary<string, long>(StringComparer.Ordinal) { ["h1"] = 10, ["h2"] = 20 }, "claude-opus-5");
        store.PutCachedTokenCounts(new Dictionary<string, long>(StringComparer.Ordinal) { ["h1"] = 99 }, "claude-opus-5");

        Assert.Equal(10, store.GetCachedTokenCount("h1"));
        Assert.Equal(20, store.GetCachedTokenCount("h2"));
        Assert.Null(store.GetCachedTokenCount("missing"));
    }

    [Fact]
    public void Sessions_PersistTurnsInTheChildTable()
    {
        CanonStoreRepository store = Create();

        store.PutSession(TestData.Session("session-1", "claude-code", 0, 1, 2));

        SessionSummaryRow stored = Assert.IsType<SessionSummaryRow>(store.GetSession("session-1"));
        Assert.Equal("claude-code", stored.Harness);
        Assert.Equal("label-session-1", stored.Label);
        Assert.NotNull(stored.Turns);
        Assert.Equal(3, stored.Turns!.Count);
        Assert.True(stored.Payload.TryGetProperty("turns", out JsonElement turnsOnPayload));
        Assert.Equal(3, turnsOnPayload.GetArrayLength());

        IReadOnlyList<(int TurnIndex, string TurnJson)> turns = store.GetSessionTurns("session-1");
        Assert.Equal([0, 1, 2], turns.Select(static turn => turn.TurnIndex));
        Assert.Contains("turn 2", turns[2].TurnJson, StringComparison.Ordinal);

        Assert.Equal(3, store.GetSession("session-1")!.Turns!.Count);
        Assert.Single(store.ListSessions());
        Assert.Single(store.ListSessions("claude-code"));
        Assert.Empty(store.ListSessions("codex-cli"));
        Assert.Equal(["claude-code"], store.ListObservedHarnesses());
    }

    [Fact]
    public void Sessions_UpsertReplacesPreviousTurns()
    {
        CanonStoreRepository store = Create();
        store.PutSession(TestData.Session("session-1", "claude-code", 0, 1, 2));

        store.PutSession(TestData.Session("session-1"));

        Assert.Empty(store.GetSessionTurns("session-1"));
    }

    [Fact]
    public void Sessions_DeleteRemovesTurns()
    {
        CanonStoreRepository store = Create();
        store.PutSessions([TestData.Session("session-1"), TestData.Session("session-2", "claude-code", 1)]);

        store.DeleteSession("session-1");
        Assert.Null(store.GetSession("session-1"));
        Assert.Empty(store.GetSessionTurns("session-1"));

        store.DeleteSessions(["session-2"]);
        Assert.Null(store.GetSession("session-2"));
    }

    [Fact]
    public void Projection_StateAndKeyMapRoundTrip()
    {
        CanonStoreRepository store = Create();

        store.MarkProjectionClean(7, "24");
        DirtyProjectionState clean = store.GetDirtyProjectionState();
        Assert.False(clean.IsWildcard);
        Assert.Equal(7, clean.ToSeq);
        Assert.Empty(clean.Keys!);

        store.SaveProjectionSessionKeyMap(new Dictionary<string, string>(StringComparer.Ordinal) { ["key-a"] = "session-a" });
        IReadOnlyDictionary<string, string> map = store.GetProjectionSessionKeyMap();
        Assert.Equal("session-a", map["key-a"]);
    }

    [Fact]
    public void Findings_MoveThePromotedPayloadFieldsOutOfThePayload()
    {
        CanonStoreRepository store = Create();
        Finding finding = TestData.Finding("finding-1");

        store.PutFinding(finding);

        Finding stored = Assert.IsType<Finding>(store.GetFinding("finding-1"));
        Assert.Equal(finding.Id, stored.Id);
        Assert.Equal(finding.Title, stored.Title);
        Assert.Equal(finding.EstimatedWasteTokens, stored.EstimatedWasteTokens);
        Assert.Equal(finding.ErrorBar, stored.ErrorBar);
        Assert.Equal(finding.EvidenceLinks, stored.EvidenceLinks);
        Assert.Equal(finding.RankScore, stored.RankScore);
        Assert.Equal(finding.MeasurementClass, stored.MeasurementClass);
        Assert.Equal(finding.ConfidenceBasis, stored.ConfidenceBasis);
        Assert.Equal(finding.WhatWouldRaiseIt, stored.WhatWouldRaiseIt);
        Assert.NotNull(stored.Payload);
        Assert.True(stored.Payload!.ContainsKey("measurementClass"));
        Assert.Single(store.ListFindings());
    }

    [Fact]
    public void Findings_ListByRunAndSessionAndDeleteBySession()
    {
        CanonStoreRepository store = Create();
        store.PutFindings(
        [
            TestData.Finding("finding-1", sessionId: "session-1", runId: "run-1"),
            TestData.Finding("finding-2", sessionId: "session-2", runId: "run-2"),
            TestData.Finding("finding-3", sessionId: "session-1", runId: "run-1"),
        ]);

        Assert.Equal(3, store.ListFindings().Count);
        Assert.Equal(["finding-1", "finding-3"], store.ListFindings(runId: "run-1").Select(static finding => finding.Id).Order(StringComparer.Ordinal));
        Assert.Equal(2, store.ListFindings(sessionId: "session-1").Count);
        Assert.Null(store.GetFinding("missing"));

        store.DeleteFindingsForSession("session-1");
        Assert.Single(store.ListFindings());
    }

    [Fact]
    public void Runs_RoundTripWithPayloadOrderedByStart()
    {
        CanonStoreRepository store = Create();
        RunRow later = new("run-2", "claude-code", "Later", "explicit", null, "/repo", "2026-01-02T00:00:00.000Z", null, 2, TestData.Json("{\"k\":1}"));
        RunRow earlier = new("run-1", "codex-cli", null, "derived", "repo+hour", "/repo", "2026-01-01T00:00:00.000Z", "2026-01-01T01:00:00.000Z", 1);

        store.UpsertRuns([later, earlier]);

        Assert.Equal(["run-2", "run-1"], store.ListRuns().Select(static run => run.RunId));
        RunRow stored = Assert.IsType<RunRow>(store.GetRun("run-2"));
        Assert.Equal(later with { Payload = stored.Payload }, stored);
        Assert.NotNull(stored.Payload);
        Assert.Equal(1, stored.Payload!.Value.GetProperty("k").GetInt32());
        Assert.Equal(["run-1"], store.ListRuns("codex-cli").Select(static run => run.RunId));
        Assert.Null(store.GetRun("missing"));
    }

    [Fact]
    public void Executions_RoundTripAndLinkToRuns()
    {
        CanonStoreRepository store = Create();
        ExecutionRow root = new("exec-1", "run-1", "session-1", null, "claude-code", "orchestrator", true, "2026-01-01T00:00:00.000Z", null, TestData.Json("{\"status\":\"linked\"}"));
        ExecutionRow child = new("exec-2", "run-1", "session-1", "exec-1", "claude-code", "subagent", false, "2026-01-01T00:05:00.000Z", null, TestData.Json("{\"status\":\"linked\"}"));

        store.UpsertExecutions([child, root]);

        IReadOnlyList<ExecutionRow> stored = store.ListExecutionsForRun("run-1");
        Assert.Equal(["exec-1", "exec-2"], stored.Select(static exec => exec.ExecutionId));
        Assert.True(stored[0].IsRoot);
        Assert.False(stored[1].IsRoot);
        Assert.Equal("exec-1", stored[1].ParentExecutionId);
        Assert.Equal(2, store.ListExecutionsByHarness("claude-code").Count);
        Assert.Empty(store.ListExecutionsByHarness("codex-cli"));
    }

    [Fact]
    public void HarnessRollups_RoundTripNullableMetrics()
    {
        CanonStoreRepository store = Create();
        HarnessRollupRow row = new(
            Harness: "claude-code",
            SampleCount: 12,
            ContextPressureMedian: 0.42,
            ContextPressureP95: null,
            CacheHitRate: 0.8,
            ToolYield: 1.5,
            DelegationOverhead: null,
            FieldCoverage: 0.9,
            Measurability: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["tokens"] = TestData.Json("{\"availability\":\"measured\"}") },
            Payload: TestData.Json("{\"note\":\"ok\"}"));

        store.UpsertHarnessRollups([row]);

        HarnessRollupRow stored = Assert.IsType<HarnessRollupRow>(store.GetHarnessRollup("claude-code"));
        Assert.Equal(12, stored.SampleCount);
        Assert.Equal(0.42, stored.ContextPressureMedian);
        Assert.Null(stored.ContextPressureP95);
        Assert.Equal(0.9, stored.FieldCoverage);
        Assert.True(stored.Measurability.ContainsKey("tokens"));
        Assert.NotNull(stored.Payload);
        Assert.Single(store.ListHarnessRollups());
    }

    [Fact]
    public void Predictions_RoundTripOptionalFields()
    {
        CanonStoreRepository store = Create();
        PredictionRow row = new(
            Id: "prediction-1",
            FindingId: "finding-1",
            RunId: "run-1",
            PredictedWasteTokens: 500,
            Confidence: 0.7,
            ConfidenceTier: "medium",
            ErrorBar: TestData.Json("{\"lower\":100,\"upper\":900}"),
            CreatedAt: "2026-01-01T00:00:00.000Z",
            ComparisonRunId: "run-0",
            ObservedDeltaTokens: 420,
            CalibrationScore: 0.75,
            Payload: TestData.Json("{\"note\":\"ok\"}"));

        store.PutPrediction(row);

        PredictionRow stored = Assert.Single(store.ListPredictions());
        Assert.Equal(row with { ErrorBar = stored.ErrorBar, Payload = stored.Payload }, stored);
        Assert.Equal(row.ErrorBar!.Value.GetRawText(), stored.ErrorBar!.Value.GetRawText());
        Assert.Equal(row.Payload!.Value.GetRawText(), stored.Payload!.Value.GetRawText());
        Assert.Single(store.ListPredictions("run-1"));
        Assert.Empty(store.ListPredictions("run-2"));
    }

    [Fact]
    public void SourceCheckpoints_RoundTripAndNormalizeHistoryWeeks()
    {
        CanonStoreRepository store = Create();
        SourceCheckpoint checkpoint = Checkpoint("claude-code", "source-a");
        store.UpsertSourceCheckpoint(checkpoint);
        store.UpsertSourceCheckpoint(Checkpoint("codex-cli", "source-b"));

        Assert.Equal(checkpoint, store.GetSourceCheckpoint("claude-code", "source-a"));
        Assert.Equal(2, store.ListSourceCheckpoints().Count);
        Assert.Single(store.ListSourceCheckpoints("codex-cli"));
        Assert.Null(store.GetSourceCheckpoint("claude-code", "missing"));

        store.PutRecordProvenance([new RecordProvenanceRow("span-1", "claude-code", "source-a", "session-1", "record-1", "rev", "1", "2026-01-01T00:00:00.000Z", "token")]);
    }

    [Fact]
    public void RefreshRuns_RoundTripAndNormalizeNegativeHistoryWeeks()
    {
        CanonStoreRepository store = Create();
        store.RecordRefreshRun(new RefreshRunRow("refresh-1", "2026-01-01T00:00:00.000Z", null, "running", 4242, "manual", null, null));
        store.RecordRefreshRun(new RefreshRunRow("refresh-2", "2026-01-02T00:00:00.000Z", "2026-01-02T00:05:00.000Z", "success", 4242, "schedule", "done", -4));

        IReadOnlyList<RefreshRunRow> rows = store.ListRefreshRuns();
        Assert.Equal(["refresh-2", "refresh-1"], rows.Select(static row => row.Id));
        Assert.Equal("done", rows[0].Summary);
        Assert.Null(rows[0].HistoryWeeks);
        Assert.Null(rows[1].CompletedAt);
        Assert.Single(store.ListRefreshRuns(1));
    }

    [Fact]
    public void PendingLogs_AreDrainedOnce()
    {
        CanonStoreRepository store = Create();
        OtlpLog log = new(
            "log-1",
            "trace-1",
            "span-1",
            "session-1",
            "2026-01-01T00:00:00.000Z",
            "body",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["k"] = TestData.Json("\"v\"") },
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            new OtlpScope("scope", "1.0.0"));

        store.StorePendingLogs([log], "claude-code");
        store.StoreQuarantinedLogs([(log, "unmatched")], "claude-code");
        store.MarkLogsEnriched(["log-2"]);

        IReadOnlyList<OtlpLog> taken = store.TakePendingLogs();
        Assert.Equal("log-1", Assert.Single(taken).LogId);
        Assert.Empty(store.TakePendingLogs());
        Assert.True(store.IsLogEnriched("log-2"));
        Assert.False(store.IsLogEnriched("log-1"));
    }

    [Fact]
    public void PurgeContentBefore_ClearsContentButKeepsRow()
    {
        CanonStoreRepository store = Create();
        store.PutRecord(TestData.Record("old", timestamp: "2026-01-01T00:00:00.000Z"));
        store.PutRecord(TestData.Record("new", timestamp: "2026-06-01T00:00:00.000Z"));

        int purged = store.PurgeContentBefore("2026-03-01T00:00:00.000Z");

        Assert.Equal(1, purged);
        CanonicalRecord old = Assert.IsType<CanonicalRecord>(store.GetRecord("old"));
        Assert.Empty(old.Content);
        Assert.Null(old.Parts);
        Assert.Equal(10, old.Tokens.FreshInput);
        Assert.Equal(1, store.PurgeContentBefore("2026-07-01T00:00:00.000Z"));
        Assert.Equal(0, store.PurgeContentBefore("2025-01-01T00:00:00.000Z"));
    }

    [Fact]
    public void WipeAll_EmptiesTelemetryButKeepsOperatorMetadata()
    {
        CanonStoreRepository store = Create();
        store.SetMetadata("settings.jobs.paused", "on");
        store.PutRecord(TestData.Record("span-1"));
        store.PutSession(TestData.Session("session-1", "claude-code", 0));
        store.PutFinding(TestData.Finding("finding-1"));
        store.RecordProblem(new SpanProblemRow("span-1", "error", "KW-1", "bad"));
        store.UpsertRuns([new RunRow("run-1", "claude-code", null, "explicit", null, null, null, null, 1)]);
        store.UpsertExecutions([new ExecutionRow("exec-1", "run-1", "session-1", null, "claude-code", null, true, null, null, TestData.Json("{}"))]);
        store.UpsertHarnessRollups([Rollup("claude-code")]);
        store.QuarantineAndDelete(new QuarantineRow("span-q", [], "unclaimed"));

        StoreWipeReport report = store.WipeAll();

        Assert.Equal(1, report.RecordsDeleted);
        Assert.Equal(1, report.SessionsDeleted);
        Assert.Equal(1, report.TurnsDeleted);
        Assert.Equal(1, report.RunsDeleted);
        Assert.Equal(1, report.ExecutionsDeleted);
        Assert.Equal(1, report.FindingsDeleted);
        Assert.Equal(1, report.RollupsDeleted);
        Assert.Equal(1, report.ProblemsDeleted);
        Assert.Equal(1, report.QuarantineDeleted);
        Assert.NotEmpty(report.LastCleanAt);
        Assert.Equal(0, store.CountRecords());
        Assert.Empty(store.ListSessions());
        Assert.Equal("on", store.GetMetadata("settings.jobs.paused"));
        Assert.Equal(report.LastCleanAt, store.GetMetadata("last_clean_at"));
    }

    [Fact]
    public void WipeHarnesses_LeavesOtherHarnessesIntact()
    {
        CanonStoreRepository store = Create();
        store.PutRecord(TestData.Record("claude-span", harness: "claude-code"));
        store.PutRecord(TestData.Record("codex-span", harness: "codex-cli", sessionId: "session-2"));
        store.PutSession(TestData.Session("session-1"));
        store.PutSession(TestData.Session("session-2", harness: "codex-cli"));
        store.UpsertRuns(
        [
            new RunRow("run-1", "claude-code", null, "explicit", null, null, null, null, 1),
            new RunRow("run-2", "codex-cli", null, "explicit", null, null, null, null, 1),
        ]);

        StoreWipeReport report = store.WipeHarnesses(["claude-code"]);

        Assert.Equal(1, report.RecordsDeleted);
        Assert.Equal(["codex-span"], store.QueryAllRecords().Select(static record => record.SpanId));
        Assert.Equal(["codex-cli"], store.ListObservedHarnesses());
        Assert.Null(store.GetRun("run-1"));
        Assert.NotNull(store.GetRun("run-2"));
        Assert.Equal(0, store.WipeHarnesses(["   "]).RecordsDeleted);
    }

    [Fact]
    public void Vacuum_CompletesOnAPopulatedStore()
    {
        CanonStoreRepository store = Create();
        store.PutRecord(TestData.Record("span-1"));

        store.Vacuum();

        Assert.Equal(1, store.CountRecords());
    }

    [Fact]
    public void CircuitBreaker_IsUsedForEveryOperation()
    {
        CountingCircuitBreaker breaker = new();
        string databasePath = Path.Combine(directory.Path, "counted.db");
        CanonStoreRepository store = new(databasePath, breaker);
        int before = breaker.Calls;

        _ = store.GetMetadata("schema_version");

        Assert.True(breaker.Calls > before);
    }

    private CanonStoreRepository Create() =>
        new(Path.Combine(directory.Path, "canon.db"), new SqliteCircuitBreaker());

    private static SourceCheckpoint Checkpoint(string harnessId, string sourceKey) =>
        new(
            HarnessId: harnessId,
            SourceKey: sourceKey,
            ProviderId: "claude",
            ParserId: "claude-transcript-reader",
            ParserContractVersion: "1",
            Format: "claude-jsonl",
            SourceRootLabel: "projects",
            RevisionToken: "100-200",
            CoveredFromUtc: "2026-01-01T00:00:00.000Z",
            CoveredThroughUtc: "2026-01-02T00:00:00.000Z",
            LastAttemptUtc: "2026-01-02T00:00:00.000Z",
            LastSuccessUtc: "2026-01-02T00:00:00.000Z",
            LastStatus: "success",
            LastErrorCode: null,
            UnitCount: 3,
            RecordCount: 42);

    private static HarnessRollupRow Rollup(string harness) =>
        new(harness, 1, null, null, null, null, null, null, new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    /// <summary>Circuit breaker that counts executions and never opens.</summary>
    private class CountingCircuitBreaker : ICircuitBreaker
    {
        public int Calls { get; private set; }

        public DashCircuitState State => DashCircuitState.Closed;

        public T Execute<T>(Func<T> operation)
        {
            Calls++;
            return operation();
        }

        public void Execute(Action operation)
        {
            Calls++;
            operation();
        }

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
            => operation(cancellationToken).AsTask();

        public void Reset()
        {
        }
    }
}
