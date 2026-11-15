using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Domain;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Canonical SQLite store (<c>canon.db</c>) accessed through the system <c>sqlite3</c> CLI, so the
/// store keeps schema v24 and JSON wire parity with the TypeScript implementation while every
/// database call passes through the injected <see cref="ICircuitBreaker"/>.
/// </summary>
public partial class CanonStoreRepository : BaseSqliteRepository, ICanonStoreRepository
{
    private const int BusyTimeoutMilliseconds = 5000;
    private const int BusyAttempts = 3;

    private const string SchemaVersionKey = "schema_version";
    private const string DetectorVersionKey = "detector_version";
    private const string CleanStampMetadataKey = "last_clean_at";
    private const string ProjectionSchemaVersionKey = "projection_schema_version";
    private const string IsoTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly JsonSerializerOptions RecordJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock writeGate = new();
    private bool initialized;

    /// <summary>
    /// Opens or creates the canonical store at <paramref name="databasePath"/> and ensures the
    /// schema is initialized at <see cref="CanonSchema.SchemaVersion"/> before the constructor returns.
    /// </summary>
    /// <param name="databasePath">Path to the <c>canon.db</c> file, created when absent.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    public CanonStoreRepository(string databasePath, ICircuitBreaker circuitBreaker)
        : base(databasePath, circuitBreaker, batchTimeout: TimeSpan.FromSeconds(120), busyTimeoutMilliseconds: BusyTimeoutMilliseconds, busyAttempts: BusyAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string? directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        EnsureInitialized();
    }

    /// <summary>Opens or creates the canonical store the resolved <see cref="DashOptions"/> names.</summary>
    /// <param name="options">Composition options holding an optional explicit database path.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    public CanonStoreRepository(DashOptions options, ICircuitBreaker circuitBreaker)
        : this(CanonDbPath.Resolve(options?.DatabasePath, null, null), circuitBreaker)
    {
    }

    /// <summary>Path to the canonical SQLite database file.</summary>
    string ICanonStoreRepository.DatabasePath => DatabasePath;

    /// <inheritdoc />
    public void EnsureInitialized()
    {
        lock (writeGate)
        {
            if (initialized)
                return;

            Initialize();
            initialized = true;
        }
    }

    /// <summary>Reads a metadata value by key, or <see langword="null"/> when absent.</summary>
    public string? GetMetadata(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            $"SELECT hex(CAST(value AS BLOB)) FROM metadata WHERE key = {TextExpr(key)} LIMIT 1;");

        foreach (string line in Lines(output))
            return DecodeHexText(line);

        return null;
    }

    /// <summary>Inserts or replaces a metadata entry.</summary>
    public void SetMetadata(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        PutMetadataInTransaction(key, value);
    }

    /// <summary>
    /// Writes or deletes a metadata entry only when its current value matches <paramref name="expected"/>,
    /// returning <see langword="true"/> when the update applied or the key was already in the requested state.
    /// </summary>
    public bool CompareAndSwapMetadata(string key, string? expected, string? newValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (writeGate)
        {
            if (expected is null && newValue is null)
            {
                string existing = ExecuteSqlite(
                    ".mode tabs\n" +
                    ".headers off\n" +
                    $"SELECT 1 FROM metadata WHERE key = {TextExpr(key)} LIMIT 1;");
                return !Lines(existing).Any();
            }

            string keyExpr = TextExpr(key);
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            if (expected is null)
            {
                script.AppendLine(
CultureInfo.InvariantCulture,
    $"INSERT INTO metadata (key, value) SELECT {keyExpr}, {TextExpr(newValue!)} " +
                    $"WHERE NOT EXISTS (SELECT 1 FROM metadata WHERE key = {keyExpr});");
            }
            else if (newValue is null)
            {
                script.AppendLine(
CultureInfo.InvariantCulture,
    $"DELETE FROM metadata WHERE key = {keyExpr} AND value = {TextExpr(expected)};");
            }
            else
            {
                script.AppendLine(
CultureInfo.InvariantCulture,
    $"UPDATE metadata SET value = {TextExpr(newValue)} WHERE key = {keyExpr} AND value = {TextExpr(expected)};");
            }

            script.AppendLine(".mode tabs");
            script.AppendLine(".headers off");
            script.AppendLine("SELECT changes();");
            script.AppendLine("COMMIT;");

            string output = ExecuteSqlite(script.ToString());
            foreach (string line in Lines(output))
            {
                if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int changes))
                    return changes == 1;
            }

            return false;
        }
    }

    /// <summary>Returns the monotonic <c>records</c> write sequence, or <c>0</c> when unset.</summary>
    public long RecordsWriteMarker()
    {
        string? raw = GetMetadata(CanonSchema.RecordsWriteMarkerKey);
        if (raw is null)
            return 0;

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value >= 0
            ? value
            : 0;
    }

    /// <summary>Increments the <c>records</c> write sequence atomically inside a write transaction.</summary>
    public void BumpRecordsWriteMarker()
    {
        string keyExpr = TextExpr(CanonSchema.RecordsWriteMarkerKey);
        lock (writeGate)
        {
            ExecuteSqlite(
                "BEGIN IMMEDIATE;\n" +
                "INSERT INTO metadata (key, value) VALUES (" + keyExpr + ", '1') " +
                "ON CONFLICT(key) DO UPDATE SET value = CAST((COALESCE(CAST(metadata.value AS INTEGER), 0) + 1) AS TEXT);\n" +
                "COMMIT;");
        }
    }

    /// <summary>Reads the stamped detector version from metadata, or <see langword="null"/> when absent.</summary>
    public int? GetDetectorVersion()
    {
        string? raw = GetMetadata(DetectorVersionKey);
        return raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)
            ? version
            : null;
    }

    /// <summary>True when the store's stamped detector version matches <see cref="CanonSchema.DetectorVersion"/>.</summary>
    public bool HasCurrentDetectorVersion() => GetDetectorVersion() == CanonSchema.DetectorVersion;

    /// <summary>Lists user table names present in the database, ordered lexicographically.</summary>
    public IReadOnlyList<string> ListTables()
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT hex(CAST(name AS BLOB)) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");

        List<string> tables = [];
        foreach (string line in Lines(output))
            tables.Add(DecodeHexText(line));
        return tables;
    }

    /// <inheritdoc />
    public int CountRecords() => ExecuteScalarInt("SELECT COUNT(*) FROM records;");

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void PutRecord(CanonicalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        PutRecords([record]);
    }

    /// <inheritdoc />
    public void PutRecords(IEnumerable<CanonicalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        List<CanonicalRecord> batch = [.. records];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            HashSet<string> touchedKeys = new(StringComparer.Ordinal);
            bool hasUnkeyed = false;

            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");

            foreach (CanonicalRecord record in batch)
            {
                ArgumentNullException.ThrowIfNull(record);

                string? key = SessionKeyOf(record);
                if (key is null)
                    hasUnkeyed = true;
                else
                    touchedKeys.Add(key);

                AppendRecordUpsertSql(script, record);
            }

            AppendDirtySessionUpdate(script, hasUnkeyed ? null : touchedKeys);
            AppendBumpRecordsWriteMarker(script);
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public CanonicalRecord? GetRecord(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);
        IReadOnlyList<CanonicalRecord> rows = QueryRecords($"WHERE span_id = {TextExpr(spanId)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc />
    public bool HasRecord(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            $"SELECT 1 FROM records WHERE span_id = {TextExpr(spanId)} LIMIT 1;");
        return Lines(output).Any();
    }

    /// <inheritdoc />
    public IReadOnlyList<CanonicalRecord> QueryAllRecords() => QueryRecords("ORDER BY timestamp");

    /// <inheritdoc />
    public IReadOnlyList<CanonicalRecord> ListRecordsBySessionKey(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        return QueryRecords($"WHERE COALESCE(session_id, trace_id) = {TextExpr(sessionKey)} ORDER BY timestamp");
    }

    /// <inheritdoc />
    public void SetCosts(IReadOnlyDictionary<string, CostBlock> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
            return;

        lock (writeGate)
        {
            Dictionary<string, string?> keyBySpan = new(StringComparer.Ordinal);
            List<string> spanIds = [.. updates.Keys];
            const int batchSize = 256;
            for (int offset = 0; offset < spanIds.Count; offset += batchSize)
            {
                int count = Math.Min(batchSize, spanIds.Count - offset);
                string inList = string.Join(", ", Enumerable.Range(offset, count).Select(i => TextExpr(spanIds[i])));
                string lookup = ExecuteSqlite(
                    ".mode tabs\n" +
                    ".headers off\n" +
                    "SELECT hex(CAST(span_id AS BLOB)), typeof(COALESCE(session_id, trace_id)), COALESCE(hex(CAST(COALESCE(session_id, trace_id) AS BLOB)), '') " +
                    $"FROM records WHERE span_id IN ({inList});");

                foreach (string line in TabLines(lookup))
                {
                    string[] cols = line.Split('\t', StringSplitOptions.None);
                    if (cols.Length < 3)
                        continue;

                    string foundSpanId = DecodeHexText(cols[0]);
                    string? foundKey = string.Equals(cols[1], "null", StringComparison.Ordinal) ? null : DecodeHexText(cols[2]);
                    keyBySpan[foundSpanId] = string.IsNullOrEmpty(foundKey) ? null : foundKey;
                }
            }

            HashSet<string> touchedKeys = new(StringComparer.Ordinal);
            bool hasUnkeyed = false;
            foreach (string spanId in spanIds)
            {
                if (!keyBySpan.TryGetValue(spanId, out string? key) || string.IsNullOrEmpty(key))
                    hasUnkeyed = true;
                else
                    touchedKeys.Add(key);
            }

            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (KeyValuePair<string, CostBlock> entry in updates)
            {
                ArgumentNullException.ThrowIfNull(entry.Value);
                string costJson = JsonSerializer.Serialize(entry.Value, RecordJsonOptions);
                string sql = $"UPDATE records SET cost_json = {TextExpr(costJson)} WHERE span_id = {TextExpr(entry.Key)};";
                script.AppendLine(sql);
            }

            AppendDirtySessionUpdate(script, hasUnkeyed ? null : touchedKeys);
            AppendBumpRecordsWriteMarker(script);
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public void QuarantineAndDelete(QuarantineRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string namespacesJson = JsonSerializer.Serialize(row.ObservedNamespaces, RecordJsonOptions);

        lock (writeGate)
        {
            string spanExpr = TextExpr(row.SpanId);
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            script.AppendLine(
                "INSERT OR REPLACE INTO quarantine (span_id, namespaces, reason, source, name, timestamp, raw_payload, harness) VALUES (" +
                $"{spanExpr}, {TextExpr(namespacesJson)}, {TextExpr(row.Reason)}, " +
                $"{NullableTextExpr(row.Source)}, {NullableTextExpr(row.Name)}, {NullableTextExpr(row.Timestamp)}, " +
                $"{NullableTextExpr(row.RawPayload)}, {NullableTextExpr(row.Harness)});");
            AppendLineInvariant(script, $"DELETE FROM records WHERE span_id = {spanExpr};");
            AppendLineInvariant(script, $"DELETE FROM problems WHERE span_id = {spanExpr};");
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<QuarantineRow> GetQuarantined(string? harness = null)
    {
        string where = string.IsNullOrWhiteSpace(harness) ? string.Empty : $" WHERE harness = {TextExpr(harness.Trim())}";
        return QueryQuarantine($"{where} ORDER BY timestamp DESC, span_id ASC");
    }

    /// <inheritdoc />
    public void RecordProblem(SpanProblemRow problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        RecordProblems([problem]);
    }

    /// <inheritdoc />
    public void RecordProblems(IEnumerable<SpanProblemRow> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        List<SpanProblemRow> batch = [.. problems];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (SpanProblemRow problem in batch)
            {
                ArgumentNullException.ThrowIfNull(problem);
                AppendProblemUpsertSql(script, problem);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SpanProblemRow> GetProblems(string? spanId = null)
    {
        string whereClause = spanId is null ? string.Empty : $" WHERE span_id = {TextExpr(spanId)}";
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(span_id AS BLOB)), " +
            "hex(CAST(severity AS BLOB)), " +
            "hex(CAST(code AS BLOB)), " +
            "hex(CAST(message AS BLOB)), " +
            "typeof(stack), COALESCE(hex(CAST(stack AS BLOB)), ''), " +
            "typeof(location), COALESCE(hex(CAST(location AS BLOB)), ''), " +
            "typeof(session_id), COALESCE(hex(CAST(session_id AS BLOB)), ''), " +
            "typeof(harness), COALESCE(hex(CAST(harness AS BLOB)), ''), " +
            "typeof(timestamp), COALESCE(hex(CAST(timestamp AS BLOB)), '') " +
            $"FROM problems{whereClause} ORDER BY id;");

        List<SpanProblemRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 14)
                continue;

            rows.Add(new SpanProblemRow(
                SpanId: DecodeHexText(cols[0]),
                Severity: DecodeHexText(cols[1]),
                Code: DecodeHexText(cols[2]),
                Message: DecodeHexText(cols[3]),
                Stack: cols[4] == "null" ? null : DecodeHexText(cols[5]),
                Location: cols[6] == "null" ? null : DecodeHexText(cols[7]),
                SessionId: cols[8] == "null" ? null : DecodeHexText(cols[9]),
                Harness: cols[10] == "null" ? null : DecodeHexText(cols[11]),
                Timestamp: cols[12] == "null" ? null : DecodeHexText(cols[13])));
        }

        return rows;
    }

    /// <inheritdoc />
    public void RecordIngest(string source, int count, string timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);
        string normalizedSource = CanonSchema.NormalizeIngestSource(source);

        lock (writeGate)
        {
            ExecuteSqlite(
                "BEGIN IMMEDIATE;\n" +
                $"INSERT INTO ingest_log (source, count, timestamp) VALUES ({TextExpr(normalizedSource)}, {count.ToString(CultureInfo.InvariantCulture)}, {TextExpr(timestamp)});\n" +
                "COMMIT;");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IngestLogEntry> GetIngestLog(string? source = null)
    {
        string where = string.IsNullOrWhiteSpace(source) ? string.Empty : $" WHERE source = {TextExpr(CanonSchema.NormalizeIngestSource(source))}";
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            $"SELECT id, hex(CAST(source AS BLOB)), count, hex(CAST(timestamp AS BLOB)) FROM ingest_log{where} ORDER BY id;");

        List<IngestLogEntry> entries = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 4)
                continue;

            entries.Add(new IngestLogEntry(
                Id: int.Parse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture),
                Source: DecodeHexText(cols[1]),
                Count: int.Parse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture),
                Timestamp: DecodeHexText(cols[3])));
        }

        return entries;
    }

    /// <inheritdoc />
    public long? GetCachedTokenCount(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            $"SELECT count FROM token_cache WHERE hash = {TextExpr(hash)} LIMIT 1;");

        foreach (string line in Lines(output))
        {
            if (long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long count))
                return count;
        }

        return null;
    }

    /// <inheritdoc />
    public void PutCachedTokenCounts(IReadOnlyDictionary<string, long> counts, string model)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (counts.Count == 0)
            return;

        StringBuilder script = new();
        script.AppendLine("BEGIN IMMEDIATE;");
        string modelExpr = TextExpr(model);
        foreach (KeyValuePair<string, long> entry in counts)
        {
            string sql = $"INSERT OR IGNORE INTO token_cache (hash, count, model) VALUES ({TextExpr(entry.Key)}, {entry.Value.ToString(CultureInfo.InvariantCulture)}, {modelExpr});";
            script.AppendLine(sql);
        }

        script.AppendLine("COMMIT;");
        lock (writeGate)
            ExecuteSqlite(script.ToString());
    }

    /// <inheritdoc />
    public void PutSession(SessionSummaryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Harness);

        SplitSessionPayload split = SplitSession(row);
        string sessionIdExpr = TextExpr(row.SessionId);

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            script.AppendLine(CanonSchema.SessionTurnTableSql);

            string problemsSql = split.ProblemsCount.HasValue
                ? split.ProblemsCount.Value.ToString(CultureInfo.InvariantCulture)
                : "NULL";
            string upsertSql =
                "INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, " +
                "summary_json, problems_count, context_limit_source, payload) VALUES (" +
                $"{sessionIdExpr}, {TextExpr(row.Harness)}, {NullableTextExpr(row.Label)}, {(row.IsSubagent ? 1 : 0)}, " +
                $"{NullableTextExpr(row.ParentSession)}, {NullableTextExpr(row.AgentName)}, {NullableTextExpr(row.Repo)}, " +
                $"{NullableTextExpr(row.Branch)}, {NullableTextExpr(row.Started)}, {NullableTextExpr(row.Ended)}, " +
                $"{NullableTextExpr(split.SummaryJson)}, {problemsSql}, {NullableTextExpr(split.ContextLimitSource)}, {TextExpr(split.PayloadJson)}) " +
                "ON CONFLICT(session_id) DO UPDATE SET harness = excluded.harness, label = excluded.label, is_subagent = excluded.is_subagent, " +
                "parent_session = excluded.parent_session, agent_name = excluded.agent_name, repo = excluded.repo, branch = excluded.branch, " +
                "started = excluded.started, ended = excluded.ended, summary_json = excluded.summary_json, problems_count = excluded.problems_count, " +
                "context_limit_source = excluded.context_limit_source, payload = excluded.payload;";
            script.AppendLine(upsertSql);

            AppendLineInvariant(script, $"DELETE FROM session_turn WHERE session_id = {sessionIdExpr};");

            foreach ((int turnIndex, string turnJson) in split.Turns)
            {
                byte[]? compressed = CanonCompressedColumn.CompressIfSmaller(turnJson);
                string turnExpr = compressed is not null ? BlobExpr(compressed) : TextExpr(turnJson);
                string insertTurnSql = $"INSERT INTO session_turn (session_id, turn_index, turn_json) VALUES ({sessionIdExpr}, {turnIndex.ToString(CultureInfo.InvariantCulture)}, {turnExpr});";
                script.AppendLine(insertTurnSql);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public void PutSessions(IEnumerable<SessionSummaryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (SessionSummaryRow row in rows)
            PutSession(row);
    }

    /// <inheritdoc />
    public SessionSummaryRow? GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        IReadOnlyList<SessionSummaryRow> rows = QuerySessions($"WHERE session_id = {TextExpr(sessionId)} LIMIT 1", sessionId);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionSummaryRow> ListSessions(string? harness = null)
    {
        if (string.IsNullOrWhiteSpace(harness))
            return QuerySessions("ORDER BY started DESC, session_id ASC", singleSessionId: null);

        string normalized = CanonPricing.NormalizeHarnessName(harness);
        return QuerySessions($"WHERE harness = {TextExpr(normalized)} ORDER BY started DESC, session_id ASC", singleSessionId: null);
    }

    /// <inheritdoc />
    public void DeleteSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        DeleteSessions([sessionId]);
    }

    /// <inheritdoc />
    public void DeleteSessions(IEnumerable<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        List<string> ids = [.. sessionIds];
        if (ids.Count == 0)
            return;

        lock (writeGate)
        {
            bool hasTurnTable = HasSessionTurnTable();
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (string id in ids)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(id);
                string idExpr = TextExpr(id);
                if (hasTurnTable)
                    AppendLineInvariant(script, $"DELETE FROM session_turn WHERE session_id = {idExpr};");

                AppendLineInvariant(script, $"DELETE FROM session WHERE session_id = {idExpr};");
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(int TurnIndex, string TurnJson)> GetSessionTurns(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!HasSessionTurnTable())
            return [];

        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT turn_index, typeof(turn_json), hex(CASE WHEN typeof(turn_json) = 'blob' THEN turn_json ELSE CAST(turn_json AS BLOB) END) " +
            $"FROM session_turn WHERE session_id = {TextExpr(sessionId)} ORDER BY turn_index;");

        List<(int TurnIndex, string TurnJson)> turns = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 3)
                continue;

            string turnJson = string.Equals(cols[1], "blob", StringComparison.Ordinal)
                ? CanonCompressedColumn.Decompress(Convert.FromHexString(cols[2]))
                : DecodeHexText(cols[2]);

            turns.Add((int.Parse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture), turnJson));
        }

        return turns;
    }

    /// <inheritdoc />
    public DirtyProjectionState GetDirtyProjectionState()
    {
        string? raw = GetMetadata(CanonSchema.ProjectionDirtySessionsKey);
        if (raw is null)
            return new DirtyProjectionState(0, 0, null, IsWildcard: true);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("fromSeq", out JsonElement fromEl)
                || !root.TryGetProperty("toSeq", out JsonElement toEl)
                || !root.TryGetProperty("keys", out JsonElement keysEl)
                || !fromEl.TryGetInt64(out long fromSeq)
                || !toEl.TryGetInt64(out long toSeq))
            {
                return new DirtyProjectionState(0, 0, null, IsWildcard: true);
            }

            if (keysEl.ValueKind == JsonValueKind.String && string.Equals(keysEl.GetString(), "*", StringComparison.Ordinal))
                return new DirtyProjectionState(fromSeq, toSeq, null, IsWildcard: true);

            if (keysEl.ValueKind == JsonValueKind.Array)
            {
                List<string> keys = [];
                foreach (JsonElement item in keysEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        return new DirtyProjectionState(0, 0, null, IsWildcard: true);

                    string? itemValue = item.GetString();
                    if (itemValue is not null)
                        keys.Add(itemValue);
                }

                return new DirtyProjectionState(fromSeq, toSeq, keys, IsWildcard: false);
            }

            return new DirtyProjectionState(0, 0, null, IsWildcard: true);
        }
        catch (JsonException)
        {
            return new DirtyProjectionState(0, 0, null, IsWildcard: true);
        }
    }

    /// <inheritdoc />
    public void MarkProjectionClean(long writeSeq, string schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(schemaVersion);
        string dirtyJson = JsonSerializer.Serialize(new { fromSeq = writeSeq, toSeq = writeSeq, keys = Array.Empty<string>() });

        lock (writeGate)
        {
            ExecuteSqlite(
                "BEGIN IMMEDIATE;\n" +
                $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CanonSchema.ProjectionDirtySessionsKey)}, {TextExpr(dirtyJson)});\n" +
                $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(ProjectionSchemaVersionKey)}, {TextExpr(schemaVersion)});\n" +
                "COMMIT;");
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetProjectionSessionKeyMap()
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        string? raw = GetMetadata(CanonSchema.ProjectionSessionKeyMapKey);
        if (raw is null)
            return map;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return map;

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    string? id = prop.Value.GetString();
                    if (id is not null)
                        map[prop.Name] = id;
                }
                else if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                            continue;

                        string? id = item.GetString();
                        if (id is not null)
                        {
                            map[prop.Name] = id;
                            break;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            map.Clear();
        }

        return map;
    }

    /// <inheritdoc />
    public void SaveProjectionSessionKeyMap(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        PutMetadataInTransaction(CanonSchema.ProjectionSessionKeyMapKey, JsonSerializer.Serialize(map));
    }

    /// <inheritdoc />
    public void PutFinding(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        PutFindings([finding]);
    }

    /// <inheritdoc />
    public void PutFindings(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        List<Finding> batch = [.. findings];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (Finding finding in batch)
            {
                ArgumentNullException.ThrowIfNull(finding);
                string errorBarJson = finding.ErrorBar is null
                    ? "null"
                    : JsonSerializer.Serialize(finding.ErrorBar, CamelCaseJsonOptions);
                string evidenceLinksJson = JsonSerializer.Serialize(finding.EvidenceLinks, CamelCaseJsonOptions);
                string? payloadJson = BuildFindingPayloadJson(finding);
                string wasteSql = finding.EstimatedWasteTokens.HasValue
                    ? finding.EstimatedWasteTokens.Value.ToString(CultureInfo.InvariantCulture)
                    : "NULL";

                string sql =
                    "INSERT OR REPLACE INTO finding (id, detector_id, title, mechanism, confidence, estimated_waste_tokens, recommendation, " +
                    "error_bar_json, evidence_links_json, outcome_risk_caveat, run_id, session_id, rank_score, payload) VALUES (" +
                    $"{TextExpr(finding.Id)}, {TextExpr(finding.DetectorId)}, {TextExpr(finding.Title)}, {TextExpr(finding.Mechanism)}, " +
                    $"{TextExpr(finding.Confidence)}, {wasteSql}, {TextExpr(finding.Recommendation)}, {TextExpr(errorBarJson)}, " +
                    $"{TextExpr(evidenceLinksJson)}, {TextExpr(finding.OutcomeRiskCaveat)}, {NullableTextExpr(finding.RunId)}, " +
                    $"{NullableTextExpr(finding.SessionId)}, {finding.RankScore.ToString("G17", CultureInfo.InvariantCulture)}, " +
                    $"{NullableTextExpr(payloadJson)});";
                script.AppendLine(sql);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public Finding? GetFinding(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        IReadOnlyList<Finding> rows = QueryFindings($"WHERE id = {TextExpr(id)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<Finding> ListFindings(string? runId = null, string? sessionId = null)
    {
        List<string> conditions = [];
        if (!string.IsNullOrEmpty(runId))
            conditions.Add($"run_id = {TextExpr(runId)}");
        if (!string.IsNullOrEmpty(sessionId))
            conditions.Add($"session_id = {TextExpr(sessionId)}");

        string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) + " " : string.Empty;
        return QueryFindings($"{whereClause}ORDER BY rank_score DESC, id ASC");
    }

    /// <inheritdoc />
    public void DeleteFindingsForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (writeGate)
        {
            ExecuteSqlite(
                "BEGIN IMMEDIATE;\n" +
                $"DELETE FROM finding WHERE session_id = {TextExpr(sessionId)};\n" +
                "COMMIT;");
        }
    }

    /// <inheritdoc />
    public void UpsertRuns(IEnumerable<RunRow> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        List<RunRow> batch = [.. runs];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (RunRow run in batch)
            {
                ArgumentNullException.ThrowIfNull(run);
                string? payloadJson = run.Payload.HasValue ? run.Payload.Value.GetRawText() : null;
                string sql =
                    "INSERT OR REPLACE INTO run (run_id, harness, label, grouping_basis, grouping_rule, working_directory, started, ended, " +
                    "execution_count, payload) VALUES (" +
                    $"{TextExpr(run.RunId)}, {TextExpr(run.Harness)}, {NullableTextExpr(run.Label)}, {TextExpr(run.GroupingBasis)}, " +
                    $"{NullableTextExpr(run.GroupingRule)}, {NullableTextExpr(run.WorkingDirectory)}, {NullableTextExpr(run.Started)}, " +
                    $"{NullableTextExpr(run.Ended)}, {run.ExecutionCount.ToString(CultureInfo.InvariantCulture)}, {NullableTextExpr(payloadJson)});";
                script.AppendLine(sql);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public RunRow? GetRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        IReadOnlyList<RunRow> rows = QueryRuns($"WHERE run_id = {TextExpr(runId)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<RunRow> ListRuns(string? harness = null)
    {
        if (string.IsNullOrWhiteSpace(harness))
            return QueryRuns("ORDER BY started DESC, run_id ASC");

        return QueryRuns($"WHERE harness = {TextExpr(harness.Trim())} ORDER BY started DESC, run_id ASC");
    }

    /// <inheritdoc />
    public void UpsertExecutions(IEnumerable<ExecutionRow> executions)
    {
        ArgumentNullException.ThrowIfNull(executions);
        List<ExecutionRow> batch = [.. executions];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (ExecutionRow exec in batch)
            {
                ArgumentNullException.ThrowIfNull(exec);
                string linkageJson = exec.ParentLinkage.ValueKind == JsonValueKind.Undefined
                    ? "{}"
                    : exec.ParentLinkage.GetRawText();
                string? payloadJson = exec.Payload.HasValue ? exec.Payload.Value.GetRawText() : null;
                string sql =
                    "INSERT OR REPLACE INTO execution (execution_id, run_id, session_id, parent_execution_id, harness, agent_name, is_root, " +
                    "started, ended, parent_linkage_json, payload) VALUES (" +
                    $"{TextExpr(exec.ExecutionId)}, {TextExpr(exec.RunId)}, {NullableTextExpr(exec.SessionId)}, " +
                    $"{NullableTextExpr(exec.ParentExecutionId)}, {TextExpr(exec.Harness)}, {NullableTextExpr(exec.AgentName)}, " +
                    $"{(exec.IsRoot ? 1 : 0)}, {NullableTextExpr(exec.Started)}, {NullableTextExpr(exec.Ended)}, " +
                    $"{TextExpr(linkageJson)}, {NullableTextExpr(payloadJson)});";
                script.AppendLine(sql);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ExecutionRow> ListExecutionsForRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return QueryExecutions($"WHERE run_id = {TextExpr(runId)} ORDER BY started ASC, execution_id ASC");
    }

    /// <inheritdoc />
    public IReadOnlyList<ExecutionRow> ListExecutionsByHarness(string harness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        return QueryExecutions($"WHERE harness = {TextExpr(harness)} ORDER BY started DESC, execution_id ASC");
    }

    /// <inheritdoc />
    public void UpsertHarnessRollups(IEnumerable<HarnessRollupRow> rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);
        List<HarnessRollupRow> batch = [.. rollups];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (HarnessRollupRow row in batch)
            {
                ArgumentNullException.ThrowIfNull(row);
                script.AppendLine(AppendHarnessRollupSql(row));
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public HarnessRollupRow? GetHarnessRollup(string harness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        IReadOnlyList<HarnessRollupRow> rows = QueryHarnessRollups($"WHERE harness = {TextExpr(harness)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<HarnessRollupRow> ListHarnessRollups() => QueryHarnessRollups("ORDER BY harness ASC");

    /// <inheritdoc />
    public void PutPrediction(PredictionRow prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        string? errorBarJson = prediction.ErrorBar.HasValue ? prediction.ErrorBar.Value.GetRawText() : null;
        string? payloadJson = prediction.Payload.HasValue ? prediction.Payload.Value.GetRawText() : null;
        string observedSql = prediction.ObservedDeltaTokens.HasValue
            ? prediction.ObservedDeltaTokens.Value.ToString(CultureInfo.InvariantCulture)
            : "NULL";

        string sql =
            "INSERT OR REPLACE INTO prediction (id, finding_id, run_id, predicted_waste_tokens, confidence, confidence_tier, error_bar_json, " +
            "created_at, comparison_run_id, observed_delta_tokens, calibration_score, payload) VALUES (" +
            $"{TextExpr(prediction.Id)}, {TextExpr(prediction.FindingId)}, {TextExpr(prediction.RunId)}, " +
            $"{prediction.PredictedWasteTokens.ToString(CultureInfo.InvariantCulture)}, " +
            $"{prediction.Confidence.ToString("G17", CultureInfo.InvariantCulture)}, {NullableTextExpr(prediction.ConfidenceTier)}, " +
            $"{NullableTextExpr(errorBarJson)}, {TextExpr(prediction.CreatedAt)}, {NullableTextExpr(prediction.ComparisonRunId)}, " +
            $"{observedSql}, {NullableDoubleSql(prediction.CalibrationScore)}, {NullableTextExpr(payloadJson)});";

        lock (writeGate)
        {
            ExecuteSqlite(
                "BEGIN IMMEDIATE;\n" +
                sql + "\n" +
                "COMMIT;");
        }
    }

    /// <inheritdoc />
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

            rows.Add(new PredictionRow(
                Id: DecodeHexText(cols[0]),
                FindingId: DecodeHexText(cols[1]),
                RunId: DecodeHexText(cols[2]),
                PredictedWasteTokens: long.Parse(cols[3], NumberStyles.Integer, CultureInfo.InvariantCulture),
                Confidence: double.Parse(cols[4], NumberStyles.Float, CultureInfo.InvariantCulture),
                ConfidenceTier: cols[5] == "null" ? null : DecodeHexText(cols[6]),
                ErrorBar: cols[7] == "null" ? null : ParseJsonOrNull(DecodeHexText(cols[8])),
                CreatedAt: DecodeHexText(cols[9]),
                ComparisonRunId: cols[10] == "null" ? null : DecodeHexText(cols[11]),
                ObservedDeltaTokens: cols[12] == "null" ? null : long.Parse(cols[13], NumberStyles.Integer, CultureInfo.InvariantCulture),
                CalibrationScore: cols[14] == "null" ? null : double.Parse(cols[15], NumberStyles.Float, CultureInfo.InvariantCulture),
                Payload: cols[16] == "null" ? null : ParseJsonOrNull(DecodeHexText(cols[17]))));
        }

        return rows;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredProblemRow> ListStoredProblems(string? harness = null, string? spanId = null)
    {
        List<string> conditions = [];
        if (!string.IsNullOrWhiteSpace(harness))
            conditions.Add($"harness = {TextExpr(harness.Trim())}");
        if (!string.IsNullOrWhiteSpace(spanId))
            conditions.Add($"span_id = {TextExpr(spanId)}");

        string where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)} " : string.Empty;
        return QueryStoredProblems($"{where}ORDER BY id DESC");
    }

    /// <inheritdoc />
    public StoredProblemRow? GetStoredProblem(int id)
    {
        IReadOnlyList<StoredProblemRow> rows = QueryStoredProblems($"WHERE id = {id.ToString(CultureInfo.InvariantCulture)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<QuarantineRow> ListQuarantine(string? harness = null)
    {
        string where = string.IsNullOrWhiteSpace(harness) ? string.Empty : $" WHERE harness = {TextExpr(harness.Trim())}";
        return QueryQuarantine($"{where} ORDER BY timestamp DESC, span_id ASC");
    }

    /// <inheritdoc />
    public QuarantineRow? GetQuarantineItem(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);
        IReadOnlyList<QuarantineRow> rows = QueryQuarantine($"WHERE span_id = {TextExpr(spanId)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc />
    public SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        IReadOnlyList<SourceCheckpoint> rows = QuerySourceCheckpoints(
            $"WHERE harness_id = {TextExpr(harnessId)} AND source_key = {TextExpr(sourceKey)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null)
    {
        string whereClause = string.IsNullOrWhiteSpace(harnessId)
            ? "ORDER BY harness_id, source_key"
            : $"WHERE harness_id = {TextExpr(harnessId)} ORDER BY source_key";
        return QuerySourceCheckpoints(whereClause);
    }

    /// <inheritdoc />
    public void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        List<RecordProvenanceRow> batch = [.. rows];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (RecordProvenanceRow prov in batch)
            {
                ArgumentNullException.ThrowIfNull(prov);
                script.AppendLine(AppendRecordProvenanceSql(prov));
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public void RecordRefreshRun(RefreshRunRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.StartedAt);

        int? normalizedWeeks = CanonSchema.NormalizeHistoryWeeks(row.HistoryWeeks);
        string weeksSql = normalizedWeeks.HasValue
            ? normalizedWeeks.Value.ToString(CultureInfo.InvariantCulture)
            : "NULL";

        string sql =
            "INSERT OR REPLACE INTO refresh_run (id, started_at, completed_at, status, pid, trigger, summary, history_weeks) VALUES (" +
            $"{TextExpr(row.Id)}, {TextExpr(row.StartedAt)}, {NullableTextExpr(row.CompletedAt)}, {TextExpr(row.Status)}, " +
            $"{row.Pid.ToString(CultureInfo.InvariantCulture)}, {TextExpr(row.Trigger)}, {NullableTextExpr(row.Summary)}, {weeksSql});";

        lock (writeGate)
        {
            ExecuteSqlite("BEGIN IMMEDIATE;\n" + sql + "\nCOMMIT;");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return QueryRefreshRuns($"ORDER BY started_at DESC LIMIT {limit.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <inheritdoc />
    public void StorePendingLogs(IEnumerable<OtlpLog> logs, string? harness = null)
    {
        ArgumentNullException.ThrowIfNull(logs);
        List<OtlpLog> batch = [.. logs];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (OtlpLog log in batch)
            {
                ArgumentNullException.ThrowIfNull(log);
                string sql =
                    "INSERT OR REPLACE INTO pending_logs (log_id, payload, harness) VALUES (" +
                    $"{TextExpr(log.LogId)}, {TextExpr(JsonSerializer.Serialize(log, RecordJsonOptions))}, {NullableTextExpr(harness)});";
                script.AppendLine(sql);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OtlpLog> TakePendingLogs(int limit = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return QueryLogs("pending_logs", ejected: true, limit);
    }

    /// <inheritdoc />
    public void StoreQuarantinedLogs(IEnumerable<(OtlpLog Log, string Reason)> logs, string? harness = null)
    {
        ArgumentNullException.ThrowIfNull(logs);
        List<(OtlpLog Log, string Reason)> batch = [.. logs];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach ((OtlpLog log, string reason) in batch)
            {
                ArgumentNullException.ThrowIfNull(log);
                ArgumentNullException.ThrowIfNull(reason);
                string sql =
                    "INSERT OR REPLACE INTO quarantined_logs (log_id, payload, reason, harness) VALUES (" +
                    $"{TextExpr(log.LogId)}, {TextExpr(JsonSerializer.Serialize(log, RecordJsonOptions))}, {TextExpr(reason)}, {NullableTextExpr(harness)});";
                script.AppendLine(sql);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public bool IsLogEnriched(string logId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logId);
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            $"SELECT 1 FROM enriched_logs WHERE log_id = {TextExpr(logId)} LIMIT 1;");
        return Lines(output).Any();
    }

    /// <inheritdoc />
    public void MarkLogsEnriched(IEnumerable<string> logIds)
    {
        ArgumentNullException.ThrowIfNull(logIds);
        List<string> batch = [.. logIds];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (string logId in batch)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(logId);
                AppendLineInvariant(script, $"INSERT OR IGNORE INTO enriched_logs (log_id) VALUES ({TextExpr(logId)});");
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public void StoreMetrics(IEnumerable<OtlpMetric> metrics, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        List<OtlpMetric> batch = [.. metrics];
        if (batch.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (OtlpMetric metric in batch)
            {
                ArgumentNullException.ThrowIfNull(metric);
                AppendMetricPointSqls(script, metric, source);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
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
            AppendLineInvariant(script, $"UPDATE records SET content_json = '{{}}', parts_json = NULL WHERE {wherePredicate};");
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

    /// <inheritdoc />
    public StoreWipeReport WipeAll()
    {
        string nowIso = NowIso();

        lock (writeGate)
        {
            int records = ExecuteScalarInt("SELECT COUNT(*) FROM records;");
            int sessions = ExecuteScalarInt("SELECT COUNT(*) FROM session;");
            bool hasTurnTable = HasSessionTurnTable();
            int turns = hasTurnTable ? ExecuteScalarInt("SELECT COUNT(*) FROM session_turn;") : 0;
            int runs = ExecuteScalarInt("SELECT COUNT(*) FROM run;");
            int executions = ExecuteScalarInt("SELECT COUNT(*) FROM execution;");
            int findings = ExecuteScalarInt("SELECT COUNT(*) FROM finding;");
            int rollups = ExecuteScalarInt("SELECT COUNT(*) FROM harness_rollup;");
            int problems = ExecuteScalarInt("SELECT COUNT(*) FROM problems;");
            int quarantine = ExecuteScalarInt("SELECT COUNT(*) FROM quarantine;");

            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            script.AppendLine("DELETE FROM records;");
            if (hasTurnTable)
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
            AppendLineInvariant(script, $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CleanStampMetadataKey)}, {TextExpr(nowIso)});");
            AppendLineInvariant(script, $"DELETE FROM metadata WHERE key IN ({TextExpr(CanonSchema.ProjectionDirtySessionsKey)}, {TextExpr(CanonSchema.ProjectionSessionKeyMapKey)});");
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

    /// <inheritdoc />
    public StoreWipeReport WipeHarnesses(IEnumerable<string> harnesses)
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

        string nowIso = NowIso();
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
            bool hasTurnTable = HasSessionTurnTable();
            int turns = hasTurnTable
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
            AppendLineInvariant(script, $"DELETE FROM finding WHERE session_id IN ({sessionSubquery}) OR run_id IN ({runSubquery});");
            if (hasTurnTable)
                AppendLineInvariant(script, $"DELETE FROM session_turn WHERE session_id IN ({sessionSubquery});");
            AppendLineInvariant(script, $"DELETE FROM session WHERE {harnessFilter};");
            AppendLineInvariant(script, $"DELETE FROM execution WHERE {harnessFilter};");
            AppendLineInvariant(script, $"DELETE FROM run WHERE {harnessFilter};");
            AppendLineInvariant(script, $"DELETE FROM harness_rollup WHERE {harnessFilter};");
            AppendLineInvariant(script, $"DELETE FROM problems WHERE {harnessFilter};");
            AppendLineInvariant(script, $"DELETE FROM quarantine WHERE {harnessFilter};");
            AppendLineInvariant(script, $"DELETE FROM pending_logs WHERE {harnessFilter};");
            AppendLineInvariant(script, $"DELETE FROM quarantined_logs WHERE {harnessFilter};");
            AppendLineInvariant(script, $"DELETE FROM records WHERE {harnessFilter};");
            AppendLineInvariant(script, $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CleanStampMetadataKey)}, {TextExpr(nowIso)});");
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

    /// <inheritdoc />
    public void Vacuum()
    {
        lock (writeGate)
        {
            ExecuteSqlite(
                "PRAGMA wal_checkpoint(TRUNCATE);\n" +
                "PRAGMA incremental_vacuum;");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetBuiltSessionIds()
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT hex(CAST(session_id AS BLOB)) FROM session;");

        List<string> ids = [];
        foreach (string line in Lines(output))
            ids.Add(DecodeHexText(line));
        return ids;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDistinctSessionKeys()
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT hex(CAST(key AS BLOB)) FROM (\n" +
            "  SELECT COALESCE(session_id, trace_id) AS key,\n" +
            "         MIN(timestamp) AS started\n" +
            "  FROM records\n" +
            "  WHERE COALESCE(session_id, trace_id) IS NOT NULL\n" +
            "  GROUP BY COALESCE(session_id, trace_id)\n" +
            ")\n" +
            "ORDER BY started DESC;");

        List<string> keys = [];
        foreach (string line in Lines(output))
            keys.Add(DecodeHexText(line));
        return keys;
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Key, string Harness)> GetSessionKeyHarnesses()
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT DISTINCT hex(CAST(COALESCE(session_id, trace_id) AS BLOB)), hex(CAST(harness AS BLOB)) " +
            "FROM records WHERE COALESCE(session_id, trace_id) IS NOT NULL;");

        List<(string Key, string Harness)> pairs = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 2)
                continue;

            pairs.Add((DecodeHexText(cols[0]), DecodeHexText(cols[1])));
        }

        return pairs;
    }

    /// <inheritdoc />
    public void SetDetectorVersion(int version = CanonSchema.DetectorVersion) =>
        SetMetadata(DetectorVersionKey, version.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? GetProjectedSessionKeyMap()
    {
        string? raw = GetMetadata(CanonSchema.ProjectionSessionKeyMapKey);
        if (raw is null)
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            Dictionary<string, IReadOnlyList<string>> map = new(StringComparer.Ordinal);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    string? singleId = prop.Value.GetString();
                    if (singleId is not null)
                        map[prop.Name] = [singleId];
                    continue;
                }

                if (prop.Value.ValueKind != JsonValueKind.Array)
                    return null;

                List<string> ids = [];
                foreach (JsonElement item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        return null;

                    string? id = item.GetString();
                    if (id is not null)
                        ids.Add(id);
                }

                map[prop.Name] = ids;
            }

            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void RecordProjectedSessionKeys(
        long seq,
        IReadOnlyDictionary<string, IReadOnlyList<string>> sessionIdsByKey)
    {
        ArgumentNullException.ThrowIfNull(sessionIdsByKey);
        string dirtyJson = JsonSerializer.Serialize(new { fromSeq = seq, toSeq = seq, keys = Array.Empty<string>() });
        string mapJson = JsonSerializer.Serialize(sessionIdsByKey);

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            AppendLineInvariant(
                script,
                $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CanonSchema.ProjectionDirtySessionsKey)}, {TextExpr(dirtyJson)});");
            AppendLineInvariant(
                script,
                $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CanonSchema.ProjectionSessionKeyMapKey)}, {TextExpr(mapJson)});");
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, int> CountRecordsBySource()
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT hex(CAST(source AS BLOB)), COUNT(*) FROM records WHERE source IS NOT NULL AND source != '' GROUP BY source ORDER BY 1;");

        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 2)
                continue;

            string source = DecodeHexText(cols[0]);
            if (int.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                counts[source] = n;
        }

        return counts;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
            $"SELECT typeof(turn_json), hex(CASE WHEN typeof(turn_json) = 'blob' THEN turn_json ELSE CAST(turn_json AS BLOB) END) FROM session_turn WHERE session_id = {TextExpr(sessionId)} ORDER BY turn_index ASC;");

        List<JsonElement> turns = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 2)
                continue;

            string turnJson = string.Equals(cols[0], "blob", StringComparison.Ordinal)
                ? CanonCompressedColumn.Decompress(Convert.FromHexString(cols[1]))
                : DecodeHexText(cols[1]);

            try
            {
                using JsonDocument doc = JsonDocument.Parse(turnJson);
                turns.Add(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // A malformed stored turn fragment is skipped rather than failing the whole read.
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

    /// <inheritdoc />
    public JsonElement? GetSessionTurnPayload(string sessionId, int turnIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (HasSessionTurnTable())
        {
            string output = ExecuteSqlite(
                ".mode tabs\n" +
                ".headers off\n" +
                $"SELECT typeof(turn_json), hex(CASE WHEN typeof(turn_json) = 'blob' THEN turn_json ELSE CAST(turn_json AS BLOB) END) FROM session_turn WHERE session_id = {TextExpr(sessionId)} AND turn_index = {turnIndex.ToString(CultureInfo.InvariantCulture)} LIMIT 1;");

            foreach (string line in TabLines(output))
            {
                string[] cols = line.Split('\t', StringSplitOptions.None);
                if (cols.Length < 2)
                    continue;

                string turnJson = string.Equals(cols[0], "blob", StringComparison.Ordinal)
                    ? CanonCompressedColumn.Decompress(Convert.FromHexString(cols[1]))
                    : DecodeHexText(cols[1]);

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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public int CountQuarantine(string? harness = null)
    {
        string where = string.IsNullOrWhiteSpace(harness) ? string.Empty : $" WHERE harness = {TextExpr(harness.Trim())}";
        return ExecuteScalarInt($"SELECT COUNT(*) FROM quarantine{where};");
    }

    /// <inheritdoc />
    public IReadOnlyList<QuarantineRow> ListQuarantinePage(string? harness = null, int? limit = null, int? offset = null)
    {
        string where = string.IsNullOrWhiteSpace(harness) ? string.Empty : $"WHERE harness = {TextExpr(harness.Trim())} ";
        string limitSql = limit.HasValue
            ? $" LIMIT {Math.Max(1, limit.Value).ToString(CultureInfo.InvariantCulture)} OFFSET {Math.Max(0, offset ?? 0).ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        return QueryQuarantine($"{where}ORDER BY timestamp DESC, span_id ASC{limitSql}");
    }

    /// <inheritdoc />
    public int CountProblems(string? harness = null)
    {
        string where = string.IsNullOrWhiteSpace(harness) ? string.Empty : $" WHERE harness = {TextExpr(harness.Trim())}";
        return ExecuteScalarInt($"SELECT COUNT(*) FROM problems{where};");
    }

    /// <inheritdoc />
    public StoredProblemRow? GetProblemById(int id) => GetStoredProblem(id);

    /// <inheritdoc />
    public IReadOnlyList<StoredProblemRow> ListProblems(string? harness = null, int? limit = null, int? offset = null)
    {
        string where = string.IsNullOrWhiteSpace(harness) ? string.Empty : $"WHERE harness = {TextExpr(harness.Trim())} ";
        string limitSql = limit.HasValue
            ? $" LIMIT {Math.Max(1, limit.Value).ToString(CultureInfo.InvariantCulture)} OFFSET {Math.Max(0, offset ?? 0).ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        return QueryStoredProblems($"{where}ORDER BY id DESC{limitSql}");
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IReadOnlyList<CanonicalRecord> ListOtlpRecords() =>
        QueryRecords("WHERE span_id NOT LIKE 'synth:%' ORDER BY timestamp");

    /// <inheritdoc />
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
                string? key = SessionKeyOf(record);
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
                AppendLineInvariant(
                    script,
                    $"DELETE FROM record_provenance WHERE span_id IN ({doomedIn}) OR span_id IN (SELECT span_id FROM records WHERE parent_span_id IN ({doomedIn}));");
                AppendLineInvariant(
                    script,
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
                script.AppendLine(AppendRecordProvenanceSql(prov));
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

    /// <inheritdoc />
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
            AppendLineInvariant(
                script,
                $"INSERT INTO refresh_run (id, started_at, completed_at, status, pid, trigger, summary, history_weeks) VALUES ({TextExpr(id)}, {TextExpr(startedAtUtc)}, NULL, 'running', {pid.ToString(CultureInfo.InvariantCulture)}, {TextExpr(trigger)}, NULL, {weeksSql});");
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }

        return id;
    }

    /// <inheritdoc />
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
            AppendLineInvariant(
                script,
                $"UPDATE refresh_run SET completed_at = {TextExpr(completedAtUtc)}, status = {TextExpr(status)}, summary = {NullableTextExpr(summary)} WHERE id = {TextExpr(id)};");
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <inheritdoc />
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
                AppendLineInvariant(
                    script,
                    $"UPDATE refresh_run SET completed_at = {TextExpr(completedAtUtc)}, status = 'failure', summary = {TextExpr(summary)} WHERE id = {TextExpr(id)} AND status = 'running';");
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }

        return doomed.Count;
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

    private static string NowIso() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString(IsoTimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Appends one line to a SQL batch using the invariant culture.</summary>
    private static void AppendLineInvariant(StringBuilder script, string line)
    {
        ArgumentNullException.ThrowIfNull(script);
        script.AppendLine(line);
    }

    private static string? SessionKeyOf(CanonicalRecord record) =>
        !string.IsNullOrEmpty(record.SessionId)
            ? record.SessionId
            : !string.IsNullOrEmpty(record.TraceId)
                ? record.TraceId
                : null;

    private void PutMetadataInTransaction(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (writeGate)
        {
            ExecuteSqlite(
                "BEGIN IMMEDIATE;\n" +
                $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(key)}, {TextExpr(value)});\n" +
                "COMMIT;");
        }
    }

    private void Initialize()
    {
        ExecuteSqlite(
            "PRAGMA journal_mode = WAL;\n" +
            "PRAGMA synchronous = NORMAL;\n" +
            $"PRAGMA journal_size_limit = {CanonSchema.WalSizeLimitBytes.ToString(CultureInfo.InvariantCulture)};\n" +
            CanonSchema.MetadataSql);

        string? existing = GetMetadata(SchemaVersionKey);
        if (existing is not null)
        {
            if (!int.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromVersion))
            {
                throw new InvalidOperationException(
                    $"canon store at {DatabasePath} has invalid schema_version '{existing}'.");
            }

            if (fromVersion > CanonSchema.SchemaVersion)
                throw new InvalidOperationException(CanonSchema.SchemaVersionRefusal(DatabasePath, fromVersion));

            if (fromVersion < CanonSchema.SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"canon store at {DatabasePath} is at schema version {fromVersion.ToString(CultureInfo.InvariantCulture)} and requires migration to {CanonSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        StringBuilder script = new();
        script.AppendLine("BEGIN IMMEDIATE;");
        script.AppendLine(CanonSchema.SchemaSql);
        script.AppendLine(CanonSchema.SessionDenormIndexSql);
        if (existing is null)
        {
            AppendLineInvariant(script, $"INSERT INTO metadata (key, value) VALUES ({TextExpr(SchemaVersionKey)}, {TextExpr(CanonSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture))});");
            AppendLineInvariant(script, $"INSERT INTO metadata (key, value) VALUES ({TextExpr(DetectorVersionKey)}, {TextExpr(CanonSchema.DetectorVersion.ToString(CultureInfo.InvariantCulture))});");
        }

        script.AppendLine("ANALYZE session;");
        script.AppendLine("COMMIT;");
        ExecuteSqlite(script.ToString());
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

    private static string AppendHarnessRollupSql(HarnessRollupRow row)
    {
        string measJson = JsonSerializer.Serialize(row.Measurability, RecordJsonOptions);
        string? payloadJson = row.Payload.HasValue ? row.Payload.Value.GetRawText() : null;
        return
            "INSERT OR REPLACE INTO harness_rollup (harness, sample_count, context_pressure_median, context_pressure_p95, cache_hit_rate, " +
            "tool_yield, delegation_overhead, field_coverage, measurability_json, payload) VALUES (" +
            $"{TextExpr(row.Harness)}, {row.SampleCount.ToString(CultureInfo.InvariantCulture)}, {NullableDoubleSql(row.ContextPressureMedian)}, " +
            $"{NullableDoubleSql(row.ContextPressureP95)}, {NullableDoubleSql(row.CacheHitRate)}, {NullableDoubleSql(row.ToolYield)}, " +
            $"{NullableDoubleSql(row.DelegationOverhead)}, {NullableDoubleSql(row.FieldCoverage)}, {TextExpr(measJson)}, {NullableTextExpr(payloadJson)});";
    }

    private static string AppendRecordProvenanceSql(RecordProvenanceRow prov) =>
        "INSERT OR REPLACE INTO record_provenance (span_id, harness_id, source_key, native_session_id, native_record_id, source_revision, " +
        "parser_version, imported_at_utc, location_token) VALUES (" +
        $"{TextExpr(prov.SpanId)}, {TextExpr(prov.HarnessId)}, {TextExpr(prov.SourceKey)}, {NullableTextExpr(prov.NativeSessionId)}, " +
        $"{NullableTextExpr(prov.NativeRecordId)}, {TextExpr(prov.SourceRevision)}, {TextExpr(prov.ParserVersion)}, " +
        $"{TextExpr(prov.ImportedAtUtc)}, {TextExpr(prov.LocationToken)});";

    private static void AppendProblemUpsertSql(StringBuilder script, SpanProblemRow problem)
    {
        string problemKey = BuildProblemKey(problem.SpanId, problem.Code, problem.Location);
        string sql =
            "INSERT INTO problems (span_id, problem_key, severity, code, message, location, session_id, harness, timestamp, stack) VALUES (" +
            $"{TextExpr(problem.SpanId)}, {TextExpr(problemKey)}, {TextExpr(problem.Severity)}, {TextExpr(problem.Code)}, {TextExpr(problem.Message)}, " +
            $"{NullableTextExpr(problem.Location)}, {NullableTextExpr(problem.SessionId)}, {NullableTextExpr(problem.Harness)}, " +
            $"{NullableTextExpr(problem.Timestamp)}, {NullableTextExpr(problem.Stack)}) " +
            "ON CONFLICT(problem_key) DO UPDATE SET span_id = excluded.span_id, severity = excluded.severity, code = excluded.code, " +
            "message = excluded.message, location = excluded.location, session_id = excluded.session_id, harness = excluded.harness, " +
            "timestamp = excluded.timestamp, stack = excluded.stack;";
        script.AppendLine(sql);
    }

    private static void AppendSourceCheckpointUpsertSql(StringBuilder script, SourceCheckpoint checkpoint)
    {
        string sql =
            "INSERT OR REPLACE INTO source_checkpoint (harness_id, source_key, provider_id, parser_id, parser_contract_version, format, " +
            "source_root_label, revision_token, covered_from_utc, covered_through_utc, last_attempt_utc, last_success_utc, last_status, " +
            "last_error_code, unit_count, record_count) VALUES (" +
            $"{TextExpr(checkpoint.HarnessId)}, {TextExpr(checkpoint.SourceKey)}, {TextExpr(checkpoint.ProviderId)}, {TextExpr(checkpoint.ParserId)}, " +
            $"{TextExpr(checkpoint.ParserContractVersion)}, {TextExpr(checkpoint.Format)}, {TextExpr(checkpoint.SourceRootLabel)}, " +
            $"{TextExpr(checkpoint.RevisionToken)}, {TextExpr(checkpoint.CoveredFromUtc)}, {TextExpr(checkpoint.CoveredThroughUtc)}, " +
            $"{TextExpr(checkpoint.LastAttemptUtc)}, {NullableTextExpr(checkpoint.LastSuccessUtc)}, {TextExpr(checkpoint.LastStatus)}, " +
            $"{NullableTextExpr(checkpoint.LastErrorCode)}, {checkpoint.UnitCount.ToString(CultureInfo.InvariantCulture)}, " +
            $"{checkpoint.RecordCount.ToString(CultureInfo.InvariantCulture)});";
        script.AppendLine(sql);
    }

    private static void AppendRecordUpsertSql(StringBuilder script, CanonicalRecord record)
    {
        string tokensJson = JsonSerializer.Serialize(record.Tokens, RecordJsonOptions);
        string contentJson = record.Parts is { Count: > 0 }
            ? "{}"
            : JsonSerializer.Serialize(record.Content, RecordJsonOptions);
        string costJson = JsonSerializer.Serialize(record.Cost, RecordJsonOptions);
        string? measurabilityJson = record.Measurability is null
            ? null
            : JsonSerializer.Serialize(record.Measurability, RecordJsonOptions);

        string partsExpr = "NULL";
        if (record.Parts is not null)
        {
            string partsJson = JsonSerializer.Serialize(record.Parts, RecordJsonOptions);
            byte[]? compressedParts = CanonCompressedColumn.CompressIfSmaller(partsJson);
            partsExpr = compressedParts is not null
                ? BlobExpr(compressedParts)
                : TextExpr(partsJson);
        }

        string rawExpr = "NULL";
        if (record.Raw is not null)
        {
            string rawJson = JsonSerializer.Serialize(record.Raw, RecordJsonOptions);
            rawExpr = BlobExpr(CanonCompressedColumn.CompressRaw(rawJson));
        }

        string sql =
            "INSERT OR REPLACE INTO records (span_id, trace_id, parent_span_id, source, harness, session_id, name, op, kind, timestamp, " +
            "duration_ms, status, tokens_json, content_json, cost_json, measurability_json, parts_json, raw) VALUES (" +
            $"{TextExpr(record.SpanId)}, {NullableTextExpr(record.TraceId)}, {NullableTextExpr(record.ParentSpanId)}, {TextExpr(record.Source)}, " +
            $"{TextExpr(record.Harness)}, {NullableTextExpr(record.SessionId)}, {TextExpr(record.Name)}, {TextExpr(record.Op)}, " +
            $"{TextExpr(record.Kind)}, {TextExpr(record.Timestamp)}, {record.DurationMs.ToString(CultureInfo.InvariantCulture)}, " +
            $"{TextExpr(record.Status)}, {TextExpr(tokensJson)}, {TextExpr(contentJson)}, {TextExpr(costJson)}, " +
            $"{NullableTextExpr(measurabilityJson)}, {partsExpr}, {rawExpr});";
        script.AppendLine(sql);
    }
}
