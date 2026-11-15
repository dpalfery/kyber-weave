using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KyberWeave.Core.Dash;

/// <summary>Summary counts returned by a full or harness-scoped store wipe.</summary>
public sealed record StoreWipeReport(
    int RecordsDeleted,
    int SessionsDeleted,
    int TurnsDeleted,
    int RunsDeleted,
    int ExecutionsDeleted,
    int FindingsDeleted,
    int RollupsDeleted,
    int ProblemsDeleted,
    int QuarantineDeleted,
    string LastCleanAt);

/// <summary>Derived run grouping persisted in <c>run</c>.</summary>
public sealed record RunRow(
    string RunId,
    string Harness,
    string? Label,
    string GroupingBasis,
    string? GroupingRule,
    string? WorkingDirectory,
    string? Started,
    string? Ended,
    int ExecutionCount,
    JsonElement? Payload = null);

/// <summary>Agent execution linkage persisted in <c>execution</c>.</summary>
public sealed record ExecutionRow(
    string ExecutionId,
    string RunId,
    string? SessionId,
    string? ParentExecutionId,
    string Harness,
    string? AgentName,
    bool IsRoot,
    string? Started,
    string? Ended,
    JsonElement ParentLinkage,
    JsonElement? Payload = null);

/// <summary>Per-harness efficiency and measurability rollup persisted in <c>harness_rollup</c>.</summary>
public sealed record HarnessRollupRow(
    string Harness,
    int SampleCount,
    double? ContextPressureMedian,
    double? ContextPressureP95,
    double? CacheHitRate,
    double? ToolYield,
    double? DelegationOverhead,
    double? FieldCoverage,
    IReadOnlyDictionary<string, JsonElement> Measurability,
    JsonElement? Payload = null);

/// <summary>Recorded prediction and calibration outcome persisted in <c>prediction</c>.</summary>
public sealed record PredictionRow(
    string Id,
    string FindingId,
    string RunId,
    long PredictedWasteTokens,
    double Confidence,
    string? ConfidenceTier,
    JsonElement? ErrorBar,
    string CreatedAt,
    string? ComparisonRunId = null,
    long? ObservedDeltaTokens = null,
    double? CalibrationScore = null,
    JsonElement? Payload = null);

/// <summary>Problem row including its database row identifier from <c>problems</c>.</summary>
public sealed record StoredProblemRow(
    int Id,
    string SpanId,
    string Severity,
    string Code,
    string Message,
    string? Location,
    string? SessionId,
    string? Harness,
    string? Timestamp,
    string? Stack);

/// <summary>Source-unit coverage checkpoint persisted in <c>source_checkpoint</c>.</summary>
public sealed record SourceCheckpoint(
    string HarnessId,
    string SourceKey,
    string ProviderId,
    string ParserId,
    string ParserContractVersion,
    string Format,
    string SourceRootLabel,
    string RevisionToken,
    string CoveredFromUtc,
    string CoveredThroughUtc,
    string LastAttemptUtc,
    string? LastSuccessUtc,
    string LastStatus,
    string? LastErrorCode,
    int UnitCount,
    int RecordCount);

/// <summary>Per-record provenance persisted in <c>record_provenance</c>.</summary>
public sealed record RecordProvenanceRow(
    string SpanId,
    string HarnessId,
    string SourceKey,
    string? NativeSessionId,
    string? NativeRecordId,
    string SourceRevision,
    string ParserVersion,
    string ImportedAtUtc,
    string LocationToken);

/// <summary>Refresh execution audit entry persisted in <c>refresh_run</c>.</summary>
public sealed record RefreshRunRow(
    string Id,
    string StartedAt,
    string? CompletedAt,
    string Status,
    int Pid,
    string Trigger,
    string? Summary,
    int? HistoryWeeks);

public sealed partial class CanonStore
{
    private const string CleanStampMetadataKey = "last_clean_at";

    /// <summary>Returns the current row count of <c>records</c>.</summary>
    public int CountRecords()
    {
        return ExecuteScalarInt("SELECT COUNT(*) FROM records;");
    }

    /// <summary>Returns the maximum SQLite <c>rowid</c> in <c>records</c>, or <see langword="null"/> if empty.</summary>
    public long? GetMaxRecordRowId()
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT typeof(MAX(rowid)), COALESCE(MAX(rowid), 0) FROM records;");

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length >= 2 && !string.Equals(cols[0], "null", StringComparison.Ordinal))
            {
                if (long.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long high))
                    return high;
            }
        }

        return null;
    }

    /// <summary>
    /// Computes the deterministic 32-bit FNV-1a store generation over <c>last_clean_at</c>,
    /// total record count, and maximum <c>records.rowid</c> (<c>dash/src/server/store-generation.ts</c>).
    /// </summary>
    public uint ComputeStoreGeneration()
    {
        try
        {
            string cleanStamp = GetMetadata(CleanStampMetadataKey) ?? string.Empty;
            string count = CountRecords().ToString(CultureInfo.InvariantCulture);
            long? maxRow = GetMaxRecordRowId();
            string highWater = maxRow.HasValue ? maxRow.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

            uint hash = 0x811c9dc5u;
            string[] parts = [cleanStamp, count, highWater];
            foreach (string part in parts)
            {
                foreach (char ch in part)
                {
                    hash ^= ch;
                    hash *= 0x01000193u;
                }

                hash ^= 0x2fu;
                hash *= 0x01000193u;
            }

            return hash;
        }
        catch (InvalidOperationException)
        {
            return 0u;
        }
    }

    /// <summary>Returns distinct non-empty harness names present in <c>records</c> or <c>session</c>.</summary>
    public IReadOnlyList<string> ListObservedHarnesses()
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT DISTINCT hex(CAST(harness AS BLOB)) FROM (\n" +
            "  SELECT harness FROM records WHERE harness IS NOT NULL AND harness != ''\n" +
            "  UNION\n" +
            "  SELECT harness FROM session WHERE harness IS NOT NULL AND harness != ''\n" +
            ") ORDER BY 1;");

        List<string> harnesses = [];
        foreach (string line in Lines(output))
            harnesses.Add(DecodeHexText(line));
        return harnesses;
    }

    /// <summary>
    /// Wipes all telemetry and derived projection tables while preserving <c>metadata</c> operator
    /// settings and <c>token_cache</c>.
    /// </summary>
    public StoreWipeReport WipeAll()
    {
        string nowIso = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        lock (writeGate)
        {
            int records = ExecuteScalarInt("SELECT COUNT(*) FROM records;");
            int sessions = ExecuteScalarInt("SELECT COUNT(*) FROM session;");
            int turns = HasSessionTurnTable() ? ExecuteScalarInt("SELECT COUNT(*) FROM session_turn;") : 0;
            int runs = ExecuteScalarInt("SELECT COUNT(*) FROM run;");
            int executions = ExecuteScalarInt("SELECT COUNT(*) FROM execution;");
            int findings = ExecuteScalarInt("SELECT COUNT(*) FROM finding;");
            int rollups = ExecuteScalarInt("SELECT COUNT(*) FROM harness_rollup;");
            int problems = ExecuteScalarInt("SELECT COUNT(*) FROM problems;");
            int quarantine = ExecuteScalarInt("SELECT COUNT(*) FROM quarantine;");

            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            script.AppendLine("DELETE FROM records;");
            if (HasSessionTurnTable())
                script.AppendLine("DELETE FROM session_turn;");
            script.AppendLine("DELETE FROM session;");
            script.AppendLine("DELETE FROM finding;");
            script.AppendLine("DELETE FROM execution;");
            script.AppendLine("DELETE FROM run;");
            script.AppendLine("DELETE FROM harness_rollup;");
            script.AppendLine("DELETE FROM problems;");
            script.AppendLine("DELETE FROM quarantine;");
            script.AppendLine("DELETE FROM pending_logs;");
            script.AppendLine("DELETE FROM quarantined_logs;");
            script.AppendLine("DELETE FROM enriched_logs;");
            script.AppendLine("DELETE FROM metric_point;");
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CleanStampMetadataKey)}, {TextExpr(nowIso)});");
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"DELETE FROM metadata WHERE key IN ({TextExpr(CanonSchema.ProjectionDirtySessionsKey)}, {TextExpr(CanonSchema.ProjectionSessionKeyMapKey)});");
            AppendBumpRecordsWriteMarker(script);
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());

            return new StoreWipeReport(
                records,
                sessions,
                turns,
                runs,
                executions,
                findings,
                rollups,
                problems,
                quarantine,
                nowIso);
        }
    }

    /// <summary>
    /// Wipes rows belonging to <paramref name="harnesses"/> (and their normalized canonical forms)
    /// across telemetry and derived tables while leaving other harnesses untouched.
    /// </summary>
    public StoreWipeReport WipeHarnesses(IReadOnlyList<string> harnesses)
    {
        ArgumentNullException.ThrowIfNull(harnesses);
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (string h in harnesses)
        {
            if (string.IsNullOrWhiteSpace(h))
                continue;

            string trimmed = h.Trim();
            targets.Add(trimmed);
            targets.Add(CanonPricing.NormalizeHarnessName(trimmed));
            targets.Add(CanonPricing.HarnessFamily(trimmed));
        }

        string nowIso = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        if (targets.Count == 0)
            return new StoreWipeReport(0, 0, 0, 0, 0, 0, 0, 0, 0, nowIso);

        string inList = string.Join(", ", targets.Order(StringComparer.Ordinal).Select(TextExpr));
        string harnessFilter = $"harness IN ({inList})";
        string sessionSubquery = $"SELECT session_id FROM session WHERE {harnessFilter}";
        string runSubquery = $"SELECT run_id FROM run WHERE {harnessFilter}";

        lock (writeGate)
        {
            int records = ExecuteScalarInt($"SELECT COUNT(*) FROM records WHERE {harnessFilter};");
            int sessions = ExecuteScalarInt($"SELECT COUNT(*) FROM session WHERE {harnessFilter};");
            int turns = HasSessionTurnTable()
                ? ExecuteScalarInt($"SELECT COUNT(*) FROM session_turn WHERE session_id IN ({sessionSubquery});")
                : 0;
            int runs = ExecuteScalarInt($"SELECT COUNT(*) FROM run WHERE {harnessFilter};");
            int executions = ExecuteScalarInt($"SELECT COUNT(*) FROM execution WHERE {harnessFilter};");
            int findings = ExecuteScalarInt(
                $"SELECT COUNT(*) FROM finding WHERE session_id IN ({sessionSubquery}) OR run_id IN ({runSubquery});");
            int rollups = ExecuteScalarInt($"SELECT COUNT(*) FROM harness_rollup WHERE {harnessFilter};");
            int problems = ExecuteScalarInt($"SELECT COUNT(*) FROM problems WHERE {harnessFilter};");
            int quarantine = ExecuteScalarInt($"SELECT COUNT(*) FROM quarantine WHERE {harnessFilter};");

            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM finding WHERE session_id IN ({sessionSubquery}) OR run_id IN ({runSubquery});");
            if (HasSessionTurnTable())
                script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM session_turn WHERE session_id IN ({sessionSubquery});");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM session WHERE {harnessFilter};");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM execution WHERE {harnessFilter};");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM run WHERE {harnessFilter};");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM harness_rollup WHERE {harnessFilter};");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM problems WHERE {harnessFilter};");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM quarantine WHERE {harnessFilter};");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM pending_logs WHERE {harnessFilter};");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM quarantined_logs WHERE {harnessFilter};");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM records WHERE {harnessFilter};");
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CleanStampMetadataKey)}, {TextExpr(nowIso)});");
            AppendDirtySessionUpdate(script, dirtyKeys: null);
            AppendBumpRecordsWriteMarker(script);
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());

            return new StoreWipeReport(
                records,
                sessions,
                turns,
                runs,
                executions,
                findings,
                rollups,
                problems,
                quarantine,
                nowIso);
        }
    }

    /// <summary>Loads decompressed turn payloads ordered by <c>turn_index ASC</c> for <paramref name="sessionId"/>.</summary>
    public IReadOnlyList<JsonElement> ListSessionTurnPayloads(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!HasSessionTurnTable())
        {
            SessionSummaryRow? session = GetSession(sessionId);
            return session?.Turns ?? [];
        }

        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            $"SELECT typeof(turn_json), hex(CAST(turn_json AS BLOB)) FROM session_turn WHERE session_id = {TextExpr(sessionId)} ORDER BY turn_index ASC;");

        List<JsonElement> turns = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 2)
                continue;

            byte[] rawBytes = Convert.FromHexString(cols[1]);
            string turnJson = string.Equals(cols[0], "blob", StringComparison.Ordinal)
                ? CanonCompressedColumn.Decompress(rawBytes)
                : Encoding.UTF8.GetString(rawBytes);

            try
            {
                using JsonDocument doc = JsonDocument.Parse(turnJson);
                turns.Add(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
            }
        }

        if (turns.Count == 0)
        {
            SessionSummaryRow? session = GetSession(sessionId);
            if (session?.Turns is { Count: > 0 } embedded)
                return embedded;
        }

        return turns;
    }

    /// <summary>Loads one decompressed turn payload by <paramref name="sessionId"/> and <paramref name="turnIndex"/>.</summary>
    public JsonElement? GetSessionTurnPayload(string sessionId, int turnIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (HasSessionTurnTable())
        {
            string output = ExecuteSqlite(
                ".mode tabs\n" +
                ".headers off\n" +
                $"SELECT typeof(turn_json), hex(CAST(turn_json AS BLOB)) FROM session_turn WHERE session_id = {TextExpr(sessionId)} AND turn_index = {turnIndex.ToString(CultureInfo.InvariantCulture)} LIMIT 1;");

            foreach (string line in TabLines(output))
            {
                string[] cols = line.Split('\t', StringSplitOptions.None);
                if (cols.Length < 2)
                    continue;

                byte[] rawBytes = Convert.FromHexString(cols[1]);
                string turnJson = string.Equals(cols[0], "blob", StringComparison.Ordinal)
                    ? CanonCompressedColumn.Decompress(rawBytes)
                    : Encoding.UTF8.GetString(rawBytes);

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(turnJson);
                    return doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        IReadOnlyList<JsonElement> allTurns = ListSessionTurnPayloads(sessionId);
        foreach (JsonElement turn in allTurns)
        {
            if (turn.ValueKind == JsonValueKind.Object
                && turn.TryGetProperty("index", out JsonElement idxEl)
                && idxEl.ValueKind == JsonValueKind.Number
                && idxEl.TryGetInt32(out int idx)
                && idx == turnIndex)
            {
                return turn;
            }
        }

        return turnIndex >= 0 && turnIndex < allTurns.Count ? allTurns[turnIndex] : null;
    }

    /// <summary>Upserts a batch of derived runs into <c>run</c>.</summary>
    public void UpsertRuns(IReadOnlyList<RunRow> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (RunRow run in runs)
            {
                ArgumentNullException.ThrowIfNull(run);
                string? payloadJson = run.Payload.HasValue ? run.Payload.Value.GetRawText() : null;
                string sql = string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT OR REPLACE INTO run (run_id, harness, label, grouping_basis, grouping_rule, working_directory, started, ended, execution_count, payload) VALUES ({TextExpr(run.RunId)}, {TextExpr(run.Harness)}, {NullableTextExpr(run.Label)}, {TextExpr(run.GroupingBasis)}, {NullableTextExpr(run.GroupingRule)}, {NullableTextExpr(run.WorkingDirectory)}, {NullableTextExpr(run.Started)}, {NullableTextExpr(run.Ended)}, {run.ExecutionCount}, {NullableTextExpr(payloadJson)});");
                script.AppendLine(sql);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>Loads one run by <paramref name="runId"/>, or <see langword="null"/> if absent.</summary>
    public RunRow? GetRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        IReadOnlyList<RunRow> rows = QueryRuns($"WHERE run_id = {TextExpr(runId)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Lists runs ordered by <c>started DESC, run_id ASC</c>, optionally filtered by harness.</summary>
    public IReadOnlyList<RunRow> ListRuns(string? harness = null)
    {
        if (string.IsNullOrWhiteSpace(harness))
            return QueryRuns("ORDER BY started DESC, run_id ASC");

        return QueryRuns($"WHERE harness = {TextExpr(harness.Trim())} ORDER BY started DESC, run_id ASC");
    }

    /// <summary>Upserts agent execution rows into <c>execution</c>.</summary>
    public void UpsertExecutions(IReadOnlyList<ExecutionRow> executions)
    {
        ArgumentNullException.ThrowIfNull(executions);
        if (executions.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (ExecutionRow exec in executions)
            {
                ArgumentNullException.ThrowIfNull(exec);
                string linkageJson = exec.ParentLinkage.ValueKind == JsonValueKind.Undefined
                    ? "{}"
                    : exec.ParentLinkage.GetRawText();
                string? payloadJson = exec.Payload.HasValue ? exec.Payload.Value.GetRawText() : null;
                string sql = string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT OR REPLACE INTO execution (execution_id, run_id, session_id, parent_execution_id, harness, agent_name, is_root, started, ended, parent_linkage_json, payload) VALUES ({TextExpr(exec.ExecutionId)}, {TextExpr(exec.RunId)}, {NullableTextExpr(exec.SessionId)}, {NullableTextExpr(exec.ParentExecutionId)}, {TextExpr(exec.Harness)}, {NullableTextExpr(exec.AgentName)}, {(exec.IsRoot ? 1 : 0)}, {NullableTextExpr(exec.Started)}, {NullableTextExpr(exec.Ended)}, {TextExpr(linkageJson)}, {NullableTextExpr(payloadJson)});");
                script.AppendLine(sql);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>Lists executions scoped to <paramref name="runId"/> ordered by <c>started ASC</c>.</summary>
    public IReadOnlyList<ExecutionRow> ListExecutionsForRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return QueryExecutions($"WHERE run_id = {TextExpr(runId)} ORDER BY started ASC, execution_id ASC");
    }

    /// <summary>Lists executions scoped to <paramref name="harness"/> ordered by <c>started DESC</c>.</summary>
    public IReadOnlyList<ExecutionRow> ListExecutionsByHarness(string harness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        return QueryExecutions($"WHERE harness = {TextExpr(harness)} ORDER BY started DESC, execution_id ASC");
    }

    /// <summary>Inserts or replaces a harness rollup row into <c>harness_rollup</c>.</summary>
    public void UpsertHarnessRollup(HarnessRollupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string measJson = JsonSerializer.Serialize(row.Measurability, RecordJsonOptions);
        string? payloadJson = row.Payload.HasValue ? row.Payload.Value.GetRawText() : null;

        lock (writeGate)
        {
            string sql = string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO harness_rollup (harness, sample_count, context_pressure_median, context_pressure_p95, cache_hit_rate, tool_yield, delegation_overhead, field_coverage, measurability_json, payload) VALUES ({TextExpr(row.Harness)}, {row.SampleCount}, {NullableDoubleSql(row.ContextPressureMedian)}, {NullableDoubleSql(row.ContextPressureP95)}, {NullableDoubleSql(row.CacheHitRate)}, {NullableDoubleSql(row.ToolYield)}, {NullableDoubleSql(row.DelegationOverhead)}, {NullableDoubleSql(row.FieldCoverage)}, {TextExpr(measJson)}, {NullableTextExpr(payloadJson)});");

            ExecuteSqlite(
                "BEGIN IMMEDIATE;\n" +
                sql + "\n" +
                "COMMIT;");
        }
    }

    /// <summary>Loads one harness rollup by <paramref name="harness"/>, or <see langword="null"/> if absent.</summary>
    public HarnessRollupRow? GetHarnessRollup(string harness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        IReadOnlyList<HarnessRollupRow> rows = QueryHarnessRollups($"WHERE harness = {TextExpr(harness)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Lists all persisted harness rollups ordered by <c>harness ASC</c>.</summary>
    public IReadOnlyList<HarnessRollupRow> ListHarnessRollups()
    {
        return QueryHarnessRollups("ORDER BY harness ASC");
    }

    /// <summary>Deletes a stale harness rollup row.</summary>
    public void DeleteHarnessRollup(string harness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        lock (writeGate)
        {
            ExecuteSqlite(
                "BEGIN IMMEDIATE;\n" +
                $"DELETE FROM harness_rollup WHERE harness = {TextExpr(harness)};\n" +
                "COMMIT;");
        }
    }

    /// <summary>Inserts or replaces a prediction record in <c>prediction</c>.</summary>
    public void UpsertPrediction(PredictionRow prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        string? errorBarJson = prediction.ErrorBar.HasValue ? prediction.ErrorBar.Value.GetRawText() : null;
        string? payloadJson = prediction.Payload.HasValue ? prediction.Payload.Value.GetRawText() : null;

        lock (writeGate)
        {
            string sql = string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO prediction (id, finding_id, run_id, predicted_waste_tokens, confidence, confidence_tier, error_bar_json, created_at, comparison_run_id, observed_delta_tokens, calibration_score, payload) VALUES ({TextExpr(prediction.Id)}, {TextExpr(prediction.FindingId)}, {TextExpr(prediction.RunId)}, {prediction.PredictedWasteTokens}, {prediction.Confidence.ToString("G17", CultureInfo.InvariantCulture)}, {NullableTextExpr(prediction.ConfidenceTier)}, {NullableTextExpr(errorBarJson)}, {TextExpr(prediction.CreatedAt)}, {NullableTextExpr(prediction.ComparisonRunId)}, {(prediction.ObservedDeltaTokens.HasValue ? prediction.ObservedDeltaTokens.Value.ToString(CultureInfo.InvariantCulture) : "NULL")}, {NullableDoubleSql(prediction.CalibrationScore)}, {NullableTextExpr(payloadJson)});");

            ExecuteSqlite(
                "BEGIN IMMEDIATE;\n" +
                sql + "\n" +
                "COMMIT;");
        }
    }

    /// <summary>Lists predictions ordered newest-first by <c>created_at DESC</c>.</summary>
    public IReadOnlyList<PredictionRow> ListPredictions(string? runId = null)
    {
        string where = string.IsNullOrWhiteSpace(runId) ? string.Empty : $"WHERE run_id = {TextExpr(runId)} ";
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(id AS BLOB)), " +
            "hex(CAST(finding_id AS BLOB)), " +
            "hex(CAST(run_id AS BLOB)), " +
            "predicted_waste_tokens, " +
            "confidence, " +
            "typeof(confidence_tier), COALESCE(hex(CAST(confidence_tier AS BLOB)), ''), " +
            "typeof(error_bar_json), COALESCE(hex(CAST(error_bar_json AS BLOB)), ''), " +
            "hex(CAST(created_at AS BLOB)), " +
            "typeof(comparison_run_id), COALESCE(hex(CAST(comparison_run_id AS BLOB)), ''), " +
            "typeof(observed_delta_tokens), COALESCE(observed_delta_tokens, 0), " +
            "typeof(calibration_score), COALESCE(calibration_score, 0.0), " +
            "typeof(payload), COALESCE(hex(CAST(payload AS BLOB)), '') " +
            $"FROM prediction {where}ORDER BY created_at DESC, id ASC;");

        List<PredictionRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 18)
                continue;

            JsonElement? errorBar = cols[7] == "null" ? null : ParseJsonOrNull(DecodeHexText(cols[8]));
            JsonElement? payload = cols[16] == "null" ? null : ParseJsonOrNull(DecodeHexText(cols[17]));
            rows.Add(new PredictionRow(
                Id: DecodeHexText(cols[0]),
                FindingId: DecodeHexText(cols[1]),
                RunId: DecodeHexText(cols[2]),
                PredictedWasteTokens: long.Parse(cols[3], NumberStyles.Integer, CultureInfo.InvariantCulture),
                Confidence: double.Parse(cols[4], NumberStyles.Float, CultureInfo.InvariantCulture),
                ConfidenceTier: cols[5] == "null" ? null : DecodeHexText(cols[6]),
                ErrorBar: errorBar,
                CreatedAt: DecodeHexText(cols[9]),
                ComparisonRunId: cols[10] == "null" ? null : DecodeHexText(cols[11]),
                ObservedDeltaTokens: cols[12] == "null" ? null : long.Parse(cols[13], NumberStyles.Integer, CultureInfo.InvariantCulture),
                CalibrationScore: cols[14] == "null" ? null : double.Parse(cols[15], NumberStyles.Float, CultureInfo.InvariantCulture),
                Payload: payload));
        }

        return rows;
    }

    /// <summary>Counts quarantined spans optionally filtered by <paramref name="harness"/>.</summary>
    public int CountQuarantine(string? harness = null)
    {
        string where = string.IsNullOrWhiteSpace(harness) ? string.Empty : $" WHERE harness = {TextExpr(harness.Trim())}";
        return ExecuteScalarInt($"SELECT COUNT(*) FROM quarantine{where};");
    }

    /// <summary>Lists quarantined spans ordered newest-first, with optional pagination.</summary>
    public IReadOnlyList<QuarantineRow> ListQuarantine(string? harness = null, int? limit = null, int? offset = null)
    {
        string where = string.IsNullOrWhiteSpace(harness) ? string.Empty : $" WHERE harness = {TextExpr(harness.Trim())}";
        string limitSql = limit.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $" LIMIT {Math.Max(1, limit.Value)} OFFSET {Math.Max(0, offset ?? 0)}")
            : string.Empty;

        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(span_id AS BLOB)), " +
            "hex(CAST(namespaces AS BLOB)), " +
            "hex(CAST(reason AS BLOB)), " +
            "typeof(source), COALESCE(hex(CAST(source AS BLOB)), ''), " +
            "typeof(name), COALESCE(hex(CAST(name AS BLOB)), ''), " +
            "typeof(timestamp), COALESCE(hex(CAST(timestamp AS BLOB)), ''), " +
            "typeof(raw_payload), COALESCE(hex(CAST(raw_payload AS BLOB)), ''), " +
            "typeof(harness), COALESCE(hex(CAST(harness AS BLOB)), '') " +
            $"FROM quarantine{where} ORDER BY timestamp DESC, span_id ASC{limitSql};");

        List<QuarantineRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 13)
                continue;

            List<string> namespaces = JsonSerializer.Deserialize<List<string>>(DecodeHexText(cols[1]), RecordJsonOptions) ?? [];
            rows.Add(new QuarantineRow(
                SpanId: DecodeHexText(cols[0]),
                ObservedNamespaces: namespaces,
                Reason: DecodeHexText(cols[2]),
                Source: cols[3] == "null" ? null : DecodeHexText(cols[4]),
                Name: cols[5] == "null" ? null : DecodeHexText(cols[6]),
                Timestamp: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                RawPayload: cols[9] == "null" ? null : DecodeHexText(cols[10]),
                Harness: cols[11] == "null" ? null : DecodeHexText(cols[12])));
        }

        return rows;
    }

    /// <summary>Counts recorded problems optionally filtered by <paramref name="harness"/>.</summary>
    public int CountProblems(string? harness = null)
    {
        string where = string.IsNullOrWhiteSpace(harness) ? string.Empty : $" WHERE harness = {TextExpr(harness.Trim())}";
        return ExecuteScalarInt($"SELECT COUNT(*) FROM problems{where};");
    }

    /// <summary>Loads a single problem row by numeric identifier, or <see langword="null"/> if absent.</summary>
    public StoredProblemRow? GetProblemById(int id)
    {
        IReadOnlyList<StoredProblemRow> rows = QueryStoredProblems(
            string.Create(CultureInfo.InvariantCulture, $"WHERE id = {id} LIMIT 1"));
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Lists problems ordered newest-first with optional harness filter and pagination.</summary>
    public IReadOnlyList<StoredProblemRow> ListProblems(string? harness = null, int? limit = null, int? offset = null)
    {
        string where = string.IsNullOrWhiteSpace(harness) ? string.Empty : $"WHERE harness = {TextExpr(harness.Trim())} ";
        string limitSql = limit.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $" LIMIT {Math.Max(1, limit.Value)} OFFSET {Math.Max(0, offset ?? 0)}")
            : string.Empty;

        return QueryStoredProblems($"{where}ORDER BY id DESC{limitSql}");
    }

    private int ExecuteScalarInt(string querySql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            querySql);

        foreach (string line in Lines(output))
        {
            if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                return val;
        }

        return 0;
    }

    private IReadOnlyList<RunRow> QueryRuns(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(run_id AS BLOB)), " +
            "hex(CAST(harness AS BLOB)), " +
            "typeof(label), COALESCE(hex(CAST(label AS BLOB)), ''), " +
            "hex(CAST(grouping_basis AS BLOB)), " +
            "typeof(grouping_rule), COALESCE(hex(CAST(grouping_rule AS BLOB)), ''), " +
            "typeof(working_directory), COALESCE(hex(CAST(working_directory AS BLOB)), ''), " +
            "typeof(started), COALESCE(hex(CAST(started AS BLOB)), ''), " +
            "typeof(ended), COALESCE(hex(CAST(ended AS BLOB)), ''), " +
            "execution_count, " +
            "typeof(payload), COALESCE(hex(CAST(payload AS BLOB)), '') " +
            $"FROM run {suffixSql};");

        List<RunRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 16)
                continue;

            JsonElement? payload = cols[14] == "null" ? null : ParseJsonOrNull(DecodeHexText(cols[15]));
            rows.Add(new RunRow(
                RunId: DecodeHexText(cols[0]),
                Harness: DecodeHexText(cols[1]),
                Label: cols[2] == "null" ? null : DecodeHexText(cols[3]),
                GroupingBasis: DecodeHexText(cols[4]),
                GroupingRule: cols[5] == "null" ? null : DecodeHexText(cols[6]),
                WorkingDirectory: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                Started: cols[9] == "null" ? null : DecodeHexText(cols[10]),
                Ended: cols[11] == "null" ? null : DecodeHexText(cols[12]),
                ExecutionCount: int.Parse(cols[13], NumberStyles.Integer, CultureInfo.InvariantCulture),
                Payload: payload));
        }

        return rows;
    }

    private IReadOnlyList<ExecutionRow> QueryExecutions(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(execution_id AS BLOB)), " +
            "hex(CAST(run_id AS BLOB)), " +
            "typeof(session_id), COALESCE(hex(CAST(session_id AS BLOB)), ''), " +
            "typeof(parent_execution_id), COALESCE(hex(CAST(parent_execution_id AS BLOB)), ''), " +
            "hex(CAST(harness AS BLOB)), " +
            "typeof(agent_name), COALESCE(hex(CAST(agent_name AS BLOB)), ''), " +
            "is_root, " +
            "typeof(started), COALESCE(hex(CAST(started AS BLOB)), ''), " +
            "typeof(ended), COALESCE(hex(CAST(ended AS BLOB)), ''), " +
            "hex(CAST(parent_linkage_json AS BLOB)), " +
            "typeof(payload), COALESCE(hex(CAST(payload AS BLOB)), '') " +
            $"FROM execution {suffixSql};");

        List<ExecutionRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 17)
                continue;

            JsonElement linkage = ParseJsonOrNull(DecodeHexText(cols[14]))
                ?? JsonSerializer.SerializeToElement(new { status = "not_measurable" });
            JsonElement? payload = cols[15] == "null" ? null : ParseJsonOrNull(DecodeHexText(cols[16]));
            rows.Add(new ExecutionRow(
                ExecutionId: DecodeHexText(cols[0]),
                RunId: DecodeHexText(cols[1]),
                SessionId: cols[2] == "null" ? null : DecodeHexText(cols[3]),
                ParentExecutionId: cols[4] == "null" ? null : DecodeHexText(cols[5]),
                Harness: DecodeHexText(cols[6]),
                AgentName: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                IsRoot: cols[9] != "0",
                Started: cols[10] == "null" ? null : DecodeHexText(cols[11]),
                Ended: cols[12] == "null" ? null : DecodeHexText(cols[13]),
                ParentLinkage: linkage,
                Payload: payload));
        }

        return rows;
    }

    private IReadOnlyList<HarnessRollupRow> QueryHarnessRollups(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(harness AS BLOB)), " +
            "sample_count, " +
            "typeof(context_pressure_median), COALESCE(context_pressure_median, 0.0), " +
            "typeof(context_pressure_p95), COALESCE(context_pressure_p95, 0.0), " +
            "typeof(cache_hit_rate), COALESCE(cache_hit_rate, 0.0), " +
            "typeof(tool_yield), COALESCE(tool_yield, 0.0), " +
            "typeof(delegation_overhead), COALESCE(delegation_overhead, 0.0), " +
            "typeof(field_coverage), COALESCE(field_coverage, 0.0), " +
            "hex(CAST(measurability_json AS BLOB)), " +
            "typeof(payload), COALESCE(hex(CAST(payload AS BLOB)), '') " +
            $"FROM harness_rollup {suffixSql};");

        List<HarnessRollupRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 17)
                continue;

            Dictionary<string, JsonElement> measurability =
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(DecodeHexText(cols[14]), RecordJsonOptions)
                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            JsonElement? payload = cols[15] == "null" ? null : ParseJsonOrNull(DecodeHexText(cols[16]));

            rows.Add(new HarnessRollupRow(
                Harness: DecodeHexText(cols[0]),
                SampleCount: int.Parse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture),
                ContextPressureMedian: ParseNullableDouble(cols[2], cols[3]),
                ContextPressureP95: ParseNullableDouble(cols[4], cols[5]),
                CacheHitRate: ParseNullableDouble(cols[6], cols[7]),
                ToolYield: ParseNullableDouble(cols[8], cols[9]),
                DelegationOverhead: ParseNullableDouble(cols[10], cols[11]),
                FieldCoverage: ParseNullableDouble(cols[12], cols[13]),
                Measurability: measurability,
                Payload: payload));
        }

        return rows;
    }

    private IReadOnlyList<StoredProblemRow> QueryStoredProblems(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "id, " +
            "COALESCE(hex(CAST(span_id AS BLOB)), ''), " +
            "hex(CAST(severity AS BLOB)), " +
            "hex(CAST(code AS BLOB)), " +
            "hex(CAST(message AS BLOB)), " +
            "typeof(location), COALESCE(hex(CAST(location AS BLOB)), ''), " +
            "typeof(session_id), COALESCE(hex(CAST(session_id AS BLOB)), ''), " +
            "typeof(harness), COALESCE(hex(CAST(harness AS BLOB)), ''), " +
            "typeof(timestamp), COALESCE(hex(CAST(timestamp AS BLOB)), ''), " +
            "typeof(stack), COALESCE(hex(CAST(stack AS BLOB)), '') " +
            $"FROM problems {suffixSql};");

        List<StoredProblemRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 15)
                continue;

            rows.Add(new StoredProblemRow(
                Id: int.Parse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture),
                SpanId: string.IsNullOrEmpty(cols[1]) ? string.Empty : DecodeHexText(cols[1]),
                Severity: DecodeHexText(cols[2]),
                Code: DecodeHexText(cols[3]),
                Message: DecodeHexText(cols[4]),
                Location: cols[5] == "null" ? null : DecodeHexText(cols[6]),
                SessionId: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                Harness: cols[9] == "null" ? null : DecodeHexText(cols[10]),
                Timestamp: cols[11] == "null" ? null : DecodeHexText(cols[12]),
                Stack: cols[13] == "null" ? null : DecodeHexText(cols[14])));
        }

        return rows;
    }

    private static string NullableDoubleSql(double? value)
    {
        return value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString("G17", CultureInfo.InvariantCulture)
            : "NULL";
    }

    private static double? ParseNullableDouble(string typeCol, string valCol)
    {
        if (string.Equals(typeCol, "null", StringComparison.Ordinal))
            return null;

        return double.TryParse(valCol, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
            ? parsed
            : null;
    }

    private static JsonElement? ParseJsonOrNull(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Persists a batch of diagnostic problem rows in one transaction.</summary>
    public void RecordProblems(IReadOnlyList<SpanProblemRow> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (problems.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (SpanProblemRow problem in problems)
            {
                ArgumentNullException.ThrowIfNull(problem);
                AppendProblemUpsertSql(script, problem);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>Loads one <see cref="SourceCheckpoint"/> by harness and source key, or <see langword="null"/> if absent.</summary>
    public SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        IReadOnlyList<SourceCheckpoint> rows = QuerySourceCheckpoints(
            $"WHERE harness_id = {TextExpr(harnessId)} AND source_key = {TextExpr(sourceKey)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Lists persisted <see cref="SourceCheckpoint"/> entries, optionally filtered to one harness.</summary>
    public IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null)
    {
        string whereClause = string.IsNullOrWhiteSpace(harnessId)
            ? "ORDER BY harness_id, source_key"
            : $"WHERE harness_id = {TextExpr(harnessId)} ORDER BY source_key";
        return QuerySourceCheckpoints(whereClause);
    }

    /// <summary>Upserts one <see cref="SourceCheckpoint"/> row into <c>source_checkpoint</c>.</summary>
    public void UpsertSourceCheckpoint(SourceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            AppendSourceCheckpointUpsertSql(script, checkpoint);
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>Lists persisted <see cref="RecordProvenanceRow"/> entries for one source unit.</summary>
    public IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(span_id AS BLOB)), " +
            "hex(CAST(harness_id AS BLOB)), " +
            "hex(CAST(source_key AS BLOB)), " +
            "typeof(native_session_id), COALESCE(hex(CAST(native_session_id AS BLOB)), ''), " +
            "typeof(native_record_id), COALESCE(hex(CAST(native_record_id AS BLOB)), ''), " +
            "hex(CAST(source_revision AS BLOB)), " +
            "hex(CAST(parser_version AS BLOB)), " +
            "hex(CAST(imported_at_utc AS BLOB)), " +
            "hex(CAST(location_token AS BLOB)) " +
            $"FROM record_provenance WHERE harness_id = {TextExpr(harnessId)} AND source_key = {TextExpr(sourceKey)} ORDER BY span_id;");

        List<RecordProvenanceRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 11)
                continue;

            rows.Add(new RecordProvenanceRow(
                SpanId: DecodeHexText(cols[0]),
                HarnessId: DecodeHexText(cols[1]),
                SourceKey: DecodeHexText(cols[2]),
                NativeSessionId: cols[3] == "null" ? null : DecodeHexText(cols[4]),
                NativeRecordId: cols[5] == "null" ? null : DecodeHexText(cols[6]),
                SourceRevision: DecodeHexText(cols[7]),
                ParserVersion: DecodeHexText(cols[8]),
                ImportedAtUtc: DecodeHexText(cols[9]),
                LocationToken: DecodeHexText(cols[10])));
        }

        return rows;
    }

    /// <summary>Loads all non-<c>synth:</c> (OTLP-ingested) records from <c>records</c> for cross-path deduplication.</summary>
    public IReadOnlyList<CanonicalRecord> ListOtlpRecords() =>
        QueryRecords("WHERE span_id NOT LIKE 'synth:%' ORDER BY timestamp");

    /// <summary>
    /// Atomically commits one checkpointed source unit: deletes any superseded digest-backed
    /// copies owned by <paramref name="checkpoint"/>, upserts <paramref name="records"/>,
    /// <paramref name="provenances"/>, optional <paramref name="problems"/>, and the
    /// <paramref name="checkpoint"/> row itself inside a single SQLite transaction.
    /// </summary>
    public void CommitSourceUnit(
        SourceCheckpoint checkpoint,
        IReadOnlyList<CanonicalRecord> records,
        IReadOnlyList<RecordProvenanceRow> provenances,
        IReadOnlyList<SpanProblemRow>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(provenances);

        lock (writeGate)
        {
            List<string> doomedSpanIds = FindSupersededDigestSpanIds(checkpoint, records);

            HashSet<string> touchedKeys = new(StringComparer.Ordinal);
            bool hasUnkeyed = doomedSpanIds.Count > 0;
            foreach (CanonicalRecord record in records)
            {
                string? key = !string.IsNullOrEmpty(record.SessionId)
                    ? record.SessionId
                    : !string.IsNullOrEmpty(record.TraceId)
                        ? record.TraceId
                        : null;
                if (key is null)
                    hasUnkeyed = true;
                else
                    touchedKeys.Add(key);
            }

            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");

            if (doomedSpanIds.Count > 0)
            {
                string doomedIn = string.Join(", ", doomedSpanIds.Select(TextExpr));
                script.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"DELETE FROM record_provenance WHERE span_id IN ({doomedIn}) OR span_id IN (SELECT span_id FROM records WHERE parent_span_id IN ({doomedIn}));");
                script.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"DELETE FROM records WHERE span_id IN ({doomedIn}) OR parent_span_id IN ({doomedIn});");
            }

            foreach (CanonicalRecord record in records)
            {
                ArgumentNullException.ThrowIfNull(record);
                AppendRecordUpsertSql(script, record);
            }

            foreach (RecordProvenanceRow prov in provenances)
            {
                ArgumentNullException.ThrowIfNull(prov);
                script.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"INSERT OR REPLACE INTO record_provenance (span_id, harness_id, source_key, native_session_id, native_record_id, source_revision, parser_version, imported_at_utc, location_token) VALUES ({TextExpr(prov.SpanId)}, {TextExpr(prov.HarnessId)}, {TextExpr(prov.SourceKey)}, {NullableTextExpr(prov.NativeSessionId)}, {NullableTextExpr(prov.NativeRecordId)}, {TextExpr(prov.SourceRevision)}, {TextExpr(prov.ParserVersion)}, {TextExpr(prov.ImportedAtUtc)}, {TextExpr(prov.LocationToken)});");
            }

            if (problems is not null)
            {
                foreach (SpanProblemRow problem in problems)
                {
                    ArgumentNullException.ThrowIfNull(problem);
                    AppendProblemUpsertSql(script, problem);
                }
            }

            AppendSourceCheckpointUpsertSql(script, checkpoint);

            if (records.Count > 0 || doomedSpanIds.Count > 0)
            {
                AppendDirtySessionUpdate(script, hasUnkeyed ? null : touchedKeys);
                AppendBumpRecordsWriteMarker(script);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>
    /// Clears <c>content_json</c> and <c>parts_json</c> in place on records older than
    /// <paramref name="cutoffUtc"/> while preserving rows and compressed <c>raw</c> payloads,
    /// returning the count of affected records.
    /// </summary>
    public int PurgeContentBefore(string cutoffUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cutoffUtc);

        string wherePredicate =
            $"datetime(timestamp) < datetime({TextExpr(cutoffUtc)}) " +
            "AND ((content_json IS NOT NULL AND content_json <> '{}') OR (parts_json IS NOT NULL AND parts_json <> '[]'))";

        lock (writeGate)
        {
            int pending = ExecuteScalarInt($"SELECT COUNT(*) FROM records WHERE {wherePredicate};");
            if (pending == 0)
                return 0;

            StringBuilder script = new();
            script.AppendLine(".mode tabs");
            script.AppendLine(".headers off");
            script.AppendLine("BEGIN IMMEDIATE;");
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"UPDATE records SET content_json = '{{}}', parts_json = NULL WHERE {wherePredicate};");
            script.AppendLine("SELECT changes();");
            AppendDirtySessionUpdate(script, dirtyKeys: null);
            AppendBumpRecordsWriteMarker(script);
            script.AppendLine("COMMIT;");

            string output = ExecuteSqlite(script.ToString());
            foreach (string line in Lines(output))
            {
                if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int changed))
                    return changed;
            }

            return pending;
        }
    }

    /// <summary>Inserts a new <c>running</c> row into <c>refresh_run</c> and returns <paramref name="id"/>.</summary>
    public string StartRefreshRun(
        string id,
        string trigger,
        string startedAtUtc,
        int pid,
        int? historyWeeks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);
        ArgumentException.ThrowIfNullOrWhiteSpace(startedAtUtc);

        int? normalizedWeeks = CanonSchema.NormalizeHistoryWeeks(historyWeeks);
        string weeksSql = normalizedWeeks.HasValue
            ? normalizedWeeks.Value.ToString(CultureInfo.InvariantCulture)
            : "NULL";

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"INSERT INTO refresh_run (id, started_at, completed_at, status, pid, trigger, summary, history_weeks) VALUES ({TextExpr(id)}, {TextExpr(startedAtUtc)}, NULL, 'running', {pid.ToString(CultureInfo.InvariantCulture)}, {TextExpr(trigger)}, NULL, {weeksSql});");
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }

        return id;
    }

    /// <summary>Closes an existing <c>refresh_run</c> row with status, completion timestamp, and summary.</summary>
    public void CompleteRefreshRun(
        string id,
        string status,
        string completedAtUtc,
        string? summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedAtUtc);

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"UPDATE refresh_run SET completed_at = {TextExpr(completedAtUtc)}, status = {TextExpr(status)}, summary = {NullableTextExpr(summary)} WHERE id = {TextExpr(id)};");
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>Marks interrupted or stale <c>running</c> rows in <c>refresh_run</c> as <c>failure</c>.</summary>
    public int ReconcileDeadRefreshRuns(string completedAtUtc, long maxAgeMs = 3_600_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(completedAtUtc);

        IReadOnlyList<RefreshRunRow> activeRuns = QueryRefreshRuns("WHERE status = 'running' ORDER BY started_at DESC");
        if (activeRuns.Count == 0)
            return 0;

        bool hasNow = DateTimeOffset.TryParse(completedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset nowDt);
        List<(string Id, string Summary)> doomed = [];

        foreach (RefreshRunRow row in activeRuns)
        {
            bool isAlive = IsPidAlive(row.Pid);
            long ageMs = 0;
            if (hasNow && DateTimeOffset.TryParse(row.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset startedDt))
                ageMs = (long)(nowDt - startedDt).TotalMilliseconds;

            bool isStale = ageMs > maxAgeMs;
            if (isAlive && !isStale)
                continue;

            string summary = !isAlive
                ? string.Create(CultureInfo.InvariantCulture, $"Refresh process {row.Pid} is no longer running (dead process); marked failed during reconciliation.")
                : string.Create(CultureInfo.InvariantCulture, $"Refresh process {row.Pid} exceeded maximum run duration ({Math.Round(ageMs / 1000.0)}s); marked failed during reconciliation.");
            doomed.Add((row.Id, summary));
        }

        if (doomed.Count == 0)
            return 0;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach ((string id, string summary) in doomed)
            {
                script.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"UPDATE refresh_run SET completed_at = {TextExpr(completedAtUtc)}, status = 'failure', summary = {TextExpr(summary)} WHERE id = {TextExpr(id)} AND status = 'running';");
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }

        return doomed.Count;
    }

    /// <summary>Lists recent entries from <c>refresh_run</c> newest-first.</summary>
    public IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return QueryRefreshRuns(string.Create(CultureInfo.InvariantCulture, $"ORDER BY started_at DESC LIMIT {limit}"));
    }

    private List<string> FindSupersededDigestSpanIds(
        SourceCheckpoint checkpoint,
        IReadOnlyList<CanonicalRecord> batch)
    {
        HashSet<string> batchIds = new(batch.Select(static r => r.SpanId), StringComparer.Ordinal);
        HashSet<string> timestamps = new(StringComparer.Ordinal);
        HashSet<(string SessionId, string Timestamp, string Name)> batchCalls = [];

        foreach (CanonicalRecord record in batch)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal))
                continue;

            timestamps.Add(record.Timestamp);
            batchCalls.Add((record.SessionId ?? string.Empty, record.Timestamp, record.Name));
        }

        if (timestamps.Count == 0)
            return [];

        string tsIn = string.Join(", ", timestamps.Order(StringComparer.Ordinal).Select(TextExpr));
        IReadOnlyList<CanonicalRecord> candidates = QueryRecords(
            $"WHERE op = 'llm.invoke' AND timestamp IN ({tsIn}) " +
            $"AND span_id IN (SELECT span_id FROM record_provenance WHERE harness_id = {TextExpr(checkpoint.HarnessId)} AND source_key = {TextExpr(checkpoint.SourceKey)})");

        List<string> doomed = [];
        foreach (CanonicalRecord existing in candidates)
        {
            if (batchIds.Contains(existing.SpanId))
                continue;

            if (!batchCalls.Contains((existing.SessionId ?? string.Empty, existing.Timestamp, existing.Name)))
                continue;

            if (HasRecordDigest(existing.Raw))
                doomed.Add(existing.SpanId);
        }

        return doomed;
    }

    private static bool HasRecordDigest(IReadOnlyDictionary<string, JsonElement>? raw)
    {
        if (raw is null || !raw.TryGetValue("provenance", out JsonElement provEl) || provEl.ValueKind != JsonValueKind.Object)
            return false;

        return provEl.TryGetProperty("recordDigest", out JsonElement digestEl)
            && digestEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(digestEl.GetString());
    }

    private IReadOnlyList<SourceCheckpoint> QuerySourceCheckpoints(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(harness_id AS BLOB)), " +
            "hex(CAST(source_key AS BLOB)), " +
            "hex(CAST(provider_id AS BLOB)), " +
            "hex(CAST(parser_id AS BLOB)), " +
            "hex(CAST(parser_contract_version AS BLOB)), " +
            "hex(CAST(format AS BLOB)), " +
            "hex(CAST(source_root_label AS BLOB)), " +
            "hex(CAST(revision_token AS BLOB)), " +
            "hex(CAST(covered_from_utc AS BLOB)), " +
            "hex(CAST(covered_through_utc AS BLOB)), " +
            "hex(CAST(last_attempt_utc AS BLOB)), " +
            "typeof(last_success_utc), COALESCE(hex(CAST(last_success_utc AS BLOB)), ''), " +
            "hex(CAST(last_status AS BLOB)), " +
            "typeof(last_error_code), COALESCE(hex(CAST(last_error_code AS BLOB)), ''), " +
            "unit_count, " +
            "record_count " +
            $"FROM source_checkpoint {suffixSql};");

        List<SourceCheckpoint> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 18)
                continue;

            rows.Add(new SourceCheckpoint(
                HarnessId: DecodeHexText(cols[0]),
                SourceKey: DecodeHexText(cols[1]),
                ProviderId: DecodeHexText(cols[2]),
                ParserId: DecodeHexText(cols[3]),
                ParserContractVersion: DecodeHexText(cols[4]),
                Format: DecodeHexText(cols[5]),
                SourceRootLabel: DecodeHexText(cols[6]),
                RevisionToken: DecodeHexText(cols[7]),
                CoveredFromUtc: DecodeHexText(cols[8]),
                CoveredThroughUtc: DecodeHexText(cols[9]),
                LastAttemptUtc: DecodeHexText(cols[10]),
                LastSuccessUtc: cols[11] == "null" ? null : DecodeHexText(cols[12]),
                LastStatus: DecodeHexText(cols[13]),
                LastErrorCode: cols[14] == "null" ? null : DecodeHexText(cols[15]),
                UnitCount: int.Parse(cols[16], NumberStyles.Integer, CultureInfo.InvariantCulture),
                RecordCount: int.Parse(cols[17], NumberStyles.Integer, CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private IReadOnlyList<RefreshRunRow> QueryRefreshRuns(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(id AS BLOB)), " +
            "hex(CAST(started_at AS BLOB)), " +
            "typeof(completed_at), COALESCE(hex(CAST(completed_at AS BLOB)), ''), " +
            "hex(CAST(status AS BLOB)), " +
            "pid, " +
            "hex(CAST(trigger AS BLOB)), " +
            "typeof(summary), COALESCE(hex(CAST(summary AS BLOB)), ''), " +
            "typeof(history_weeks), COALESCE(history_weeks, 0) " +
            $"FROM refresh_run {suffixSql};");

        List<RefreshRunRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 11)
                continue;

            int? weeks = cols[9] == "null"
                ? null
                : CanonSchema.NormalizeHistoryWeeks(long.Parse(cols[10], NumberStyles.Integer, CultureInfo.InvariantCulture));

            rows.Add(new RefreshRunRow(
                Id: DecodeHexText(cols[0]),
                StartedAt: DecodeHexText(cols[1]),
                CompletedAt: cols[2] == "null" ? null : DecodeHexText(cols[3]),
                Status: DecodeHexText(cols[4]),
                Pid: int.Parse(cols[5], NumberStyles.Integer, CultureInfo.InvariantCulture),
                Trigger: DecodeHexText(cols[6]),
                Summary: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                HistoryWeeks: weeks));
        }

        return rows;
    }

    private static void AppendSourceCheckpointUpsertSql(StringBuilder script, SourceCheckpoint checkpoint)
    {
        string sql = string.Create(
            CultureInfo.InvariantCulture,
            $"INSERT OR REPLACE INTO source_checkpoint (harness_id, source_key, provider_id, parser_id, parser_contract_version, format, source_root_label, revision_token, covered_from_utc, covered_through_utc, last_attempt_utc, last_success_utc, last_status, last_error_code, unit_count, record_count) VALUES ({TextExpr(checkpoint.HarnessId)}, {TextExpr(checkpoint.SourceKey)}, {TextExpr(checkpoint.ProviderId)}, {TextExpr(checkpoint.ParserId)}, {TextExpr(checkpoint.ParserContractVersion)}, {TextExpr(checkpoint.Format)}, {TextExpr(checkpoint.SourceRootLabel)}, {TextExpr(checkpoint.RevisionToken)}, {TextExpr(checkpoint.CoveredFromUtc)}, {TextExpr(checkpoint.CoveredThroughUtc)}, {TextExpr(checkpoint.LastAttemptUtc)}, {NullableTextExpr(checkpoint.LastSuccessUtc)}, {TextExpr(checkpoint.LastStatus)}, {NullableTextExpr(checkpoint.LastErrorCode)}, {checkpoint.UnitCount.ToString(CultureInfo.InvariantCulture)}, {checkpoint.RecordCount.ToString(CultureInfo.InvariantCulture)});");
        script.AppendLine(sql);
    }

    private static bool IsPidAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
