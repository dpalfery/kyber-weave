using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Dash;

/// <summary>Projected session metadata and payload persisted in <c>session</c> and <c>session_turn</c>.</summary>
public sealed record SessionSummaryRow(
    string SessionId,
    string Harness,
    string? Label,
    bool IsSubagent,
    string? ParentSession,
    string? AgentName,
    string? Repo,
    string? Branch,
    string? Started,
    string? Ended,
    string? SummaryJson,
    int? ProblemsCount,
    string? ContextLimitSource,
    JsonElement Payload,
    IReadOnlyList<JsonElement>? Turns = null);

/// <summary>Incremental dirty-session tracking entry recorded alongside <c>records_write_seq</c>.</summary>
public sealed record DirtyProjectionState(
    long FromSeq,
    long ToSeq,
    IReadOnlyList<string>? Keys,
    bool IsWildcard);

/// <summary>
/// Opens and maintains a KyberDash canonical SQLite store (<c>canon.db</c>) through the system
/// <c>sqlite3</c> CLI so the store avoids the <c>SQLitePCLRaw.lib.e_sqlite3</c> native
/// package (<c>GHSA-2m69-gcr7-jv3q</c>) while maintaining schema and metadata parity with
/// <c>dash/src/canon/store.ts</c>.
/// </summary>
public sealed partial class CanonStore
{
    private const int BusyTimeoutMilliseconds = 5000;
    private const int BusyAttempts = 3;
    private const int SqliteBusyResultCode = 5;
    private const int SqliteLockedResultCode = 6;

    /// <summary>
    /// Bounds one <c>sqlite3</c> batch invocation so a hung child cannot block the host.
    /// </summary>
    private static readonly TimeSpan SqliteBatchTimeout = TimeSpan.FromSeconds(120);

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

    [GeneratedRegex(@"\blocked\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LockedWordRegex();

    [GeneratedRegex(@"\bbusy\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BusyWordRegex();

    [GeneratedRegex(@"SQLITE_(BUSY|LOCKED)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SqliteBusyOrLockedRegex();

    /// <summary>
    /// Opens or creates the canonical store at <paramref name="databasePath"/> and ensures the
    /// schema is initialized at <see cref="CanonSchema.SchemaVersion"/>.
    /// </summary>
    public CanonStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);

        string? directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Initialize();
    }

    /// <summary>Full path to the canonical SQLite database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Reads a metadata value by key, or returns <see langword="null"/> if absent.</summary>
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
        ExecuteSqlite(
            "BEGIN IMMEDIATE;\n" +
            $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(key)}, {TextExpr(value)});\n" +
            "COMMIT;");
    }

    /// <summary>
    /// Atomically writes or deletes a metadata entry only when its current value matches
    /// <paramref name="expected"/>, returning <see langword="true"/> if the update applied or
    /// the key was already in the requested state.
    /// </summary>
    public bool CompareAndSetMetadata(string key, string? expected, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (expected is null && value is null)
        {
            string existing = ExecuteSqlite(
                ".mode tabs\n" +
                ".headers off\n" +
                $"SELECT 1 FROM metadata WHERE key = {TextExpr(key)} LIMIT 1;");
            return !Lines(existing).Any();
        }

        StringBuilder script = new();
        script.AppendLine("BEGIN IMMEDIATE;");
        if (expected is null)
        {
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"INSERT INTO metadata (key, value) SELECT {TextExpr(key)}, {TextExpr(value!)} " +
                $"WHERE NOT EXISTS (SELECT 1 FROM metadata WHERE key = {TextExpr(key)});");
        }
        else if (value is null)
        {
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"DELETE FROM metadata WHERE key = {TextExpr(key)} AND value = {TextExpr(expected)};");
        }
        else
        {
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"UPDATE metadata SET value = {TextExpr(value)} " +
                $"WHERE key = {TextExpr(key)} AND value = {TextExpr(expected)};");
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

    /// <summary>Returns the current monotonic <c>records</c> write sequence, or <c>0</c> if unset.</summary>
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
        ExecuteSqlite(
            "BEGIN IMMEDIATE;\n" +
            "INSERT INTO metadata (key, value) VALUES (" + keyExpr + ", '1') " +
            "ON CONFLICT(key) DO UPDATE SET value = CAST((COALESCE(CAST(metadata.value AS INTEGER), 0) + 1) AS TEXT);\n" +
            "COMMIT;");
    }

    /// <summary>Reads the stamped detector version from metadata, or <see langword="null"/> if absent.</summary>
    public int? GetDetectorVersion()
    {
        string? raw = GetMetadata("detector_version");
        return raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)
            ? version
            : null;
    }

    /// <summary>Stamps the detector version into metadata.</summary>
    public void SetDetectorVersion(int version = CanonSchema.DetectorVersion) =>
        SetMetadata("detector_version", version.ToString(CultureInfo.InvariantCulture));

    /// <summary>Returns whether the store's stamped detector version matches <see cref="CanonSchema.DetectorVersion"/>.</summary>
    public bool HasCurrentDetectorVersion() =>
        GetDetectorVersion() == CanonSchema.DetectorVersion;

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

    /// <summary>
    /// Upserts a batch of canonical records in a single transaction, incrementing
    /// <see cref="CanonSchema.RecordsWriteMarkerKey"/> and updating
    /// <see cref="CanonSchema.ProjectionDirtySessionsKey"/> for the affected session keys.
    /// </summary>
    public void UpsertRecords(IReadOnlyList<CanonicalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            return;

        lock (writeGate)
        {
            HashSet<string> touchedKeys = new(StringComparer.Ordinal);
            bool hasUnkeyed = false;

            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");

            foreach (CanonicalRecord record in records)
            {
                ArgumentNullException.ThrowIfNull(record);

                string? key = !string.IsNullOrEmpty(record.SessionId)
                    ? record.SessionId
                    : !string.IsNullOrEmpty(record.TraceId)
                        ? record.TraceId
                        : null;
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

        string upsertSql = string.Create(
            CultureInfo.InvariantCulture,
            $"INSERT OR REPLACE INTO records (span_id, trace_id, parent_span_id, source, harness, session_id, name, op, kind, timestamp, duration_ms, status, tokens_json, content_json, cost_json, measurability_json, parts_json, raw) VALUES ({TextExpr(record.SpanId)}, {NullableTextExpr(record.TraceId)}, {NullableTextExpr(record.ParentSpanId)}, {TextExpr(record.Source)}, {TextExpr(record.Harness)}, {NullableTextExpr(record.SessionId)}, {TextExpr(record.Name)}, {TextExpr(record.Op)}, {TextExpr(record.Kind)}, {TextExpr(record.Timestamp)}, {record.DurationMs.ToString(CultureInfo.InvariantCulture)}, {TextExpr(record.Status)}, {TextExpr(tokensJson)}, {TextExpr(contentJson)}, {TextExpr(costJson)}, {NullableTextExpr(measurabilityJson)}, {partsExpr}, {rawExpr});");
        script.AppendLine(upsertSql);
    }

    /// <summary>Returns whether a record with <paramref name="spanId"/> exists in <c>records</c>.</summary>
    public bool HasRecord(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            $"SELECT 1 FROM records WHERE span_id = {TextExpr(spanId)} LIMIT 1;");
        return Lines(output).Any();
    }

    /// <summary>Loads one canonical record by <paramref name="spanId"/>, or <see langword="null"/> if absent.</summary>
    public CanonicalRecord? GetRecord(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);
        IReadOnlyList<CanonicalRecord> rows = QueryRecords($"WHERE span_id = {TextExpr(spanId)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Loads all canonical records matching <paramref name="sessionKey"/> (<c>COALESCE(session_id, trace_id)</c>), ordered by timestamp.</summary>
    public IReadOnlyList<CanonicalRecord> GetRecordsForSessionKey(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        return QueryRecords($"WHERE COALESCE(session_id, trace_id) = {TextExpr(sessionKey)} ORDER BY timestamp");
    }

    /// <summary>Returns distinct non-null session keys (<c>COALESCE(session_id, trace_id)</c>) ordered newest-started first.</summary>
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

    /// <summary>Returns distinct <c>(sessionKey, harness)</c> pairs present in <c>records</c>.</summary>
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

    /// <summary>
    /// Updates derived <c>cost_json</c> blocks for multiple spans in one transaction,
    /// marking affected session keys dirty and advancing <c>records_write_seq</c>.
    /// </summary>
    public void SetCosts(IReadOnlyDictionary<string, CostBlock> costBySpanId)
    {
        ArgumentNullException.ThrowIfNull(costBySpanId);
        if (costBySpanId.Count == 0)
            return;

        lock (writeGate)
        {
            Dictionary<string, string?> keyBySpan = new(StringComparer.Ordinal);
            List<string> spanIds = [.. costBySpanId.Keys];
            const int batchSize = 256;
            for (int offset = 0; offset < spanIds.Count; offset += batchSize)
            {
                int count = Math.Min(batchSize, spanIds.Count - offset);
                string inList = string.Join(", ", spanIds.GetRange(offset, count).Select(TextExpr));
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
                    string? foundKey = cols[1] == "null" ? null : DecodeHexText(cols[2]);
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
            foreach (KeyValuePair<string, CostBlock> entry in costBySpanId)
            {
                ArgumentNullException.ThrowIfNull(entry.Value);
                string costJson = JsonSerializer.Serialize(entry.Value, RecordJsonOptions);
                string sql = string.Create(
                    CultureInfo.InvariantCulture,
                    $"UPDATE records SET cost_json = {TextExpr(costJson)} WHERE span_id = {TextExpr(entry.Key)};");
                script.AppendLine(sql);
            }

            AppendDirtySessionUpdate(script, hasUnkeyed ? null : touchedKeys);
            AppendBumpRecordsWriteMarker(script);
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>
    /// Moves a span into <c>quarantine</c>, removing any stale rows for that span from <c>records</c>
    /// and <c>problems</c> in one transaction.
    /// </summary>
    public void QuarantineAndDelete(QuarantineRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string namespacesJson = JsonSerializer.Serialize(row.ObservedNamespaces, RecordJsonOptions);

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO quarantine (span_id, namespaces, reason, source, name, timestamp, raw_payload, harness) VALUES (" +
                $"{TextExpr(row.SpanId)}, {TextExpr(namespacesJson)}, {TextExpr(row.Reason)}, " +
                $"{NullableTextExpr(row.Source)}, {NullableTextExpr(row.Name)}, {NullableTextExpr(row.Timestamp)}, " +
                $"{NullableTextExpr(row.RawPayload)}, {NullableTextExpr(row.Harness)});");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM records WHERE span_id = {TextExpr(row.SpanId)};");
            script.AppendLine(CultureInfo.InvariantCulture, $"DELETE FROM problems WHERE span_id = {TextExpr(row.SpanId)};");
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>Loads one quarantined span entry by <paramref name="spanId"/>, or <see langword="null"/> if absent.</summary>
    public QuarantineRow? GetQuarantined(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);
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
            $"FROM quarantine WHERE span_id = {TextExpr(spanId)} LIMIT 1;");

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 13)
                continue;

            List<string> namespaces = JsonSerializer.Deserialize<List<string>>(DecodeHexText(cols[1]), RecordJsonOptions) ?? [];
            return new QuarantineRow(
                SpanId: DecodeHexText(cols[0]),
                ObservedNamespaces: namespaces,
                Reason: DecodeHexText(cols[2]),
                Source: cols[3] == "null" ? null : DecodeHexText(cols[4]),
                Name: cols[5] == "null" ? null : DecodeHexText(cols[6]),
                Timestamp: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                RawPayload: cols[9] == "null" ? null : DecodeHexText(cols[10]),
                Harness: cols[11] == "null" ? null : DecodeHexText(cols[12]));
        }

        return null;
    }

    /// <summary>
    /// Persists a surfaced record or normalization problem, upserting on the canonical
    /// <c>(spanId, code, location)</c> identity key.
    /// </summary>
    public void RecordProblem(SpanProblemRow problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            AppendProblemUpsertSql(script, problem);
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    private static void AppendProblemUpsertSql(StringBuilder script, SpanProblemRow problem)
    {
        string problemKey = BuildProblemKey(problem.SpanId, problem.Code, problem.Location);
        string problemSql = string.Create(
            CultureInfo.InvariantCulture,
            $"INSERT INTO problems (span_id, problem_key, severity, code, message, location, session_id, harness, timestamp, stack) VALUES ({TextExpr(problem.SpanId)}, {TextExpr(problemKey)}, {TextExpr(problem.Severity)}, {TextExpr(problem.Code)}, {TextExpr(problem.Message)}, {NullableTextExpr(problem.Location)}, {NullableTextExpr(problem.SessionId)}, {NullableTextExpr(problem.Harness)}, {NullableTextExpr(problem.Timestamp)}, {NullableTextExpr(problem.Stack)}) ON CONFLICT(problem_key) DO UPDATE SET span_id = excluded.span_id, severity = excluded.severity, code = excluded.code, message = excluded.message, location = excluded.location, session_id = excluded.session_id, harness = excluded.harness, timestamp = excluded.timestamp, stack = excluded.stack;");
        script.AppendLine(problemSql);
    }

    /// <summary>Reads recorded problems ordered by row insertion id, optionally filtered to one <paramref name="spanId"/>.</summary>
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

    /// <summary>Appends an ingest audit entry normalized through <see cref="CanonSchema.NormalizeIngestSource"/>.</summary>
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

    /// <summary>Returns all entries from <c>ingest_log</c> ordered by insertion id.</summary>
    public IReadOnlyList<IngestLogEntry> GetIngestLog()
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT id, hex(CAST(source AS BLOB)), count, hex(CAST(timestamp AS BLOB)) FROM ingest_log ORDER BY id;");

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

    /// <summary>Looks up a cached token count for <paramref name="hash"/> from <c>token_cache</c>.</summary>
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

    /// <summary>Batch-inserts new token-count memo entries into <c>token_cache</c> inside a single transaction.</summary>
    public void PutCachedTokenCounts(IReadOnlyDictionary<string, long> entries, string model)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (entries.Count == 0)
            return;

        StringBuilder script = new();
        script.AppendLine("BEGIN IMMEDIATE;");
        string modelExpr = TextExpr(model);
        foreach (KeyValuePair<string, long> entry in entries)
        {
            string sql = string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT OR IGNORE INTO token_cache (hash, count, model) VALUES ({TextExpr(entry.Key)}, {entry.Value}, {modelExpr});");
            script.AppendLine(sql);
        }

        script.AppendLine("COMMIT;");
        lock (writeGate)
            ExecuteSqlite(script.ToString());
    }

    /// <summary>
    /// Stores or updates a derived session in <c>session</c> and replaces its normalized turn rows
    /// in <c>session_turn</c> without disturbing transcript-sync columns.
    /// </summary>
    public void UpsertSession(SessionSummaryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Harness);

        SplitSessionPayload split = TrySplitSessionTurns(row);

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            script.AppendLine(CanonSchema.SessionTurnTableSql);

            string upsertSql = string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, summary_json, problems_count, context_limit_source, payload) VALUES ({TextExpr(row.SessionId)}, {TextExpr(row.Harness)}, {NullableTextExpr(row.Label)}, {(row.IsSubagent ? 1 : 0)}, {NullableTextExpr(row.ParentSession)}, {NullableTextExpr(row.AgentName)}, {NullableTextExpr(row.Repo)}, {NullableTextExpr(row.Branch)}, {NullableTextExpr(row.Started)}, {NullableTextExpr(row.Ended)}, {NullableTextExpr(split.SummaryJson)}, {(split.ProblemsCount.HasValue ? split.ProblemsCount.Value.ToString(CultureInfo.InvariantCulture) : "NULL")}, {NullableTextExpr(split.ContextLimitSource)}, {TextExpr(split.PayloadJson)}) ON CONFLICT(session_id) DO UPDATE SET harness = excluded.harness, label = excluded.label, is_subagent = excluded.is_subagent, parent_session = excluded.parent_session, agent_name = excluded.agent_name, repo = excluded.repo, branch = excluded.branch, started = excluded.started, ended = excluded.ended, summary_json = excluded.summary_json, problems_count = excluded.problems_count, context_limit_source = excluded.context_limit_source, payload = excluded.payload;");
            script.AppendLine(upsertSql);

            string deleteTurnsSql = string.Create(
                CultureInfo.InvariantCulture,
                $"DELETE FROM session_turn WHERE session_id = {TextExpr(row.SessionId)};");
            script.AppendLine(deleteTurnsSql);

            string sessionIdExpr = TextExpr(row.SessionId);
            foreach ((int turnIndex, string turnJson) in split.Turns)
            {
                byte[]? compressed = CanonCompressedColumn.CompressIfSmaller(turnJson);
                string turnExpr = compressed is not null ? BlobExpr(compressed) : TextExpr(turnJson);
                string insertTurnSql = string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO session_turn (session_id, turn_index, turn_json) VALUES ({sessionIdExpr}, {turnIndex}, {turnExpr});");
                script.AppendLine(insertTurnSql);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>Loads one derived session by <paramref name="sessionId"/>, or <see langword="null"/> if absent.</summary>
    public SessionSummaryRow? GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        IReadOnlyList<SessionSummaryRow> rows = QuerySessions($"WHERE session_id = {TextExpr(sessionId)} LIMIT 1", sessionId);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Lists derived sessions ordered by <c>started DESC, session_id ASC</c>, optionally filtered by harness.</summary>
    public IReadOnlyList<SessionSummaryRow> ListSessions(string? harness = null)
    {
        if (string.IsNullOrWhiteSpace(harness))
            return QuerySessions("ORDER BY started DESC, session_id ASC", singleSessionId: null);

        string normalized = CanonPricing.NormalizeHarnessName(harness);
        return QuerySessions($"WHERE harness = {TextExpr(normalized)} ORDER BY started DESC, session_id ASC", singleSessionId: null);
    }

    /// <summary>Returns all currently stored session identifiers.</summary>
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

    /// <summary>Deletes a derived session and its <c>session_turn</c> rows.</summary>
    public void DeleteSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            if (HasSessionTurnTable())
            {
                string deleteTurnsSql = string.Create(
                    CultureInfo.InvariantCulture,
                    $"DELETE FROM session_turn WHERE session_id = {TextExpr(sessionId)};");
                script.AppendLine(deleteTurnsSql);
            }

            string deleteSessionSql = string.Create(
                CultureInfo.InvariantCulture,
                $"DELETE FROM session WHERE session_id = {TextExpr(sessionId)};");
            script.AppendLine(deleteSessionSql);
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>Inserts or replaces a single diagnostic finding in <c>finding</c>.</summary>
    public void UpsertFinding(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        UpsertFindings([finding]);
    }

    /// <summary>Batch-upserts diagnostic findings into <c>finding</c> inside one transaction.</summary>
    public void UpsertFindings(IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Count == 0)
            return;

        lock (writeGate)
        {
            StringBuilder script = new();
            script.AppendLine("BEGIN IMMEDIATE;");
            foreach (Finding finding in findings)
            {
                ArgumentNullException.ThrowIfNull(finding);
                string errorBarJson = finding.ErrorBar is null
                    ? "null"
                    : JsonSerializer.Serialize(finding.ErrorBar, CamelCaseJsonOptions);
                string evidenceLinksJson = JsonSerializer.Serialize(finding.EvidenceLinks, CamelCaseJsonOptions);
                string? payloadJson = BuildFindingPayloadJson(finding);

                string sql = string.Create(
                    CultureInfo.InvariantCulture,
                    $"INSERT OR REPLACE INTO finding (id, detector_id, title, mechanism, confidence, estimated_waste_tokens, recommendation, error_bar_json, evidence_links_json, outcome_risk_caveat, run_id, session_id, rank_score, payload) VALUES ({TextExpr(finding.Id)}, {TextExpr(finding.DetectorId)}, {TextExpr(finding.Title)}, {TextExpr(finding.Mechanism)}, {TextExpr(finding.Confidence)}, {(finding.EstimatedWasteTokens.HasValue ? finding.EstimatedWasteTokens.Value.ToString(CultureInfo.InvariantCulture) : "NULL")}, {TextExpr(finding.Recommendation)}, {TextExpr(errorBarJson)}, {TextExpr(evidenceLinksJson)}, {TextExpr(finding.OutcomeRiskCaveat)}, {NullableTextExpr(finding.RunId)}, {NullableTextExpr(finding.SessionId)}, {finding.RankScore.ToString("G17", CultureInfo.InvariantCulture)}, {NullableTextExpr(payloadJson)});");
                script.AppendLine(sql);
            }

            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    /// <summary>Loads one finding by <paramref name="id"/>, or <see langword="null"/> if absent.</summary>
    public Finding? GetFinding(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        IReadOnlyList<Finding> rows = QueryFindings($"WHERE id = {TextExpr(id)} LIMIT 1");
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Lists findings ordered by <c>rank_score DESC, id ASC</c>, optionally filtered by session or run.</summary>
    public IReadOnlyList<Finding> ListFindings(string? sessionId = null, string? runId = null)
    {
        List<string> conditions = [];
        if (!string.IsNullOrEmpty(runId))
            conditions.Add($"run_id = {TextExpr(runId)}");
        if (!string.IsNullOrEmpty(sessionId))
            conditions.Add($"session_id = {TextExpr(sessionId)}");

        string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) + " " : string.Empty;
        return QueryFindings($"{whereClause}ORDER BY rank_score DESC, id ASC");
    }

    /// <summary>Deletes a single finding by identifier.</summary>
    public void DeleteFinding(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (writeGate)
        {
            ExecuteSqlite(
                "BEGIN IMMEDIATE;\n" +
                $"DELETE FROM finding WHERE id = {TextExpr(id)};\n" +
                "COMMIT;");
        }
    }

    /// <summary>Deletes all findings scoped to <paramref name="sessionId"/>.</summary>
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

    /// <summary>Reads the dirty-session-keys state persisted under <see cref="CanonSchema.ProjectionDirtySessionsKey"/>.</summary>
    public DirtyProjectionState? GetDirtyProjectionState()
    {
        string? raw = GetMetadata(CanonSchema.ProjectionDirtySessionsKey);
        if (raw is null)
            return null;

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
                return null;
            }

            if (keysEl.ValueKind == JsonValueKind.String && keysEl.GetString() == "*")
                return new DirtyProjectionState(fromSeq, toSeq, null, IsWildcard: true);

            if (keysEl.ValueKind == JsonValueKind.Array)
            {
                List<string> keys = [];
                foreach (JsonElement item in keysEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        return null;

                    string? itemValue = item.GetString();
                    if (itemValue is not null)
                        keys.Add(itemValue);
                }

                return new DirtyProjectionState(fromSeq, toSeq, keys, IsWildcard: false);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the map of session keys to built session IDs from <see cref="CanonSchema.ProjectionSessionKeyMapKey"/>.</summary>
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

    /// <summary>
    /// Clears dirty-session keys at <paramref name="seq"/> and stores the built session-ID map
    /// in one metadata transaction.
    /// </summary>
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
            string dirtySql = string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CanonSchema.ProjectionDirtySessionsKey)}, {TextExpr(dirtyJson)});");
            string mapSql = string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CanonSchema.ProjectionSessionKeyMapKey)}, {TextExpr(mapJson)});");
            script.AppendLine(dirtySql);
            script.AppendLine(mapSql);
            script.AppendLine("COMMIT;");
            ExecuteSqlite(script.ToString());
        }
    }

    private void Initialize()
    {
        ExecuteSqlite(
            "PRAGMA journal_mode = WAL;\n" +
            "PRAGMA synchronous = NORMAL;\n" +
            $"PRAGMA journal_size_limit = {CanonSchema.WalSizeLimitBytes.ToString(CultureInfo.InvariantCulture)};\n" +
            CanonSchema.MetadataSql);

        string? existing = GetMetadata("schema_version");
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
                    $"canon store at {DatabasePath} is at schema version {fromVersion} and requires migration to {CanonSchema.SchemaVersion}.");
            }
        }

        StringBuilder script = new();
        script.AppendLine("BEGIN IMMEDIATE;");
        script.AppendLine(CanonSchema.SchemaSql);
        script.AppendLine(CanonSchema.SessionDenormIndexSql);
        if (existing is null)
        {
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"INSERT INTO metadata (key, value) VALUES ({TextExpr("schema_version")}, {TextExpr(CanonSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture))});");
            script.AppendLine(
                CultureInfo.InvariantCulture,
                $"INSERT INTO metadata (key, value) VALUES ({TextExpr("detector_version")}, {TextExpr(CanonSchema.DetectorVersion.ToString(CultureInfo.InvariantCulture))});");
        }

        script.AppendLine("ANALYZE session;");
        script.AppendLine("COMMIT;");
        ExecuteSqlite(script.ToString());
    }

    private string ExecuteSqlite(string sql)
    {
        string boundedInput = $".timeout {BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}\n{sql}";
        for (int attempt = 1; attempt <= BusyAttempts; attempt++)
        {
            ProcessStartInfo startInfo = CreateStartInfo();
            startInfo.ArgumentList.Add("-batch");
            startInfo.ArgumentList.Add("-bail");
            startInfo.ArgumentList.Add(DatabasePath);

            ProcessResult result;
            try
            {
                result = ProcessRunner.Run(startInfo, boundedInput, SqliteBatchTimeout);
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException("The 'sqlite3' executable is unavailable.", exception);
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    "The canonical store did not answer within the time limit, so the sqlite3 batch was stopped.",
                    exception);
            }

            if (result.ExitCode == 0)
                return result.StandardOutput;

            if (IsBusy(result) && attempt < BusyAttempts)
                continue;

            throw new InvalidOperationException(
                $"sqlite3 exited with code {result.ExitCode}: {(result.StandardOutput + "\n" + result.StandardError).Trim()}");
        }

        throw new InvalidOperationException("The canonical store operation did not complete.");
    }

    private static ProcessStartInfo CreateStartInfo() =>
        new("sqlite3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

    private static string TextExpr(string value) =>
        $"CAST(X'{Convert.ToHexString(Encoding.UTF8.GetBytes(value))}' AS TEXT)";

    private static string NullableTextExpr(string? value) =>
        value is null ? "NULL" : TextExpr(value);

    private static string BlobExpr(byte[] bytes) =>
        $"X'{Convert.ToHexString(bytes)}'";

    private static string DecodeHexText(string hex) =>
        Encoding.UTF8.GetString(Convert.FromHexString(hex));

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> TabLines(string output) =>
        output.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string BuildProblemKey(string spanId, string code, string? location)
    {
        object?[] parts = ["span-problem", spanId, code, location];
        return JsonSerializer.Serialize(parts);
    }

    private static Dictionary<string, string> MergeContent(
        IReadOnlyDictionary<string, string> derived,
        IReadOnlyDictionary<string, string> stored)
    {
        Dictionary<string, string> merged = new(derived.Count + stored.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in derived)
            merged[entry.Key] = entry.Value;
        foreach (KeyValuePair<string, string> entry in stored)
            merged[entry.Key] = entry.Value;
        return merged;
    }

    private void AppendDirtySessionUpdate(StringBuilder script, HashSet<string>? dirtyKeys)
    {
        long prevSeq = RecordsWriteMarker();
        long nextSeq = prevSeq + 1;
        string? existingJson = GetMetadata(CanonSchema.ProjectionDirtySessionsKey);

        long fromSeq = prevSeq;
        bool wildcard = dirtyKeys is null;
        HashSet<string>? mergedKeys = dirtyKeys is null ? null : new HashSet<string>(dirtyKeys, StringComparer.Ordinal);

        if (existingJson is not null)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(existingJson);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("fromSeq", out JsonElement fromEl)
                    && root.TryGetProperty("toSeq", out JsonElement toEl)
                    && root.TryGetProperty("keys", out JsonElement keysEl)
                    && fromEl.TryGetInt64(out long existingFrom)
                    && toEl.TryGetInt64(out long existingTo)
                    && existingTo == prevSeq)
                {
                    fromSeq = existingFrom;
                    if (keysEl.ValueKind == JsonValueKind.String && keysEl.GetString() == "*")
                    {
                        wildcard = true;
                        mergedKeys = null;
                    }
                    else if (!wildcard && keysEl.ValueKind == JsonValueKind.Array && mergedKeys is not null)
                    {
                        foreach (JsonElement item in keysEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                string? itemValue = item.GetString();
                                if (itemValue is not null)
                                    mergedKeys.Add(itemValue);
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore corrupt prior metadata and reset dirty tracking from prevSeq.
            }
        }

        object payload = wildcard
            ? new { fromSeq, toSeq = nextSeq, keys = "*" }
            : new { fromSeq, toSeq = nextSeq, keys = mergedKeys!.Order(StringComparer.Ordinal).ToArray() };
        string serialized = JsonSerializer.Serialize(payload);
        script.AppendLine(
            CultureInfo.InvariantCulture,
            $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CanonSchema.ProjectionDirtySessionsKey)}, {TextExpr(serialized)});");
    }

    private static void AppendBumpRecordsWriteMarker(StringBuilder script)
    {
        string keyExpr = TextExpr(CanonSchema.RecordsWriteMarkerKey);
        string sql = $"INSERT INTO metadata (key, value) VALUES ({keyExpr}, '1') ON CONFLICT(key) DO UPDATE SET value = CAST((COALESCE(CAST(metadata.value AS INTEGER), 0) + 1) AS TEXT);";
        script.AppendLine(sql);
    }

    private IReadOnlyList<CanonicalRecord> QueryRecords(string whereAndOrderClause)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(span_id AS BLOB)), " +
            "typeof(trace_id), COALESCE(hex(CAST(trace_id AS BLOB)), ''), " +
            "typeof(parent_span_id), COALESCE(hex(CAST(parent_span_id AS BLOB)), ''), " +
            "hex(CAST(source AS BLOB)), " +
            "hex(CAST(harness AS BLOB)), " +
            "typeof(session_id), COALESCE(hex(CAST(session_id AS BLOB)), ''), " +
            "hex(CAST(name AS BLOB)), " +
            "hex(CAST(op AS BLOB)), " +
            "hex(CAST(kind AS BLOB)), " +
            "hex(CAST(timestamp AS BLOB)), " +
            "duration_ms, " +
            "hex(CAST(status AS BLOB)), " +
            "hex(CAST(tokens_json AS BLOB)), " +
            "hex(CAST(content_json AS BLOB)), " +
            "hex(CAST(cost_json AS BLOB)), " +
            "typeof(measurability_json), COALESCE(hex(CAST(measurability_json AS BLOB)), ''), " +
            "typeof(parts_json), COALESCE(hex(CAST(parts_json AS BLOB)), ''), " +
            "COALESCE(hex(raw), ''), typeof(raw) " +
            $"FROM records {whereAndOrderClause};");

        List<CanonicalRecord> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 24)
                continue;

            rows.Add(ParseRecordRow(cols));
        }

        return rows;
    }

    private static CanonicalRecord ParseRecordRow(string[] cols)
    {
        string recordSpanId = DecodeHexText(cols[0]);
        string? traceId = cols[1] == "null" ? null : DecodeHexText(cols[2]);
        string? parentSpanId = cols[3] == "null" ? null : DecodeHexText(cols[4]);
        string source = DecodeHexText(cols[5]);
        string harness = DecodeHexText(cols[6]);
        string? sessionId = cols[7] == "null" ? null : DecodeHexText(cols[8]);
        string name = DecodeHexText(cols[9]);
        string op = DecodeHexText(cols[10]);
        string kind = DecodeHexText(cols[11]);
        string timestamp = DecodeHexText(cols[12]);
        long durationMs = long.Parse(cols[13], NumberStyles.Integer, CultureInfo.InvariantCulture);
        string status = DecodeHexText(cols[14]);
        TokenUsage tokens = JsonSerializer.Deserialize<TokenUsage>(DecodeHexText(cols[15]), RecordJsonOptions)
            ?? throw new InvalidOperationException($"corrupt tokens_json for span {recordSpanId}.");
        Dictionary<string, string> storedContent = JsonSerializer.Deserialize<Dictionary<string, string>>(DecodeHexText(cols[16]), RecordJsonOptions)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        CostBlock cost = JsonSerializer.Deserialize<CostBlock>(DecodeHexText(cols[17]), RecordJsonOptions)
            ?? throw new InvalidOperationException($"corrupt cost_json for span {recordSpanId}.");

        IReadOnlyDictionary<string, JsonElement>? measurability = cols[18] == "null"
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(DecodeHexText(cols[19]), RecordJsonOptions);

        IReadOnlyList<ContentPart>? parts = null;
        if (cols[20] != "null")
        {
            string partsJson = cols[20] == "blob"
                ? CanonCompressedColumn.Decompress(Convert.FromHexString(cols[21]))
                : DecodeHexText(cols[21]);
            parts = JsonSerializer.Deserialize<List<ContentPart>>(partsJson, RecordJsonOptions);
        }

        IReadOnlyDictionary<string, string> content = parts is null
            ? storedContent
            : MergeContent(CanonValidation.ContentFromParts(parts), storedContent);

        IReadOnlyDictionary<string, JsonElement>? raw = null;
        if (cols[23] != "null")
        {
            string rawJson = cols[23] == "blob"
                ? CanonCompressedColumn.Decompress(Convert.FromHexString(cols[22]))
                : DecodeHexText(cols[22]);
            raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rawJson, RecordJsonOptions);
        }

        return new CanonicalRecord
        {
            SpanId = recordSpanId,
            TraceId = traceId,
            ParentSpanId = parentSpanId,
            Source = source,
            Harness = harness,
            SessionId = sessionId,
            Name = name,
            Op = op,
            Kind = kind,
            Timestamp = timestamp,
            DurationMs = durationMs,
            Status = status,
            Tokens = tokens,
            Content = content,
            Parts = parts,
            Cost = cost,
            Measurability = measurability,
            Raw = raw,
        };
    }

    private IReadOnlyList<SessionSummaryRow> QuerySessions(string suffixSql, string? singleSessionId)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(session_id AS BLOB)), " +
            "hex(CAST(harness AS BLOB)), " +
            "typeof(label), COALESCE(hex(CAST(label AS BLOB)), ''), " +
            "is_subagent, " +
            "typeof(parent_session), COALESCE(hex(CAST(parent_session AS BLOB)), ''), " +
            "typeof(agent_name), COALESCE(hex(CAST(agent_name AS BLOB)), ''), " +
            "typeof(repo), COALESCE(hex(CAST(repo AS BLOB)), ''), " +
            "typeof(branch), COALESCE(hex(CAST(branch AS BLOB)), ''), " +
            "typeof(started), COALESCE(hex(CAST(started AS BLOB)), ''), " +
            "typeof(ended), COALESCE(hex(CAST(ended AS BLOB)), ''), " +
            "typeof(summary_json), COALESCE(hex(CAST(summary_json AS BLOB)), ''), " +
            "typeof(problems_count), COALESCE(problems_count, 0), " +
            "typeof(context_limit_source), COALESCE(hex(CAST(context_limit_source AS BLOB)), ''), " +
            "hex(CAST(payload AS BLOB)) " +
            $"FROM session {suffixSql};");

        bool hasTurnTable = HasSessionTurnTable();
        Dictionary<string, List<JsonElement>>? turnsBySession = hasTurnTable
            ? ReadSessionTurnsMap(singleSessionId)
            : null;

        List<SessionSummaryRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 24)
                continue;

            string sessionId = DecodeHexText(cols[0]);
            string harness = DecodeHexText(cols[1]);
            string? label = cols[2] == "null" ? null : DecodeHexText(cols[3]);
            bool isSubagent = cols[4] != "0";
            string? parentSession = cols[5] == "null" ? null : DecodeHexText(cols[6]);
            string? agentName = cols[7] == "null" ? null : DecodeHexText(cols[8]);
            string? repo = cols[9] == "null" ? null : DecodeHexText(cols[10]);
            string? branch = cols[11] == "null" ? null : DecodeHexText(cols[12]);
            string? started = cols[13] == "null" ? null : DecodeHexText(cols[14]);
            string? ended = cols[15] == "null" ? null : DecodeHexText(cols[16]);
            string? summaryJson = cols[17] == "null" ? null : DecodeHexText(cols[18]);
            int? problemsCount = cols[19] == "null" ? null : int.Parse(cols[20], NumberStyles.Integer, CultureInfo.InvariantCulture);
            string? contextLimitSource = cols[21] == "null" ? null : DecodeHexText(cols[22]);
            string payloadText = DecodeHexText(cols[23]);

            List<JsonElement>? sessionTurns = null;
            if (turnsBySession is not null)
            {
                turnsBySession.TryGetValue(sessionId, out sessionTurns);
            }

            JsonElement payloadElement;
            if (hasTurnTable && JsonNode.Parse(payloadText) is JsonObject obj && !obj.ContainsKey("turns"))
            {
                JsonArray turnsArray = [];
                if (sessionTurns is not null)
                {
                    foreach (JsonElement turn in sessionTurns)
                        turnsArray.Add(JsonNode.Parse(turn.GetRawText()));
                }

                obj["turns"] = turnsArray;
                payloadElement = JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString());
            }
            else
            {
                payloadElement = JsonSerializer.Deserialize<JsonElement>(payloadText);
                if (sessionTurns is null
                    && payloadElement.ValueKind == JsonValueKind.Object
                    && payloadElement.TryGetProperty("turns", out JsonElement embeddedTurns)
                    && embeddedTurns.ValueKind == JsonValueKind.Array)
                {
                    sessionTurns = [];
                    foreach (JsonElement item in embeddedTurns.EnumerateArray())
                        sessionTurns.Add(item.Clone());
                }
            }

            rows.Add(new SessionSummaryRow(
                SessionId: sessionId,
                Harness: harness,
                Label: label,
                IsSubagent: isSubagent,
                ParentSession: parentSession,
                AgentName: agentName,
                Repo: repo,
                Branch: branch,
                Started: started,
                Ended: ended,
                SummaryJson: summaryJson,
                ProblemsCount: problemsCount,
                ContextLimitSource: contextLimitSource,
                Payload: payloadElement,
                Turns: sessionTurns));
        }

        return rows;
    }

    private Dictionary<string, List<JsonElement>> ReadSessionTurnsMap(string? singleSessionId)
    {
        string whereClause = singleSessionId is null ? string.Empty : $"WHERE session_id = {TextExpr(singleSessionId)} ";
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(session_id AS BLOB)), " +
            "typeof(turn_json), " +
            "hex(CASE WHEN typeof(turn_json) = 'blob' THEN turn_json ELSE CAST(turn_json AS BLOB) END) " +
            $"FROM session_turn {whereClause}ORDER BY session_id, turn_index;");

        Dictionary<string, List<JsonElement>> map = new(StringComparer.Ordinal);
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 3)
                continue;

            string sessionId = DecodeHexText(cols[0]);
            string turnJson = cols[1] == "blob"
                ? CanonCompressedColumn.Decompress(Convert.FromHexString(cols[2]))
                : DecodeHexText(cols[2]);

            try
            {
                JsonElement turnElement = JsonSerializer.Deserialize<JsonElement>(turnJson);
                if (turnElement.ValueKind != JsonValueKind.Object)
                    continue;

                if (!map.TryGetValue(sessionId, out List<JsonElement>? list))
                {
                    list = [];
                    map[sessionId] = list;
                }

                list.Add(turnElement);
            }
            catch (JsonException)
            {
                // Ignore malformed stored turn fragments.
            }
        }

        return map;
    }

    private bool HasSessionTurnTable()
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'session_turn' LIMIT 1;");
        return Lines(output).Any();
    }

    private IReadOnlyList<Finding> QueryFindings(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(id AS BLOB)), " +
            "hex(CAST(detector_id AS BLOB)), " +
            "hex(CAST(title AS BLOB)), " +
            "hex(CAST(mechanism AS BLOB)), " +
            "hex(CAST(confidence AS BLOB)), " +
            "typeof(estimated_waste_tokens), COALESCE(estimated_waste_tokens, 0), " +
            "hex(CAST(recommendation AS BLOB)), " +
            "hex(CAST(error_bar_json AS BLOB)), " +
            "hex(CAST(evidence_links_json AS BLOB)), " +
            "hex(CAST(outcome_risk_caveat AS BLOB)), " +
            "typeof(run_id), COALESCE(hex(CAST(run_id AS BLOB)), ''), " +
            "typeof(session_id), COALESCE(hex(CAST(session_id AS BLOB)), ''), " +
            "rank_score, " +
            "typeof(payload), COALESCE(hex(CAST(payload AS BLOB)), '') " +
            $"FROM finding {suffixSql};");

        List<Finding> findings = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 18)
                continue;

            findings.Add(ParseFindingRow(cols));
        }

        return findings;
    }

    private static Finding ParseFindingRow(string[] cols)
    {
        string id = DecodeHexText(cols[0]);
        string detectorId = DecodeHexText(cols[1]);
        string title = DecodeHexText(cols[2]);
        string mechanism = DecodeHexText(cols[3]);
        string confidence = DecodeHexText(cols[4]);
        long? estimatedWasteTokens = cols[5] == "null"
            ? null
            : long.Parse(cols[6], NumberStyles.Integer, CultureInfo.InvariantCulture);
        string recommendation = DecodeHexText(cols[7]);
        string errorBarRaw = DecodeHexText(cols[8]);
        FindingErrorBar? errorBar = errorBarRaw == "null"
            ? null
            : JsonSerializer.Deserialize<FindingErrorBar>(errorBarRaw, CamelCaseJsonOptions);
        IReadOnlyList<FindingEvidenceLink> evidenceLinks =
            JsonSerializer.Deserialize<List<FindingEvidenceLink>>(DecodeHexText(cols[9]), CamelCaseJsonOptions)
            ?? [];
        string outcomeRiskCaveat = DecodeHexText(cols[10]);
        string? runId = cols[11] == "null" ? null : DecodeHexText(cols[12]);
        string? sessionId = cols[13] == "null" ? null : DecodeHexText(cols[14]);
        double rankScore = double.Parse(cols[15], NumberStyles.Float, CultureInfo.InvariantCulture);

        Dictionary<string, object?>? payloadDict = null;
        string? measurementClass = null;
        string? confidenceBasis = null;
        string? whatWouldRaiseIt = null;
        if (cols[16] != "null")
        {
            string payloadJson = DecodeHexText(cols[17]);
            ExtractFindingPayloadFields(
                payloadJson,
                out payloadDict,
                out measurementClass,
                out confidenceBasis,
                out whatWouldRaiseIt);
        }

        return new Finding(
            Id: id,
            DetectorId: detectorId,
            Title: title,
            Mechanism: mechanism,
            EvidenceLinks: evidenceLinks,
            Confidence: confidence,
            EstimatedWasteTokens: estimatedWasteTokens,
            Recommendation: recommendation,
            ErrorBar: errorBar,
            OutcomeRiskCaveat: outcomeRiskCaveat,
            RunId: runId,
            SessionId: sessionId,
            RankScore: rankScore,
            MeasurementClass: measurementClass,
            ConfidenceBasis: confidenceBasis,
            WhatWouldRaiseIt: whatWouldRaiseIt,
            Payload: payloadDict);
    }

    private static SplitSessionPayload TrySplitSessionTurns(SessionSummaryRow row)
    {
        List<(int TurnIndex, string TurnJson)> turns = [];
        if (row.Payload.ValueKind != JsonValueKind.Object)
        {
            string rawPayload = row.Payload.ValueKind == JsonValueKind.Undefined ? "{}" : row.Payload.GetRawText();
            if (row.Turns is not null)
            {
                for (int i = 0; i < row.Turns.Count; i++)
                    turns.Add((TurnIndexOf(row.Turns[i], i), row.Turns[i].GetRawText()));
            }

            return new SplitSessionPayload(rawPayload, row.SummaryJson, row.ProblemsCount, row.ContextLimitSource, turns);
        }

        JsonObject obj = (JsonObject)JsonNode.Parse(row.Payload.GetRawText())!;

        string? summaryJson = row.SummaryJson;
        if (summaryJson is null && obj.TryGetPropertyValue("summary", out JsonNode? summaryNode) && summaryNode is not null)
            summaryJson = summaryNode.ToJsonString();

        int? problemsCount = row.ProblemsCount;
        if (problemsCount is null && obj.TryGetPropertyValue("problems", out JsonNode? problemsNode) && problemsNode is JsonArray problemsArray)
            problemsCount = problemsArray.Count;

        string? contextLimitSource = row.ContextLimitSource;
        if (contextLimitSource is null
            && obj.TryGetPropertyValue("context", out JsonNode? contextNode)
            && contextNode is JsonObject contextObj
            && contextObj.TryGetPropertyValue("contextLimitSource", out JsonNode? clsNode)
            && clsNode is JsonValue clsVal
            && clsVal.TryGetValue(out string? clsString)
            && !string.IsNullOrEmpty(clsString))
        {
            contextLimitSource = clsString;
        }

        if (obj.TryGetPropertyValue("turns", out JsonNode? turnsNode))
        {
            obj.Remove("turns");
            if (turnsNode is JsonArray turnsArray)
            {
                for (int i = 0; i < turnsArray.Count; i++)
                {
                    JsonNode? item = turnsArray[i];
                    if (item is null)
                        continue;

                    int idx = i;
                    if (item is JsonObject turnObj
                        && turnObj.TryGetPropertyValue("index", out JsonNode? idxNode)
                        && idxNode is JsonValue idxVal
                        && idxVal.TryGetValue(out int explicitIdx))
                    {
                        idx = explicitIdx;
                    }

                    turns.Add((idx, item.ToJsonString()));
                }
            }
        }
        else if (row.Turns is not null)
        {
            for (int i = 0; i < row.Turns.Count; i++)
                turns.Add((TurnIndexOf(row.Turns[i], i), row.Turns[i].GetRawText()));
        }

        return new SplitSessionPayload(obj.ToJsonString(), summaryJson, problemsCount, contextLimitSource, turns);
    }

    private static int TurnIndexOf(JsonElement turn, int position)
    {
        if (turn.ValueKind == JsonValueKind.Object
            && turn.TryGetProperty("index", out JsonElement idxEl)
            && idxEl.ValueKind == JsonValueKind.Number
            && idxEl.TryGetInt32(out int idx))
        {
            return idx;
        }

        return position;
    }

    private static string? BuildFindingPayloadJson(Finding finding)
    {
        if (finding.Payload is not null)
        {
            return finding.Payload.Count == 0
                ? null
                : JsonSerializer.Serialize(finding.Payload, CamelCaseJsonOptions);
        }

        Dictionary<string, object?> extra = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(finding.MeasurementClass))
            extra["measurementClass"] = finding.MeasurementClass;
        if (!string.IsNullOrEmpty(finding.ConfidenceBasis))
            extra["confidenceBasis"] = finding.ConfidenceBasis;
        if (!string.IsNullOrEmpty(finding.WhatWouldRaiseIt))
            extra["whatWouldRaiseIt"] = finding.WhatWouldRaiseIt;

        return extra.Count == 0 ? null : JsonSerializer.Serialize(extra, CamelCaseJsonOptions);
    }

    private static void ExtractFindingPayloadFields(
        string payloadJson,
        out Dictionary<string, object?>? payloadDict,
        out string? measurementClass,
        out string? confidenceBasis,
        out string? whatWouldRaiseIt)
    {
        payloadDict = null;
        measurementClass = null;
        confidenceBasis = null;
        whatWouldRaiseIt = null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            payloadDict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                payloadDict[prop.Name] = prop.Value.Clone();
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    switch (prop.Name)
                    {
                        case "measurementClass":
                            measurementClass = prop.Value.GetString();
                            break;
                        case "confidenceBasis":
                            confidenceBasis = prop.Value.GetString();
                            break;
                        case "whatWouldRaiseIt":
                            whatWouldRaiseIt = prop.Value.GetString();
                            break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed extra payload JSON.
        }
    }

    private sealed record SplitSessionPayload(
        string PayloadJson,
        string? SummaryJson,
        int? ProblemsCount,
        string? ContextLimitSource,
        IReadOnlyList<(int TurnIndex, string TurnJson)> Turns);

    private static bool IsBusy(ProcessResult result)
    {
        string output = result.StandardOutput + "\n" + result.StandardError;
        if (output.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
            || output.Contains("database is busy", StringComparison.OrdinalIgnoreCase)
            || LockedWordRegex().IsMatch(output)
            || BusyWordRegex().IsMatch(output)
            || SqliteBusyOrLockedRegex().IsMatch(output))
        {
            return true;
        }

        return result.ExitCode == SqliteBusyResultCode || result.ExitCode == SqliteLockedResultCode;
    }
}
