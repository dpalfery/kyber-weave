using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Verifies <see cref="SqliteRelationalStore"/> schema initialization, hex-encoded transactional
/// write SQL generation, and JSON/constraint write guards using a test subclass that intercepts
/// <see cref="KyberDash.Core.Repositories.BaseSqliteRepository.ExecuteSqliteCore"/> without
/// opening or creating any SQLite database file on disk.
/// </summary>
public class SqliteRelationalStoreWriteTests
{
    private const string DummySha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void SqliteRelationalStore_IsPublicUnsealedAndImplementsContracts()
    {
        Type storeType = typeof(SqliteRelationalStore);

        Assert.True(storeType.IsPublic);
        Assert.False(storeType.IsSealed);
        Assert.True(typeof(IRelationalStoreWriter).IsAssignableFrom(storeType));
        Assert.True(typeof(IRelationalStoreReader).IsAssignableFrom(storeType));
        Assert.True(typeof(IProjectionStore).IsAssignableFrom(storeType));
    }

    [Fact]
    public void SqliteRelationalStore_ContainsNoSystemTextJsonSerializationOrDeserialization()
    {
        Type storeType = typeof(SqliteRelationalStore);
        List<Type> typesToInspect = [storeType, .. storeType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)];

        foreach (Type type in typesToInspect)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNotSystemTextJson(field.FieldType, $"{type.Name}.{field.Name}");
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNotSystemTextJson(property.PropertyType, $"{type.Name}.{property.Name}");
            }

            foreach (MethodBase method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                InspectMethodSignatureAndLocals(method);
            }

            foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                InspectMethodSignatureAndLocals(ctor);
            }
        }
    }

    [Fact]
    public void Constructor_InitializesPragmasAppliesGeneratedDdlAndStampsStoreStateWithoutTouchingDisk()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "nested", "canon.db");

        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());

        Assert.False(Directory.Exists(rootDir));
        Assert.False(File.Exists(dbPath));
        Assert.Equal(3, store.ExecutedSql.Count);

        string pragmaSql = store.ExecutedSql[0];
        Assert.Contains("PRAGMA journal_mode = WAL;", pragmaSql, StringComparison.Ordinal);
        Assert.Contains("PRAGMA synchronous = NORMAL;", pragmaSql, StringComparison.Ordinal);
        Assert.Contains($"PRAGMA journal_size_limit = {SqliteRelationalStore.WalSizeLimitBytes.ToString(CultureInfo.InvariantCulture)};", pragmaSql, StringComparison.Ordinal);
        string expectedStoreStateDdl = NormalizeLineEndings(SchemaDdlGenerator.GenerateTableDdl(RelationalSchemaCatalog.GetTable("store_state"))).Trim();
        Assert.Contains(expectedStoreStateDdl, NormalizeLineEndings(pragmaSql), StringComparison.Ordinal);

        string versionCheckSql = store.ExecutedSql[1];
        Assert.Contains("SELECT schema_version, detector_version FROM store_state WHERE singleton_id = 1 LIMIT 1;", versionCheckSql, StringComparison.Ordinal);

        string ddlSql = NormalizeLineEndings(store.ExecutedSql[2]);
        Assert.StartsWith("PRAGMA foreign_keys = ON;\nBEGIN IMMEDIATE;\n", ddlSql, StringComparison.Ordinal);
        Assert.Contains(NormalizeLineEndings(SchemaDdlGenerator.GenerateSchemaDdl()).Trim(), ddlSql, StringComparison.Ordinal);
        Assert.Contains(
            $"INSERT OR IGNORE INTO store_state (singleton_id, schema_version, detector_version) VALUES (1, {SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}, {SqliteRelationalStore.DetectorVersion.ToString(CultureInfo.InvariantCulture)});",
            ddlSql,
            StringComparison.Ordinal);
        Assert.Contains("\nCOMMIT;", ddlSql, StringComparison.Ordinal);

        store.ClearRecordedSql();
        store.EnsureInitialized();
        Assert.Empty(store.ExecutedSql);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void Constructor_WithDashOptions_ResolvesDatabasePathAndValidatesNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new SqliteRelationalStore((DashOptions)null!, new SqliteCircuitBreaker()));

        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        DashOptions options = new() { DatabasePath = dbPath };

        RecordingSqliteRelationalStore store = new(options, new SqliteCircuitBreaker());

        Assert.Equal(dbPath, store.DatabasePath);
        Assert.Equal(3, store.ExecutedSql.Count);
        Assert.False(Directory.Exists(rootDir));
    }

    [Theory]
    [InlineData("not-an-integer\t5")]
    [InlineData("0\t5")]
    [InlineData("99\t5")]
    public void EnsureInitialized_RefusesInvalidOlderOrNewerSchemaVersion(string storeStateResponse)
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");

        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker(), initialize: false);
        store.EnqueueResponse(string.Empty);
        store.EnqueueResponse(storeStateResponse);

        Assert.Throws<InvalidOperationException>(() => store.EnsureInitialized());
        Assert.Equal(2, store.ExecutedSql.Count);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void EnsureInitialized_KeepsStoredDetectorVersionForTheProjectorToRestamp()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");

        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker(), initialize: false);
        store.EnqueueResponse(string.Empty);
        store.EnqueueResponse($"{SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}\t1");

        store.EnsureInitialized();

        Assert.Equal(3, store.ExecutedSql.Count);
        string ddlSql = NormalizeLineEndings(store.ExecutedSql[2]);
        Assert.DoesNotContain("UPDATE store_state", ddlSql, StringComparison.Ordinal);
        Assert.Contains(
            $"INSERT OR IGNORE INTO store_state (singleton_id, schema_version, detector_version) VALUES (1, {SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}, {SqliteRelationalStore.DetectorVersion.ToString(CultureInfo.InvariantCulture)});",
            ddlSql,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void WriteBatch_EmitsTransactionalHexEncodedSqlForAllCollectedTablesWithoutTouchingDisk()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        store.ClearRecordedSql();

        string trickyTitle = "O'Reilly session \"alpha\"\nNext line {not json}";
        string trickyPartText = "Assistant wrote {\"ok\":true} in prose.";
        CollectedBatch batch = BuildFullCollectedBatch(trickyTitle, trickyPartText);

        store.WriteBatch(batch);

        Assert.Single(store.ExecutedSql);
        string sql = NormalizeLineEndings(store.ExecutedSql[0]);

        Assert.StartsWith("PRAGMA foreign_keys = ON;\nBEGIN IMMEDIATE;\n", sql, StringComparison.Ordinal);
        Assert.EndsWith("\nCOMMIT;\n", sql, StringComparison.Ordinal);

        Assert.Contains("INSERT OR IGNORE INTO harness (harness_id)", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT OR IGNORE INTO source (source_id, harness_id, source_kind, locator)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT OR REPLACE INTO source", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT OR REPLACE INTO session", sql, StringComparison.Ordinal);

        Assert.Contains("INSERT INTO session (session_id, harness_id, native_session_id)", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT(session_id) DO UPDATE SET harness_id = excluded.harness_id, native_session_id = excluded.native_session_id;", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO session_workspace", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO session_repository", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO session_branch", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO session_title", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO session_model_usage", sql, StringComparison.Ordinal);

        Assert.Contains("INSERT INTO record (span_id, source_id, harness_id, op, kind, name, started_at_utc, status)", sql, StringComparison.Ordinal);
        Assert.Contains(HexExpr("llm.invoke"), sql, StringComparison.Ordinal);
        Assert.Contains(HexExpr("client"), sql, StringComparison.Ordinal);
        Assert.Contains(HexExpr("ok"), sql, StringComparison.Ordinal);
        Assert.Contains(HexExpr("otlp"), sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_session_link", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_parent", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_trace", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_model", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_end", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_reported_duration", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_token_count", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_reported_total", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_reported_cost", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_skill_invocation", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_agent_instance", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_error", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_edit_failure", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_correction", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_correction_rule", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO record_declared_context_window", sql, StringComparison.Ordinal);

        Assert.Contains("INSERT INTO content_part", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO content_part_text", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO content_part_withheld", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO content_part_reported_tokens", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO content_part_server", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO content_part_role", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO content_part_residency", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO content_part_truncation", sql, StringComparison.Ordinal);

        Assert.Contains("INSERT INTO tool_definition", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_definition_server", sql, StringComparison.Ordinal);

        Assert.Contains("INSERT INTO tool_call", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_mcp", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_input_size", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_input_fingerprint", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_input_text", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_input_truncation", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_result_size", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_result_text", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_result_truncation", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_outcome", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_error", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_error_code", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO tool_call_exit_code", sql, StringComparison.Ordinal);

        Assert.Contains("INSERT INTO agent_invocation", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO agent_invocation_spawn", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO agent_invocation_outcome", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO agent_invocation_error", sql, StringComparison.Ordinal);

        Assert.Contains("INSERT INTO ingest_problem (problem_key, severity, code, message)", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT(problem_key) DO UPDATE SET severity = excluded.severity, code = excluded.code, message = excluded.message;", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO ingest_problem_span", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO ingest_problem_location", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO ingest_problem_session", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO ingest_problem_harness", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO ingest_problem_timestamp", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO ingest_problem_stack", sql, StringComparison.Ordinal);

        Assert.Contains(HexExpr(trickyTitle), sql, StringComparison.Ordinal);
        Assert.Contains(HexExpr(trickyPartText), sql, StringComparison.Ordinal);
        Assert.DoesNotContain("O'Reilly", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"ok\":true}", sql, StringComparison.Ordinal);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void WriteProblem_And_DeleteSource_EmitTransactionalHexEncodedSqlWithoutTouchingDisk()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        store.ClearRecordedSql();

        CollectedProblem problem = new(
            ProblemKey: "prob-key-1",
            Severity: "warning",
            Code: "WARN_CODE",
            Message: "Unusual prompt structure",
            SpanId: "span-9",
            Location: "line:42",
            SessionKey: "sess-9",
            HarnessId: "codex",
            TimestampUtc: "2026-11-19T10:00:00.000Z",
            Stack: "at Parser.Run()");

        store.WriteProblem(problem);
        store.DeleteSource("src-deadbeef");

        Assert.Equal(2, store.ExecutedSql.Count);

        string problemSql = NormalizeLineEndings(store.ExecutedSql[0]);
        Assert.StartsWith("PRAGMA foreign_keys = ON;\nBEGIN IMMEDIATE;\n", problemSql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO ingest_problem (problem_key, severity, code, message)", problemSql, StringComparison.Ordinal);
        Assert.Contains(HexExpr("Unusual prompt structure"), problemSql, StringComparison.Ordinal);
        Assert.DoesNotContain("Unusual prompt structure", problemSql, StringComparison.Ordinal);
        Assert.EndsWith("\nCOMMIT;\n", problemSql, StringComparison.Ordinal);

        // The source delete moves the write marker and marks the dirty range wildcard in the same
        // transaction as the delete, so a failed delete moves nothing.
        string deleteSql = NormalizeLineEndings(store.ExecutedSql[1]);
        // Records another source still claims are handed to it before the delete cascades.
        Assert.StartsWith(
            $"PRAGMA foreign_keys = ON;\nBEGIN IMMEDIATE;\nUPDATE record SET source_id = (SELECT c.source_id FROM record_source_claim c ",
            deleteSql,
            StringComparison.Ordinal);
        Assert.Contains(
            $";\nDELETE FROM source WHERE source_id = {HexExpr("src-deadbeef")};\n",
            deleteSql,
            StringComparison.Ordinal);
        Assert.Contains("DELETE FROM projection_dirty_session;\n", deleteSql, StringComparison.Ordinal);
        Assert.Contains("INSERT OR REPLACE INTO projection_dirty_range (singleton_id, from_seq, to_seq)", deleteSql, StringComparison.Ordinal);
        Assert.Contains("INSERT OR REPLACE INTO store_write_marker (singleton_id, records_write_seq)", deleteSql, StringComparison.Ordinal);
        Assert.Single(deleteSql.Split("BEGIN IMMEDIATE;", StringSplitOptions.None).Skip(1));
        Assert.Single(deleteSql.Split("COMMIT;", StringSplitOptions.None).Skip(1));
        Assert.EndsWith("\nCOMMIT;\n", deleteSql, StringComparison.Ordinal);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void ProjectionStoreWrites_EmitTransactionalHexEncodedSqlAcrossAllWriteOperations()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        store.ClearRecordedSql();

        StoredSessionProjection measurableProjection = BuildMeasurableProjection("sess-1");
        StoredSessionProjection unmeasurableProjection = BuildUnmeasurableProjection("sess-2");

        store.ReplaceSessionProjection(measurableProjection);
        store.ReplaceSessionProjections([measurableProjection, unmeasurableProjection]);
        store.DeleteSessionProjection("sess-2");
        store.ReplaceSessionTurns("sess-1", [new StoredSessionTurn("sess-1", 0, "span-1")]);
        store.ReplaceRuns(
        [
            new StoredRun(
                RunId: "run-1",
                HarnessId: "claude-code",
                GroupingBasis: "explicit",
                GroupingRule: "explicit_run_id",
                ExecutionCount: 2,
                Label: "Run label 'test'",
                WorkingDirectory: "/work/repo",
                StartedAtUtc: "2026-11-19T00:00:00Z",
                EndedAtUtc: "2026-11-19T00:10:00Z")
        ]);
        store.DeleteRun("run-old");
        store.ReplaceExecutions(
        [
            new StoredExecution("exec-child", "run-1", "claude-code", false, "sess-1", "exec-root", "explorer", "2026-11-19T00:01:00Z", "2026-11-19T00:05:00Z"),
            new StoredExecution("exec-root", "run-1", "claude-code", true, "sess-1", null, null, null, null)
        ]);
        store.DeleteExecutionsForRun("run-old");
        store.ReplaceHarnessRollups(
        [
            new StoredHarnessRollup(
                HarnessId: "claude-code",
                SessionCount: 10,
                RunCount: 4,
                ExecutionCount: 8,
                UnknownWindowSessions: 1,
                WindowSessionsTotal: 10,
                Metrics: [new StoredHarnessRollupMetric("cache_hit_rate", 0.72, "measured")])
        ]);
        store.DeleteHarnessRollup("old-harness");
        store.ReplaceFindings(
        [
            new StoredFinding(
                FindingId: "find-1",
                DetectorId: "compaction-hazard",
                Title: "Context near limit",
                Mechanism: "Prompt history grew rapidly",
                Confidence: "high",
                Recommendation: "Compact earlier",
                OutcomeRiskCaveat: "Estimated tokens",
                RankScore: 0.91,
                EvidenceLinks: [new FindingEvidenceLink("span-1", 0, "Peak turn")],
                EstimatedWasteTokens: 12000,
                ErrorBar: new FindingErrorBar(10000, 14000),
                RunId: "run-1",
                SessionId: "sess-1",
                MeasurementClass: "measured",
                ConfidenceBasis: "telemetry",
                WhatWouldRaiseIt: "More turns",
                CompactionHazard: new StoredCompactionHazard(200000, "model_table"))
        ]);
        store.DeleteFindingsForSession("sess-old");
        store.ReplacePrediction(
            new StoredPrediction(
                PredictionId: "pred-1",
                FindingId: "find-1",
                RunId: "run-1",
                PredictedWasteTokens: 8000,
                Confidence: 0.85,
                ConfidenceTier: "high",
                CreatedAtUtc: "2026-11-19T00:15:00Z",
                ComparisonRunId: "run-0",
                ObservedDeltaTokens: 7500,
                CalibrationScore: 0.93));
        store.DeletePrediction("pred-old");

        Assert.Equal(14, store.ExecutedSql.Count);
        foreach (string raw in store.ExecutedSql)
        {
            string sql = NormalizeLineEndings(raw);
            Assert.StartsWith("PRAGMA foreign_keys = ON;\nBEGIN IMMEDIATE;\n", sql, StringComparison.Ordinal);
            Assert.EndsWith("\nCOMMIT;\n", sql, StringComparison.Ordinal);
        }

        string replaceProjSql = NormalizeLineEndings(store.ExecutedSql[1]);
        Assert.Contains("INSERT INTO session_projection", replaceProjSql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO session_context", replaceProjSql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO session_context_unmeasurable", replaceProjSql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO session_schema", replaceProjSql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO session_schema_unmeasurable", replaceProjSql, StringComparison.Ordinal);

        string replaceRunsSql = NormalizeLineEndings(store.ExecutedSql[4]);
        Assert.Contains("ON CONFLICT(run_id) DO UPDATE SET", replaceRunsSql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT OR REPLACE INTO run", replaceRunsSql, StringComparison.Ordinal);
        Assert.Contains(HexExpr("Run label 'test'"), replaceRunsSql, StringComparison.Ordinal);
        Assert.DoesNotContain("Run label 'test'", replaceRunsSql, StringComparison.Ordinal);

        string replaceExecSql = NormalizeLineEndings(store.ExecutedSql[6]);
        int lastExecutionInsert = replaceExecSql.LastIndexOf("INSERT INTO execution (", StringComparison.Ordinal);
        int firstParentInsert = replaceExecSql.IndexOf("INSERT INTO execution_parent", StringComparison.Ordinal);
        Assert.True(lastExecutionInsert >= 0);
        Assert.True(firstParentInsert > lastExecutionInsert);

        string deleteFindingsSql = NormalizeLineEndings(store.ExecutedSql[11]);
        Assert.Contains("DELETE FROM finding WHERE finding_id IN (SELECT finding_id FROM finding_session WHERE session_id =", deleteFindingsSql, StringComparison.Ordinal);

        Assert.False(Directory.Exists(rootDir));
    }

    [Theory]
    [InlineData("{\"kind\":\"object\"}")]
    [InlineData(" \n\t [1, 2, 3]\t ")]
    public void WriteGuard_RefusesJsonObjectOrArrayTextAcrossAllWritersWithoutCallingSqlite(string jsonText)
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        store.ClearRecordedSql();

        AssertRefusesJson(() => store.WriteProblem(new CollectedProblem("k1", "warning", "C1", jsonText)), store);
        AssertRefusesJson(() => store.WriteProblem(new CollectedProblem("k1", "warning", "C1", "msg", Stack: jsonText)), store);

        CollectedBatch badSessionBatch = new(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/x.jsonl"),
            [new CollectedSession("sess-1", "claude-code", "nat-1", new CollectedWorkspace(jsonText, null, null), "title", [])],
            [],
            []);
        AssertRefusesJson(() => store.WriteBatch(badSessionBatch), store);

        CollectedRecord goodRecord = BuildFullCollectedBatch("title", "plain text").Records[0];
        CollectedBatch badPartBatch = new(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/x.jsonl"),
            [],
            [goodRecord with { Parts = [new CollectedContentPart(0, ContentBucket.UserPrompt, jsonText, null, null, null, null, null, null)] }],
            []);
        AssertRefusesJson(() => store.WriteBatch(badPartBatch), store);

        CollectedBatch badToolBatch = new(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/x.jsonl"),
            [],
            [goodRecord with { Tool = goodRecord.Tool! with { Result = new CollectedToolContent(jsonText, 10, 10, null, null) } }],
            []);
        AssertRefusesJson(() => store.WriteBatch(badToolBatch), store);

        AssertRefusesJson(
            () => store.ReplaceSessionProjection(BuildMeasurableProjection("sess-1") with { Label = jsonText }),
            store);
        AssertRefusesJson(
            () => store.ReplaceSessionTurns("sess-1", [new StoredSessionTurn("sess-1", 0, jsonText)]),
            store);
        AssertRefusesJson(
            () => store.ReplaceRuns([new StoredRun("run-1", "claude-code", "explicit", "explicit_run_id", 1, Label: jsonText)]),
            store);
        AssertRefusesJson(
            () => store.ReplaceExecutions([new StoredExecution("exec-1", "run-1", "claude-code", true, AgentName: jsonText)]),
            store);
        AssertRefusesJson(
            () => store.ReplaceHarnessRollups([new StoredHarnessRollup(jsonText, 1, 1, 1, 0, 1, [])]),
            store);
        AssertRefusesJson(
            () => store.ReplaceFindings([new StoredFinding("f-1", "d-1", jsonText, "mech", "high", "rec", "caveat", 1.0, [])]),
            store);
        AssertRefusesJson(
            () => store.ReplacePrediction(new StoredPrediction("p-1", "f-1", "run-1", 10, 0.5, jsonText, "2026-11-19T00:00:00Z")),
            store);

        Assert.Empty(store.ExecutedSql);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void ConstraintGuard_RejectsDomainConstraintViolationsBeforeCallingSqlite()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        store.ClearRecordedSql();

        AssertConstraintViolated(
            () => store.ReplaceSessionProjection(BuildMeasurableProjection("s1") with { ContextUnmeasurableReason = "cannot have both" }),
            store);
        AssertConstraintViolated(
            () => store.ReplaceSessionProjection(BuildMeasurableProjection("s1") with { SchemaUnmeasurableReason = "cannot have both" }),
            store);

        AssertConstraintViolated(
            () => store.ReplaceSessionTurns("s1", [new StoredSessionTurn("s2", 0, "span-1")]),
            store);

        AssertConstraintViolated(
            () => store.ReplaceRuns([new StoredRun("r1", "claude-code", "explicit", "explicit_run_id", 1, StartedAtUtc: "2026-11-19T00:00:00Z", EndedAtUtc: null)]),
            store);
        AssertConstraintViolated(
            () => store.ReplaceExecutions([new StoredExecution("e1", "r1", "claude-code", true, StartedAtUtc: null, EndedAtUtc: "2026-11-19T00:00:00Z")]),
            store);

        AssertConstraintViolated(
            () => store.ReplaceFindings([new StoredFinding("f1", "d1", "t1", "m1", "high", "r1", "c1", double.NaN, [])]),
            store);
        AssertConstraintViolated(
            () => store.ReplacePrediction(new StoredPrediction("p1", "f1", "r1", 10, double.PositiveInfinity, "high", "2026-11-19T00:00:00Z")),
            store);
        AssertConstraintViolated(
            () => store.ReplaceHarnessRollups([new StoredHarnessRollup("claude-code", 1, 1, 1, 0, 1, [new StoredHarnessRollupMetric("cache_hit_rate", double.NegativeInfinity, "measured")])]),
            store);

        CollectedBatch duplicateSessionBatch = new(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/a.jsonl"),
            [
                new CollectedSession("s1", "claude-code", "n1", null, null, []),
                new CollectedSession("s1", "claude-code", "n2", null, null, [])
            ],
            [],
            []);
        AssertConstraintViolated(() => store.WriteBatch(duplicateSessionBatch), store);

        CollectedBatch duplicateProblemBatch = new(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/a.jsonl"),
            [],
            [],
            [
                new CollectedProblem("pk1", "warning", "C1", "m1"),
                new CollectedProblem("pk1", "error", "C2", "m2")
            ]);
        AssertConstraintViolated(() => store.WriteBatch(duplicateProblemBatch), store);

        CollectedRecord validRecord = BuildFullCollectedBatch("title", "plain text").Records[0];
        CollectedBatch mismatchedMcpBatch = new(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/a.jsonl"),
            [],
            [validRecord with { Tool = validRecord.Tool! with { McpServerName = "github", McpToolName = null } }],
            []);
        AssertConstraintViolated(() => store.WriteBatch(mismatchedMcpBatch), store);

        CollectedBatch nullSignalsBatch = new(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/a.jsonl"),
            [],
            [validRecord with { Signals = null! }],
            []);
        AssertConstraintViolated(() => store.WriteBatch(nullSignalsBatch), store);

        Assert.Empty(store.ExecutedSql);
        Assert.False(Directory.Exists(rootDir));
    }

    /// <summary>
    /// An empty session key is refused naming the column it would be written to, not the dirty
    /// projection table the write marker advance fills from the same key.
    /// </summary>
    [Fact]
    public void ConstraintGuard_EmptySessionKeyRefusalNamesTheSessionOrRecordLinkColumn()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        RecordingSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());
        store.ClearRecordedSql();

        CollectedBatch emptySessionBatch = new(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/a.jsonl"),
            [new CollectedSession(string.Empty, "claude-code", "n1", null, null, [])],
            [],
            []);
        DashValidationException sessionError = Assert.Throws<DashValidationException>(() => store.WriteBatch(emptySessionBatch));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", sessionError.Code);
        Assert.Contains("'session.session_id'", sessionError.Message, StringComparison.Ordinal);

        CollectedRecord validRecord = BuildFullCollectedBatch("title", "plain text").Records[0];
        CollectedBatch emptyRecordSessionBatch = new(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/a.jsonl"),
            [],
            [validRecord with { SessionKey = string.Empty }],
            []);
        DashValidationException recordError = Assert.Throws<DashValidationException>(() => store.WriteBatch(emptyRecordSessionBatch));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", recordError.Code);
        Assert.Contains("'record_session_link.session_key'", recordError.Message, StringComparison.Ordinal);

        Assert.Empty(store.ExecutedSql);
    }

    private static string NewNonExistentRoot() =>
        Path.Combine(Path.GetTempPath(), $"kyberdash-t7-nowrite-{Guid.NewGuid():N}");

    private static string HexExpr(string value) =>
        $"X'{Convert.ToHexString(Encoding.UTF8.GetBytes(value))}' || ''";

    private static string NormalizeLineEndings(string sql) =>
        sql.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void AssertRefusesJson(Action action, RecordingSqliteRelationalStore store)
    {
        DashValidationException ex = Assert.Throws<DashValidationException>(action);
        Assert.Equal("STORE_JSON_TEXT_REFUSED", ex.Code);
        Assert.Empty(store.ExecutedSql);
    }

    private static void AssertConstraintViolated(Action action, RecordingSqliteRelationalStore store)
    {
        DashValidationException ex = Assert.Throws<DashValidationException>(action);
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", ex.Code);
        Assert.Empty(store.ExecutedSql);
    }

    private static void InspectMethodSignatureAndLocals(MethodBase method)
    {
        if (method is MethodInfo methodInfo)
        {
            AssertNotSystemTextJson(methodInfo.ReturnType, $"{method.DeclaringType?.Name}.{method.Name} return");
        }

        foreach (ParameterInfo parameter in method.GetParameters())
        {
            AssertNotSystemTextJson(parameter.ParameterType, $"{method.DeclaringType?.Name}.{method.Name}({parameter.Name})");
        }

        MethodBody? body = method.GetMethodBody();
        if (body is null)
        {
            return;
        }

        foreach (LocalVariableInfo local in body.LocalVariables)
        {
            AssertNotSystemTextJson(local.LocalType, $"{method.DeclaringType?.Name}.{method.Name} local[{local.LocalIndex.ToString(CultureInfo.InvariantCulture)}]");
        }
    }

    private static void AssertNotSystemTextJson(Type type, string site)
    {
        string? ns = type.Namespace;
        Assert.False(
            ns is not null && (ns.Equals("System.Text.Json", StringComparison.Ordinal) || ns.StartsWith("System.Text.Json.", StringComparison.Ordinal)),
            $"Unexpected System.Text.Json reference at {site}: {type.FullName}");
    }

    private static CollectedBatch BuildFullCollectedBatch(string sessionTitle, string plainPartText)
    {
        DateTimeOffset started = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset ended = new(2026, 11, 19, 10, 0, 5, TimeSpan.Zero);

        CollectedSession session = new(
            SessionKey: "sess-1",
            HarnessId: "claude-code",
            NativeSessionId: "native-1",
            Workspace: new CollectedWorkspace("/work/repo", "kyber-weave", "main"),
            Title: sessionTitle,
            ReportedUsage:
            [
                new CollectedModelUsage(0, "claude-sonnet-4-5", TokenClass.Input, 100),
                new CollectedModelUsage(0, "claude-sonnet-4-5", TokenClass.Output, 50)
            ]);

        CollectedRecord record = new(
            RecordId: "span-1",
            HarnessId: "claude-code",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "llm_turn",
            StartedAt: started,
            Status: RecordStatus.Ok,
            SessionKey: "sess-1",
            ParentRecordId: "span-0",
            TraceId: "trace-1",
            Model: "claude-sonnet-4-5",
            EndedAt: ended,
            ReportedDurationMs: 5000,
            Tokens:
            [
                new CollectedTokenCount(TokenClass.Input, 100),
                new CollectedTokenCount(TokenClass.CacheRead, 200),
                new CollectedTokenCount(TokenClass.CacheWrite, 50),
                new CollectedTokenCount(TokenClass.Output, 40),
                new CollectedTokenCount(TokenClass.Reasoning, 10)
            ],
            ReportedCost: new CollectedCost(0.0125, "USD"),
            Parts:
            [
                new CollectedContentPart(0, ContentBucket.UserPrompt, plainPartText, null, 25, "mcp-docs", "user", true, 64),
                new CollectedContentPart(1, ContentBucket.ToolDefinitions, null, new CollectedWithheldText(512, 500, 128), 128, "mcp-docs", "system", false, null)
            ],
            Tool: new CollectedToolCall(
                CallId: "call-1",
                ToolName: "read_file",
                McpServerName: "fs",
                McpToolName: "read",
                Input: new CollectedToolContent("path=/tmp/foo.txt", 17, 17, DummySha256, 32),
                Result: new CollectedToolContent("file contents", 13, 13, null, 20),
                Succeeded: true,
                Error: new CollectedError("soft warning", "WARN_PARTIAL"),
                ExitCode: 0),
            Agent: new CollectedAgentInvocation("reviewer", "span-0", true, "minor warning"),
            SkillsInvoked: ["csharp-dev", "code-review"],
            AgentInstanceId: "agent-inst-1",
            ToolDefinitions: [new CollectedToolDefinition(0, "read_file", "fs", 120)],
            Signals: new CollectedRecordSignals("warning msg", 1, "no-raw-json", 200000),
            ReportedInputTokens: 350,
            ReportedOutputTokens: 50);

        CollectedProblem problem = new(
            ProblemKey: "prob-batch-1",
            Severity: "warning",
            Code: "MISSING_FIELD",
            Message: "Optional attribute missing",
            SpanId: "span-1",
            Location: "line:10",
            SessionKey: "sess-1",
            HarnessId: "claude-code",
            TimestampUtc: "2026-11-19T10:00:01.000Z",
            Stack: "stack line");

        return new CollectedBatch(
            new CollectedSource("claude-code", SourceKind.Otlp, "http://127.0.0.1:4318/v1/traces"),
            [session],
            [record],
            [problem]);
    }

    private static StoredSessionProjection BuildMeasurableProjection(string sessionId) =>
        new(
            SessionId: sessionId,
            Label: "Session label",
            IsSubagent: true,
            SpanCount: 4,
            StartedAtUtc: "2026-11-19T10:00:00.000Z",
            EndedAtUtc: "2026-11-19T10:05:00.000Z",
            ParentSessionId: "sess-parent",
            AgentName: "dev-hard",
            IncludedInParent: true,
            RequestSpanIds: ["span-1"],
            Turns: [new StoredSessionTurn(sessionId, 0, "span-1")],
            Context: new StoredSessionContext(
                ContextLimit: 200000,
                ContextLimitSource: "model_table",
                ResidualTotal: 1500,
                FreshJumpFactor: 1.2,
                AccumulationRate: 0.35,
                ObservedBuckets: ["user_prompt", "tool_definitions"],
                DerivedCounts: [new StoredSessionContextDerivedCount("turns", 1)],
                FlaggedTurnIndexes: [0],
                Turns:
                [
                    new StoredSessionContextTurn(
                        Position: 0,
                        InputTokens: 1000,
                        BucketedTokens: 900,
                        ResidualTokens: 100,
                        ResidualAttribution: "unattributed",
                        BuiltinToolDefinitionTokens: 200,
                        FreshInput: 400,
                        Buckets: [new StoredTurnBucketTokens("user_prompt", 300)],
                        ServerDefinitions: [new StoredTurnServerDefinitionTokens("fs", 200)],
                        Headroom: 199000,
                        Pressure: 0.005,
                        AccumulationRate: 0.25,
                        StrippedBlockCount: 1,
                        StrippedTokens: 50,
                        PreviousFreshInput: 350,
                        FreshJumpFactor: 1.14,
                        NonResidentTokens: 40)
                ]),
            ContextUnmeasurableReason: null,
            Schema: new StoredSessionSchema(
                InvocationCount: 2,
                TurnCount: 1,
                RankedTools: [new StoredSchemaRankedTool(0, "read_file", 120, true, "fs")],
                Servers: [new StoredSchemaServerTokens("fs", 120)],
                UnusedTokenResidencies: 10),
            SchemaUnmeasurableReason: null,
            TimelineSubagentSpanIds: ["span-sub"],
            TimelineAuxiliarySpanIds: ["span-aux"]);

    private static StoredSessionProjection BuildUnmeasurableProjection(string sessionId) =>
        new(
            SessionId: sessionId,
            Label: "Unmeasurable session",
            IsSubagent: false,
            SpanCount: 1,
            StartedAtUtc: "2026-11-19T11:00:00.000Z",
            EndedAtUtc: "2026-11-19T11:01:00.000Z",
            ParentSessionId: null,
            AgentName: null,
            IncludedInParent: false,
            RequestSpanIds: [],
            Turns: [],
            Context: null,
            ContextUnmeasurableReason: "no token telemetry",
            Schema: null,
            SchemaUnmeasurableReason: "no tool definitions",
            TimelineSubagentSpanIds: null,
            TimelineAuxiliarySpanIds: null);

    private class RecordingSqliteRelationalStore : SqliteRelationalStore
    {
        private readonly List<string> executedSql = [];
        private readonly Queue<string> queuedResponses = new();

        public RecordingSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
            : base(databasePath, circuitBreaker)
        {
        }

        public RecordingSqliteRelationalStore(DashOptions options, ICircuitBreaker circuitBreaker)
            : base(options, circuitBreaker)
        {
        }

        public RecordingSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker, bool initialize)
            : base(databasePath, circuitBreaker, initialize)
        {
        }

        public IReadOnlyList<string> ExecutedSql => executedSql;

        public void EnqueueResponse(string response) => queuedResponses.Enqueue(response);

        public void ClearRecordedSql() => executedSql.Clear();

        protected override string ExecuteSqliteCore(string sql)
        {
            executedSql.Add(sql);
            return queuedResponses.Count > 0 ? queuedResponses.Dequeue() : string.Empty;
        }
    }
}
