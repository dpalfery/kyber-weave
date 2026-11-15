using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Application;
using KyberDash.Domain;

namespace KyberDash.Domain.Tests;

/// <summary>In-memory <see cref="ICanonStoreRepository"/> for tests that need no database file.</summary>
/// <remarks>
/// It keeps records, sessions, findings, rollups, checkpoints, quarantine entries, and problems in
/// memory with the same keying the SQLite store uses, so a domain test can assert pipeline effects
/// without paying for process-based persistence. Query coverage is limited to what the domain and
/// application engines actually read.
/// </remarks>
public class FakeCanonStoreRepository : ICanonStoreRepository
{
    private readonly List<CanonicalRecord> records = [];
    private readonly Dictionary<string, SessionSummaryRow> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(int TurnIndex, string TurnJson)>> turns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Finding> findings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunRow> runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionRow> executions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HarnessRollupRow> rollups = new(StringComparer.Ordinal);
    private readonly List<QuarantineRow> quarantine = [];
    private readonly List<SpanProblemRow> problems = [];
    private readonly List<IngestLogEntry> ingestLog = [];
    private readonly Dictionary<string, string> metadata = new(StringComparer.Ordinal)
    {
        ["schema_version"] = CanonSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture),
        ["detector_version"] = CanonSchema.DetectorVersion.ToString(CultureInfo.InvariantCulture),
    };
    private readonly Dictionary<string, long> tokenCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceCheckpoint> checkpoints = new(StringComparer.Ordinal);
    private readonly List<RecordProvenanceRow> provenance = [];
    private readonly List<RefreshRunRow> refreshRuns = [];
    private readonly List<PredictionRow> predictions = [];
    private readonly List<OtlpLog> pendingLogs = [];
    private readonly List<OtlpMetric> metrics = [];
    private long writeMarker;

    /// <summary>Records persisted so far, in insertion order.</summary>
    public IReadOnlyList<CanonicalRecord> Records => records;

    /// <summary>Whether <see cref="Vacuum"/> has been called.</summary>
    public bool VacuumCalled { get; private set; }

    /// <summary>Whether <see cref="EnsureInitialized"/> has been called.</summary>
    public bool Initialized { get; private set; }

    /// <inheritdoc />
    public string DatabasePath => ":memory:";

    /// <inheritdoc />
    public void EnsureInitialized() => Initialized = true;

    /// <inheritdoc />
    public string? GetMetadata(string key) => metadata.TryGetValue(key, out string? value) ? value : null;

    /// <inheritdoc />
    public void SetMetadata(string key, string value) => metadata[key] = value;

    /// <inheritdoc />
    public bool CompareAndSwapMetadata(string key, string? expected, string? newValue)
    {
        string? current = GetMetadata(key);
        if (!string.Equals(current, expected, StringComparison.Ordinal))
        {
            return false;
        }

        if (newValue is null)
        {
            _ = metadata.Remove(key);
        }
        else
        {
            metadata[key] = newValue;
        }

        return true;
    }

    /// <inheritdoc />
    public long RecordsWriteMarker() => writeMarker;

    /// <inheritdoc />
    public int CountRecords() => records.Count;

    /// <inheritdoc />
    public IReadOnlyList<string> ListObservedHarnesses() =>
        [.. records.Select(static record => record.Harness).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <inheritdoc />
    public void PutRecord(CanonicalRecord record) => PutRecords([record]);

    /// <inheritdoc />
    public void PutRecords(IEnumerable<CanonicalRecord> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        HashSet<string> touched = new(StringComparer.Ordinal);
        bool unkeyed = false;
        foreach (CanonicalRecord record in batch)
        {
            records.RemoveAll(existing => string.Equals(existing.SpanId, record.SpanId, StringComparison.Ordinal));
            records.Add(record);
            string? key = SessionKeyOf(record);
            if (key is null)
            {
                unkeyed = true;
            }
            else
            {
                _ = touched.Add(key);
            }
        }

        long prevSeq = writeMarker;
        writeMarker++;
        RecordDirty(prevSeq, writeMarker, touched, unkeyed);
    }

    /// <inheritdoc />
    public CanonicalRecord? GetRecord(string spanId) =>
        records.FirstOrDefault(record => string.Equals(record.SpanId, spanId, StringComparison.Ordinal));

    /// <inheritdoc />
    public bool HasRecord(string spanId) => GetRecord(spanId) is not null;

    /// <inheritdoc />
    public IReadOnlyList<CanonicalRecord> QueryAllRecords() =>
        [.. records.OrderBy(static record => record.Timestamp, StringComparer.Ordinal)];

    /// <inheritdoc />
    public IReadOnlyList<CanonicalRecord> ListRecordsBySessionKey(string sessionKey) =>
        [.. records
            .Where(record => string.Equals(record.SessionId ?? record.TraceId, sessionKey, StringComparison.Ordinal))
            .OrderBy(static record => record.Timestamp, StringComparer.Ordinal)];

    /// <inheritdoc />
    public void SetCosts(IReadOnlyDictionary<string, CostBlock> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        for (int index = 0; index < records.Count; index++)
        {
            if (updates.TryGetValue(records[index].SpanId, out CostBlock? cost))
            {
                records[index] = records[index] with { Cost = cost };
            }
        }
    }

    /// <inheritdoc />
    public void QuarantineAndDelete(QuarantineRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _ = records.RemoveAll(record => string.Equals(record.SpanId, row.SpanId, StringComparison.Ordinal));
        quarantine.RemoveAll(existing => string.Equals(existing.SpanId, row.SpanId, StringComparison.Ordinal));
        quarantine.Add(row);
    }

    /// <inheritdoc />
    public IReadOnlyList<QuarantineRow> GetQuarantined(string? harness = null) =>
        harness is null
            ? quarantine
            : [.. quarantine.Where(row => string.Equals(row.Harness, harness, StringComparison.Ordinal))];

    /// <inheritdoc />
    public void RecordProblem(SpanProblemRow problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        problems.Add(problem);
    }

    /// <inheritdoc />
    public void RecordProblems(IEnumerable<SpanProblemRow> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        problems.AddRange(batch);
    }

    /// <inheritdoc />
    public IReadOnlyList<SpanProblemRow> GetProblems(string? spanId = null) =>
        spanId is null
            ? problems
            : [.. problems.Where(row => string.Equals(row.SpanId, spanId, StringComparison.Ordinal))];

    /// <inheritdoc />
    public void RecordIngest(string source, int count, string timestamp) =>
        ingestLog.Add(new IngestLogEntry(ingestLog.Count + 1, source, count, timestamp));

    /// <inheritdoc />
    public IReadOnlyList<IngestLogEntry> GetIngestLog(string? source = null) =>
        source is null
            ? ingestLog
            : [.. ingestLog.Where(entry => string.Equals(entry.Source, source, StringComparison.Ordinal))];

    /// <inheritdoc />
    public long? GetCachedTokenCount(string hash) => tokenCounts.TryGetValue(hash, out long count) ? count : null;

    /// <inheritdoc />
    public void PutCachedTokenCounts(IReadOnlyDictionary<string, long> counts, string model)
    {
        ArgumentNullException.ThrowIfNull(counts);
        _ = model;
        foreach (KeyValuePair<string, long> entry in counts)
        {
            tokenCounts[entry.Key] = entry.Value;
        }
    }

    /// <inheritdoc />
    public void PutSession(SessionSummaryRow row) => PutSessions([row]);

    /// <inheritdoc />
    public void PutSessions(IEnumerable<SessionSummaryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (SessionSummaryRow row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            sessions[row.SessionId] = row;
            List<(int TurnIndex, string TurnJson)> sessionTurns = [];
            if (row.Payload.ValueKind == JsonValueKind.Object
                && row.Payload.TryGetProperty("turns", out JsonElement embedded)
                && embedded.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement turn in embedded.EnumerateArray())
                {
                    sessionTurns.Add((index++, turn.GetRawText()));
                }
            }
            else if (row.Turns is { Count: > 0 })
            {
                for (int index = 0; index < row.Turns.Count; index++)
                {
                    sessionTurns.Add((index, row.Turns[index].GetRawText()));
                }
            }

            turns[row.SessionId] = sessionTurns;
        }
    }

    /// <inheritdoc />
    public SessionSummaryRow? GetSession(string sessionId) =>
        sessions.TryGetValue(sessionId, out SessionSummaryRow? row) ? row : null;

    /// <inheritdoc />
    public IReadOnlyList<SessionSummaryRow> ListSessions(string? harness = null) =>
        [.. sessions.Values
            .Where(row => harness is null || string.Equals(row.Harness, harness, StringComparison.Ordinal))
            .OrderByDescending(static row => row.Started, StringComparer.Ordinal)];

    /// <inheritdoc />
    public void DeleteSession(string sessionId) => DeleteSessions([sessionId]);

    /// <inheritdoc />
    public void DeleteSessions(IEnumerable<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        foreach (string id in sessionIds)
        {
            _ = sessions.Remove(id);
            _ = turns.Remove(id);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(int TurnIndex, string TurnJson)> GetSessionTurns(string sessionId) =>
        turns.TryGetValue(sessionId, out List<(int TurnIndex, string TurnJson)>? rows) ? rows : [];

    /// <inheritdoc />
    public DirtyProjectionState GetDirtyProjectionState()
    {
        string? raw = GetMetadata(CanonSchema.ProjectionDirtySessionsKey);
        if (raw is null)
        {
            return new DirtyProjectionState(0, 0, null, IsWildcard: true);
        }

        using JsonDocument document = JsonDocument.Parse(raw);
        JsonElement root = document.RootElement;
        List<string> keys = [];
        bool wildcard = !root.TryGetProperty("keys", out JsonElement keysElement)
            || keysElement.ValueKind == JsonValueKind.String;
        if (!wildcard)
        {
            foreach (JsonElement item in keysElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } key)
                {
                    keys.Add(key);
                }
            }
        }

        return new DirtyProjectionState(
            root.GetProperty("fromSeq").GetInt64(),
            root.GetProperty("toSeq").GetInt64(),
            keys,
            wildcard);
    }

    /// <inheritdoc />
    public void MarkProjectionClean(long writeSeq, string schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(schemaVersion);
        SetMetadata(
            CanonSchema.ProjectionDirtySessionsKey,
            JsonSerializer.Serialize(new { fromSeq = writeSeq, toSeq = writeSeq, keys = Array.Empty<string>() }));
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetProjectionSessionKeyMap()
    {
        string? raw = GetMetadata(CanonSchema.ProjectionSessionKeyMapKey);
        return raw is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(raw, JsonDefaults.Options)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public void SaveProjectionSessionKeyMap(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        SetMetadata(CanonSchema.ProjectionSessionKeyMapKey, JsonSerializer.Serialize(map, JsonDefaults.Options));
    }

    /// <inheritdoc />
    public void PutFinding(Finding finding) => PutFindings([finding]);

    /// <inheritdoc />
    public void PutFindings(IEnumerable<Finding> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (Finding finding in batch)
        {
            findings[finding.Id] = finding;
        }
    }

    /// <inheritdoc />
    public Finding? GetFinding(string id) => findings.TryGetValue(id, out Finding? finding) ? finding : null;

    /// <inheritdoc />
    public IReadOnlyList<Finding> ListFindings(string? runId = null, string? sessionId = null) =>
        [.. findings.Values
            .Where(finding => (runId is null || string.Equals(finding.RunId, runId, StringComparison.Ordinal))
                && (sessionId is null || string.Equals(finding.SessionId, sessionId, StringComparison.Ordinal)))];

    /// <inheritdoc />
    public void DeleteFindingsForSession(string sessionId)
    {
        foreach (string id in findings.Values
            .Where(finding => string.Equals(finding.SessionId, sessionId, StringComparison.Ordinal))
            .Select(static finding => finding.Id)
            .ToList())
        {
            _ = findings.Remove(id);
        }
    }

    /// <inheritdoc />
    public void UpsertRuns(IEnumerable<RunRow> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (RunRow run in batch)
        {
            runs[run.RunId] = run;
        }
    }

    /// <inheritdoc />
    public RunRow? GetRun(string runId) => runs.TryGetValue(runId, out RunRow? run) ? run : null;

    /// <inheritdoc />
    public IReadOnlyList<RunRow> ListRuns(string? harness = null) =>
        [.. runs.Values
            .Where(run => harness is null || string.Equals(run.Harness, harness, StringComparison.Ordinal))
            .OrderByDescending(static run => run.Started, StringComparer.Ordinal)];

    /// <inheritdoc />
    public void UpsertExecutions(IEnumerable<ExecutionRow> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (ExecutionRow execution in batch)
        {
            executions[execution.ExecutionId] = execution;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ExecutionRow> ListExecutionsForRun(string runId) =>
        [.. executions.Values.Where(execution => string.Equals(execution.RunId, runId, StringComparison.Ordinal))];

    /// <inheritdoc />
    public IReadOnlyList<ExecutionRow> ListExecutionsByHarness(string harness) =>
        [.. executions.Values.Where(execution => string.Equals(execution.Harness, harness, StringComparison.Ordinal))];

    /// <inheritdoc />
    public void UpsertHarnessRollups(IEnumerable<HarnessRollupRow> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (HarnessRollupRow rollup in batch)
        {
            rollups[rollup.Harness] = rollup;
        }
    }

    /// <inheritdoc />
    public HarnessRollupRow? GetHarnessRollup(string harness) =>
        rollups.TryGetValue(harness, out HarnessRollupRow? rollup) ? rollup : null;

    /// <inheritdoc />
    public IReadOnlyList<HarnessRollupRow> ListHarnessRollups() =>
        [.. rollups.Values.OrderBy(static rollup => rollup.Harness, StringComparer.Ordinal)];

    /// <inheritdoc />
    public void PutPrediction(PredictionRow prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        predictions.RemoveAll(existing => string.Equals(existing.Id, prediction.Id, StringComparison.Ordinal));
        predictions.Add(prediction);
    }

    /// <inheritdoc />
    public IReadOnlyList<PredictionRow> ListPredictions(string? runId = null) =>
        runId is null
            ? predictions
            : [.. predictions.Where(prediction => string.Equals(prediction.RunId, runId, StringComparison.Ordinal))];

    /// <inheritdoc />
    public IReadOnlyList<StoredProblemRow> ListStoredProblems(string? harness = null, string? spanId = null)
    {
        List<StoredProblemRow> rows = [];
        for (int index = 0; index < problems.Count; index++)
        {
            SpanProblemRow problem = problems[index];
            if (harness is not null && !string.Equals(problem.Harness, harness, StringComparison.Ordinal))
            {
                continue;
            }

            if (spanId is not null && !string.Equals(problem.SpanId, spanId, StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(new StoredProblemRow(
                index + 1,
                problem.SpanId,
                problem.Severity,
                problem.Code,
                problem.Message,
                problem.Location,
                problem.SessionId,
                problem.Harness,
                problem.Timestamp,
                problem.Stack));
        }

        return rows;
    }

    /// <inheritdoc />
    public StoredProblemRow? GetStoredProblem(int id) =>
        ListStoredProblems().FirstOrDefault(row => row.Id == id);

    /// <inheritdoc />
    public IReadOnlyList<QuarantineRow> ListQuarantine(string? harness = null) => GetQuarantined(harness);

    /// <inheritdoc />
    public QuarantineRow? GetQuarantineItem(string spanId) =>
        quarantine.FirstOrDefault(row => string.Equals(row.SpanId, spanId, StringComparison.Ordinal));

    /// <inheritdoc />
    public SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey) =>
        checkpoints.TryGetValue(CheckpointKey(harnessId, sourceKey), out SourceCheckpoint? checkpoint) ? checkpoint : null;

    /// <inheritdoc />
    public void UpsertSourceCheckpoint(SourceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoints[CheckpointKey(checkpoint.HarnessId, checkpoint.SourceKey)] = checkpoint;
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null) =>
        [.. checkpoints.Values.Where(checkpoint => harnessId is null || string.Equals(checkpoint.HarnessId, harnessId, StringComparison.Ordinal))];

    /// <inheritdoc />
    public void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        provenance.AddRange(rows);
    }

    /// <summary>Provenance rows recorded so far.</summary>
    public IReadOnlyList<RecordProvenanceRow> Provenance => provenance;

    /// <inheritdoc />
    public void RecordRefreshRun(RefreshRunRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        refreshRuns.Add(row);
    }

    /// <inheritdoc />
    public IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20) =>
        [.. refreshRuns.OrderByDescending(static row => row.StartedAt, StringComparer.Ordinal).Take(limit)];

    /// <inheritdoc />
    public void StorePendingLogs(IEnumerable<OtlpLog> logs, string? harness = null)
    {
        ArgumentNullException.ThrowIfNull(logs);
        _ = harness;
        pendingLogs.AddRange(logs);
    }

    /// <inheritdoc />
    public IReadOnlyList<OtlpLog> TakePendingLogs(int limit = 500)
    {
        List<OtlpLog> taken = [.. pendingLogs.Take(limit)];
        pendingLogs.RemoveRange(0, taken.Count);
        return taken;
    }

    /// <inheritdoc />
    public void StoreQuarantinedLogs(IEnumerable<(OtlpLog Log, string Reason)> logs, string? harness = null)
    {
        ArgumentNullException.ThrowIfNull(logs);
        _ = harness;
    }

    /// <inheritdoc />
    public bool IsLogEnriched(string logId) => false;

    /// <inheritdoc />
    public void MarkLogsEnriched(IEnumerable<string> logIds)
    {
        ArgumentNullException.ThrowIfNull(logIds);
    }

    /// <inheritdoc />
    public void StoreMetrics(IEnumerable<OtlpMetric> batch, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _ = source;
        metrics.AddRange(batch);
    }

    /// <summary>Metric points recorded so far.</summary>
    public IReadOnlyList<OtlpMetric> Metrics => metrics;

    /// <inheritdoc />
    public int PurgeContentBefore(string cutoffUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cutoffUtc);
        int purged = 0;
        for (int index = 0; index < records.Count; index++)
        {
            if (string.CompareOrdinal(records[index].Timestamp, cutoffUtc) >= 0 || records[index].Content.Count == 0)
            {
                continue;
            }

            records[index] = records[index] with
            {
                Content = new Dictionary<string, string>(StringComparer.Ordinal),
                Parts = null,
            };
            purged++;
        }

        return purged;
    }

    /// <inheritdoc />
    public StoreWipeReport WipeAll()
    {
        int recordCount = records.Count;
        int sessionCount = sessions.Count;
        int turnCount = turns.Values.Sum(static rows => rows.Count);
        int runCount = runs.Count;
        int executionCount = executions.Count;
        int findingCount = findings.Count;
        int rollupCount = rollups.Count;
        int problemCount = problems.Count;
        int quarantineCount = quarantine.Count;

        records.Clear();
        sessions.Clear();
        turns.Clear();
        runs.Clear();
        executions.Clear();
        findings.Clear();
        rollups.Clear();
        problems.Clear();
        quarantine.Clear();
        pendingLogs.Clear();
        metrics.Clear();

        return new StoreWipeReport(
            recordCount,
            sessionCount,
            turnCount,
            runCount,
            executionCount,
            findingCount,
            rollupCount,
            problemCount,
            quarantineCount,
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public StoreWipeReport WipeHarnesses(IEnumerable<string> harnesses)
    {
        ArgumentNullException.ThrowIfNull(harnesses);
        HashSet<string> targets = [.. harnesses.Select(CanonPricing.NormalizeHarnessName)];
        int recordCount = records.Count(record => targets.Contains(record.Harness));
        records.RemoveAll(record => targets.Contains(record.Harness));

        List<string> sessionIds = [.. sessions.Values
            .Where(row => targets.Contains(row.Harness))
            .Select(static row => row.SessionId)];
        int turnCount = sessionIds.Sum(id => turns.TryGetValue(id, out List<(int TurnIndex, string TurnJson)>? rows) ? rows.Count : 0);
        int findingCount = findings.Values.Count(finding => finding.SessionId is not null && sessionIds.Contains(finding.SessionId, StringComparer.Ordinal));
        foreach (string id in sessionIds)
        {
            _ = sessions.Remove(id);
            _ = turns.Remove(id);
        }

        foreach (string id in findings.Values
            .Where(finding => finding.SessionId is not null && sessionIds.Contains(finding.SessionId, StringComparer.Ordinal))
            .Select(static finding => finding.Id)
            .ToList())
        {
            _ = findings.Remove(id);
        }

        int runCount = runs.Values.Count(run => targets.Contains(run.Harness));
        foreach (string id in runs.Values.Where(run => targets.Contains(run.Harness)).Select(static run => run.RunId).ToList())
        {
            _ = runs.Remove(id);
        }

        int executionCount = executions.Values.Count(execution => targets.Contains(execution.Harness));
        foreach (string id in executions.Values.Where(execution => targets.Contains(execution.Harness)).Select(static execution => execution.ExecutionId).ToList())
        {
            _ = executions.Remove(id);
        }

        int rollupCount = rollups.Values.Count(rollup => targets.Contains(rollup.Harness));
        foreach (string id in rollups.Values.Where(rollup => targets.Contains(rollup.Harness)).Select(static rollup => rollup.Harness).ToList())
        {
            _ = rollups.Remove(id);
        }

        int problemCount = problems.Count(problem => problem.Harness is not null && targets.Contains(problem.Harness));
        problems.RemoveAll(problem => problem.Harness is not null && targets.Contains(problem.Harness));
        int quarantineCount = quarantine.Count(row => row.Harness is not null && targets.Contains(row.Harness));
        quarantine.RemoveAll(row => row.Harness is not null && targets.Contains(row.Harness));

        return new StoreWipeReport(
            recordCount,
            sessionIds.Count,
            turnCount,
            runCount,
            executionCount,
            findingCount,
            rollupCount,
            problemCount,
            quarantineCount,
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public void Vacuum() => VacuumCalled = true;

    /// <inheritdoc />
    public IReadOnlyList<string> GetBuiltSessionIds() => [.. sessions.Keys];

    /// <inheritdoc />
    public IReadOnlyList<string> GetDistinctSessionKeys() =>
        [.. records
            .Where(static r => SessionKeyOf(r) is not null)
            .GroupBy(static r => SessionKeyOf(r)!, StringComparer.Ordinal)
            .Select(static g => (Key: g.Key, Started: g.Min(static r => r.Timestamp) ?? string.Empty))
            .OrderByDescending(static x => x.Started, StringComparer.Ordinal)
            .Select(static x => x.Key)];

    /// <inheritdoc />
    public IReadOnlyList<(string Key, string Harness)> GetSessionKeyHarnesses() =>
        [.. records
            .Where(static r => SessionKeyOf(r) is not null)
            .Select(static r => (SessionKeyOf(r)!, r.Harness))
            .Distinct()];

    /// <inheritdoc />
    public bool HasCurrentDetectorVersion() =>
        string.Equals(GetMetadata("detector_version"), CanonSchema.DetectorVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <inheritdoc />
    public void SetDetectorVersion(int version = CanonSchema.DetectorVersion) =>
        SetMetadata("detector_version", version.ToString(CultureInfo.InvariantCulture));

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
    public void RecordProjectedSessionKeys(long seq, IReadOnlyDictionary<string, IReadOnlyList<string>> sessionIdsByKey)
    {
        ArgumentNullException.ThrowIfNull(sessionIdsByKey);
        SetMetadata(CanonSchema.ProjectionDirtySessionsKey, JsonSerializer.Serialize(new { fromSeq = seq, toSeq = seq, keys = Array.Empty<string>() }));
        SetMetadata(CanonSchema.ProjectionSessionKeyMapKey, JsonSerializer.Serialize(sessionIdsByKey));
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, int> CountRecordsBySource() =>
        records
            .Where(static record => !string.IsNullOrEmpty(record.Source))
            .GroupBy(static record => record.Source, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    /// <inheritdoc />
    public long? GetMaxRecordRowId() => records.Count > 0 ? records.Count : null;

    /// <inheritdoc />
    public uint ComputeStoreGeneration()
    {
        string cleanStamp = GetMetadata("last_clean_at") ?? string.Empty;
        string count = CountRecords().ToString(CultureInfo.InvariantCulture);
        long? maxRow = GetMaxRecordRowId();
        string highWater = maxRow.HasValue ? maxRow.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

        uint hash = 0x811c9dc5u;
        foreach (string part in new[] { cleanStamp, count, highWater })
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

    /// <inheritdoc />
    public IReadOnlyList<JsonElement> ListSessionTurnPayloads(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!turns.TryGetValue(sessionId, out List<(int TurnIndex, string TurnJson)>? rows) || rows.Count == 0)
        {
            return GetSession(sessionId)?.Turns ?? [];
        }

        List<JsonElement> result = [];
        foreach ((int _, string turnJson) in rows.OrderBy(static r => r.TurnIndex))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(turnJson);
                result.Add(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // Skip malformed turn payloads just as SQLite store queries do.
            }
        }

        return result;
    }

    /// <inheritdoc />
    public JsonElement? GetSessionTurnPayload(string sessionId, int turnIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (turns.TryGetValue(sessionId, out List<(int TurnIndex, string TurnJson)>? rows))
        {
            foreach ((int idx, string turnJson) in rows)
            {
                if (idx != turnIndex)
                    continue;

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
        _ = rollups.Remove(harness);
    }

    /// <inheritdoc />
    public int CountQuarantine(string? harness = null) =>
        ListQuarantine(harness).Count;

    /// <inheritdoc />
    public IReadOnlyList<QuarantineRow> ListQuarantinePage(string? harness = null, int? limit = null, int? offset = null)
    {
        IEnumerable<QuarantineRow> query = quarantine
            .Where(row => string.IsNullOrWhiteSpace(harness) || string.Equals(row.Harness, harness.Trim(), StringComparison.Ordinal))
            .OrderByDescending(static row => row.Timestamp, StringComparer.Ordinal)
            .ThenBy(static row => row.SpanId, StringComparer.Ordinal);
        if (offset.HasValue && offset.Value > 0)
        {
            query = query.Skip(offset.Value);
        }

        if (limit.HasValue)
        {
            query = query.Take(Math.Max(1, limit.Value));
        }

        return [.. query];
    }

    /// <inheritdoc />
    public int CountProblems(string? harness = null) =>
        ListStoredProblems(harness).Count;

    /// <inheritdoc />
    public StoredProblemRow? GetProblemById(int id) => GetStoredProblem(id);

    /// <inheritdoc />
    public IReadOnlyList<StoredProblemRow> ListProblems(string? harness = null, int? limit = null, int? offset = null)
    {
        IEnumerable<StoredProblemRow> query = ListStoredProblems(string.IsNullOrWhiteSpace(harness) ? null : harness.Trim())
            .OrderByDescending(static row => row.Id);
        if (offset.HasValue && offset.Value > 0)
        {
            query = query.Skip(offset.Value);
        }

        if (limit.HasValue)
        {
            query = query.Take(Math.Max(1, limit.Value));
        }

        return [.. query];
    }

    /// <inheritdoc />
    public IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey) =>
        [.. provenance
            .Where(row => string.Equals(row.HarnessId, harnessId, StringComparison.Ordinal)
                && string.Equals(row.SourceKey, sourceKey, StringComparison.Ordinal))
            .OrderBy(static row => row.SpanId, StringComparer.Ordinal)];

    /// <inheritdoc />
    public IReadOnlyList<CanonicalRecord> ListOtlpRecords(string? coveredFromUtc = null, string? coveredThroughUtc = null) =>
        [.. records
            .Where(static record => !record.SpanId.StartsWith("synth:", StringComparison.Ordinal))
            .Where(record => string.IsNullOrWhiteSpace(coveredFromUtc)
                || string.CompareOrdinal(record.Timestamp, coveredFromUtc) >= 0)
            .Where(record => string.IsNullOrWhiteSpace(coveredThroughUtc)
                || string.CompareOrdinal(record.Timestamp, coveredThroughUtc) <= 0)
            .OrderBy(static record => record.Timestamp, StringComparer.Ordinal)];

    /// <inheritdoc />
    public void CommitSourceUnit(
        SourceCheckpoint checkpoint,
        IReadOnlyList<CanonicalRecord> batch,
        IReadOnlyList<RecordProvenanceRow> provenances,
        IReadOnlyList<SpanProblemRow>? batchProblems = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(provenances);

        HashSet<string> batchIds = new(batch.Select(static r => r.SpanId), StringComparer.Ordinal);
        HashSet<(string SessionId, string Timestamp, string Name)> batchCalls = [];
        foreach (CanonicalRecord record in batch)
        {
            if (string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal))
            {
                batchCalls.Add((record.SessionId ?? string.Empty, record.Timestamp, record.Name));
            }
        }

        HashSet<string> unitSpanIds = new(
            provenance
                .Where(row => string.Equals(row.HarnessId, checkpoint.HarnessId, StringComparison.Ordinal)
                    && string.Equals(row.SourceKey, checkpoint.SourceKey, StringComparison.Ordinal))
                .Select(static row => row.SpanId),
            StringComparer.Ordinal);

        HashSet<string> doomedSpanIds = new(StringComparer.Ordinal);
        foreach (CanonicalRecord existing in records)
        {
            if (string.Equals(existing.Op, "llm.invoke", StringComparison.Ordinal)
                && !batchIds.Contains(existing.SpanId)
                && unitSpanIds.Contains(existing.SpanId)
                && batchCalls.Contains((existing.SessionId ?? string.Empty, existing.Timestamp, existing.Name))
                && existing.Raw is not null
                && existing.Raw.TryGetValue("provenance", out JsonElement provEl)
                && provEl.ValueKind == JsonValueKind.Object
                && provEl.TryGetProperty("recordDigest", out JsonElement digestEl)
                && digestEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(digestEl.GetString()))
            {
                _ = doomedSpanIds.Add(existing.SpanId);
            }
        }

        if (doomedSpanIds.Count > 0)
        {
            HashSet<string> doomedWithChildren = new(doomedSpanIds, StringComparer.Ordinal);
            foreach (CanonicalRecord r in records)
            {
                if (r.ParentSpanId is not null && doomedSpanIds.Contains(r.ParentSpanId))
                {
                    _ = doomedWithChildren.Add(r.SpanId);
                }
            }

            _ = records.RemoveAll(r => doomedWithChildren.Contains(r.SpanId));
            _ = provenance.RemoveAll(p => doomedWithChildren.Contains(p.SpanId));
        }

        PutRecords(batch);
        foreach (RecordProvenanceRow prov in provenances)
        {
            _ = provenance.RemoveAll(existing => string.Equals(existing.SpanId, prov.SpanId, StringComparison.Ordinal));
            provenance.Add(prov);
        }

        if (batchProblems is not null)
        {
            RecordProblems(batchProblems);
        }

        UpsertSourceCheckpoint(checkpoint);
    }

    /// <inheritdoc />
    public string StartRefreshRun(string id, string trigger, string startedAtUtc, int pid, int? historyWeeks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);
        ArgumentException.ThrowIfNullOrWhiteSpace(startedAtUtc);

        refreshRuns.RemoveAll(existing => string.Equals(existing.Id, id, StringComparison.Ordinal));
        refreshRuns.Add(new RefreshRunRow(
            Id: id,
            StartedAt: startedAtUtc,
            CompletedAt: null,
            Status: "running",
            Pid: pid,
            Trigger: trigger,
            Summary: null,
            HistoryWeeks: CanonSchema.NormalizeHistoryWeeks(historyWeeks)));
        return id;
    }

    /// <inheritdoc />
    public void CompleteRefreshRun(string id, string status, string completedAtUtc, string? summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedAtUtc);

        for (int i = 0; i < refreshRuns.Count; i++)
        {
            if (!string.Equals(refreshRuns[i].Id, id, StringComparison.Ordinal))
            {
                continue;
            }

            refreshRuns[i] = refreshRuns[i] with
            {
                CompletedAt = completedAtUtc,
                Status = status,
                Summary = summary,
            };
        }
    }

    /// <inheritdoc />
    public int ReconcileDeadRefreshRuns(string completedAtUtc, long maxAgeMs = 3_600_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(completedAtUtc);
        bool hasNow = DateTimeOffset.TryParse(completedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset nowDt);
        int updated = 0;

        for (int i = 0; i < refreshRuns.Count; i++)
        {
            RefreshRunRow row = refreshRuns[i];
            if (!string.Equals(row.Status, "running", StringComparison.Ordinal))
            {
                continue;
            }

            bool isAlive = false;
            if (row.Pid > 0)
            {
                try
                {
                    using Process process = Process.GetProcessById(row.Pid);
                    isAlive = !process.HasExited;
                }
                catch (ArgumentException)
                {
                    isAlive = false;
                }
                catch (InvalidOperationException)
                {
                    isAlive = false;
                }
            }

            long ageMs = 0;
            if (hasNow && DateTimeOffset.TryParse(row.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset startedDt))
            {
                ageMs = (long)(nowDt - startedDt).TotalMilliseconds;
            }

            if (isAlive && ageMs <= maxAgeMs)
            {
                continue;
            }

            string summary = !isAlive
                ? string.Create(CultureInfo.InvariantCulture, $"Refresh process {row.Pid} is no longer running (dead process); marked failed during reconciliation.")
                : string.Create(CultureInfo.InvariantCulture, $"Refresh process {row.Pid} exceeded maximum run duration ({Math.Round(ageMs / 1000.0)}s); marked failed during reconciliation.");
            refreshRuns[i] = row with
            {
                CompletedAt = completedAtUtc,
                Status = "failure",
                Summary = summary,
            };
            updated++;
        }

        return updated;
    }

    private void RecordDirty(long prevSeq, long nextSeq, HashSet<string> keys, bool unkeyed)
    {
        DirtyProjectionState existing = GetDirtyProjectionState();
        long fromSeq = prevSeq;
        bool wildcard = unkeyed;
        HashSet<string>? mergedKeys = unkeyed ? null : new HashSet<string>(keys, StringComparer.Ordinal);
        if (GetMetadata(CanonSchema.ProjectionDirtySessionsKey) is not null && existing.ToSeq == prevSeq)
        {
            fromSeq = existing.FromSeq;
            if (existing.IsWildcard)
            {
                wildcard = true;
                mergedKeys = null;
            }
            else if (!wildcard && existing.Keys is not null && mergedKeys is not null)
            {
                foreach (string existingKey in existing.Keys)
                {
                    mergedKeys.Add(existingKey);
                }
            }
        }

        string dirtyJson = wildcard
            ? JsonSerializer.Serialize(new { fromSeq, toSeq = nextSeq, keys = "*" })
            : JsonSerializer.Serialize(new { fromSeq, toSeq = nextSeq, keys = mergedKeys!.Order(StringComparer.Ordinal).ToArray() });
        metadata[CanonSchema.ProjectionDirtySessionsKey] = dirtyJson;
    }

    private static string? SessionKeyOf(CanonicalRecord record) =>
        !string.IsNullOrEmpty(record.SessionId)
            ? record.SessionId
            : !string.IsNullOrEmpty(record.TraceId)
                ? record.TraceId
                : null;

    private static string CheckpointKey(string harnessId, string sourceKey) =>
        string.Create(CultureInfo.InvariantCulture, $"{harnessId}\u001f{sourceKey}");
}

/// <summary>Shared serializer options for the test fakes.</summary>
internal static class JsonDefaults
{
    /// <summary>Camel-cased options matching the store's persisted JSON.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>In-memory <see cref="IPricingRepository"/> holding a hand-built rate table.</summary>
public class FakePricingRepository : IPricingRepository
{
    private readonly PricingTables tables;
    private readonly JsonElement catalog;

    /// <summary>Initializes a repository over the table built by <see cref="CanonPricing.BuildPricingTables"/>.</summary>
    /// <param name="snapshot">Published model rates.</param>
    /// <param name="fallback">Fallback model rates.</param>
    public FakePricingRepository(
        IReadOnlyDictionary<string, ModelCosts>? snapshot = null,
        IReadOnlyDictionary<string, ModelCosts>? fallback = null)
    {
        tables = CanonPricing.BuildPricingTables(
            snapshot ?? new Dictionary<string, ModelCosts>(StringComparer.Ordinal),
            fallback ?? new Dictionary<string, ModelCosts>(StringComparer.Ordinal));
        catalog = JsonSerializer.SerializeToElement(new { source = "fake", models = tables.SortedKeys.Count });
        CanonPricing.ConfigurePricingProvider(() => tables);
    }

    /// <summary>Number of times the tables were asked for.</summary>
    public int LoadCalls { get; private set; }

    /// <inheritdoc />
    public PricingTables LoadPricingTables()
    {
        LoadCalls++;
        return tables;
    }

    /// <inheritdoc />
    public JsonElement GetModelCatalogSnapshot() => catalog;
}

/// <summary>In-memory <see cref="ISettingsRepository"/> with the production decoding and bounds.</summary>
public class FakeSettingsRepository : ISettingsRepository
{
    private readonly Dictionary<string, string> metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> consents = new(StringComparer.Ordinal);
    private readonly List<JsonElement> statusLines = [];

    /// <summary>Boolean settings written, keyed by metadata key.</summary>
    public IReadOnlyDictionary<string, string> Metadata => metadata;

    /// <summary>Status-line events recorded so far.</summary>
    public IReadOnlyList<JsonElement> StatusLines => statusLines;

    /// <inheritdoc />
    public SettingsView ReadSettings() =>
        new(
            FolderImportScheduled: string.Equals(Get(SettingsService.FolderImportScheduledKey), "on", StringComparison.Ordinal),
            JobsPaused: string.Equals(Get(SettingsService.JobsPausedKey), "on", StringComparison.Ordinal),
            RefreshCadenceMinutes: DecodeBoundedInt(Get(SettingsService.RefreshCadenceMinutesKey), SettingsService.CadenceMinMinutes, SettingsService.CadenceMaxMinutes, 5),
            ReceiverHosted: string.Equals(Get(SettingsService.ReceiverHostedKey), "on", StringComparison.Ordinal),
            DashboardPort: DecodeBoundedInt(Get(SettingsService.DashboardPortKey), SettingsService.PortMin, SettingsService.PortMax, 4747));

    /// <inheritdoc />
    public void WriteBooleanSetting(string key, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        metadata[key] = value ? "on" : "off";
    }

    /// <inheritdoc />
    public void WriteIntSetting(string key, int value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        metadata[key] = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public bool ReadFactoryContentConsent(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        return consents.TryGetValue(Path.GetFullPath(stateDir), out bool enabled) && enabled;
    }

    /// <inheritdoc />
    public void WriteFactoryContentConsent(string stateDir, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        consents[Path.GetFullPath(stateDir)] = enabled;
    }

    /// <inheritdoc />
    public void AppendAntigravityStatusLineEvent(string stateDir, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        statusLines.Add(payload);
    }

    private string? Get(string key) => metadata.TryGetValue(key, out string? value) ? value : null;

    private static int DecodeBoundedInt(string? raw, int min, int max, int fallback) =>
        raw is not null
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= min
            && parsed <= max
            ? parsed
            : fallback;
}

/// <summary><see cref="IServerStateRepository"/> recording publication and returns configurable probes.</summary>
public class FakeServerStateRepository : IServerStateRepository
{
    private readonly Dictionary<string, ServerInfo> serverInfo = new(StringComparer.Ordinal);

    /// <summary>Caller-supplied state directory reported as the default.</summary>
    public FakeServerStateRepository(string? defaultStateDirectory = null) =>
        DefaultStateDirectory = defaultStateDirectory ?? Path.Combine(Path.GetTempPath(), "kyberdash-fake-state");

    /// <inheritdoc />
    public string DefaultStateDirectory { get; }

    /// <summary>Probe result returned by <see cref="ProbeEngineAsync"/>.</summary>
    public (EngineProbeState State, EngineDescriptor? Holder) ProbeResult { get; set; } = (EngineProbeState.NotRunning, null);

    /// <summary>Whether <see cref="RequestEngineShutdownAsync"/> reports success.</summary>
    public bool ShutdownAccepted { get; set; } = true;

    /// <summary>Number of shutdown requests received.</summary>
    public int ShutdownRequests { get; private set; }

    /// <summary>Probe results returned in order, falling back to <see cref="ProbeResult"/> when exhausted.</summary>
    public Queue<(EngineProbeState State, EngineDescriptor? Holder)> ProbeSequence { get; } = new();

    /// <summary>Whether the jobs lease is available.</summary>
    public bool JobsLeaseAvailable { get; set; } = true;

    /// <summary>Number of times the jobs lease was requested.</summary>
    public int JobsLeaseRequests { get; private set; }

    /// <inheritdoc />
    public ServerInfo? ReadServerInfo(string stateDir) =>
        serverInfo.TryGetValue(Path.GetFullPath(stateDir), out ServerInfo? info) ? info : null;

    /// <inheritdoc />
    public void WriteServerInfo(string stateDir, ServerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        serverInfo[Path.GetFullPath(stateDir)] = info;
    }

    /// <inheritdoc />
    public void DeleteServerInfo(string stateDir) => _ = serverInfo.Remove(Path.GetFullPath(stateDir));

    /// <inheritdoc />
    public EngineDescriptor? ReadEngineDescriptor(string stateDir) => null;

    /// <inheritdoc />
    public void WriteEngineDescriptor(string stateDir, EngineDescriptor descriptor)
    {
    }

    /// <inheritdoc />
    public void DeleteEngineDescriptor(string stateDir)
    {
    }

    /// <inheritdoc />
    public Task<(EngineProbeState State, EngineDescriptor? Holder)> ProbeEngineAsync(
        string stateDir,
        CancellationToken cancellationToken = default)
    {
        _ = stateDir;
        _ = cancellationToken;
        return Task.FromResult(ProbeSequence.Count > 0 ? ProbeSequence.Dequeue() : ProbeResult);
    }

    /// <inheritdoc />
    public Task<bool> RequestEngineShutdownAsync(string stateDir, CancellationToken cancellationToken = default)
    {
        _ = stateDir;
        _ = cancellationToken;
        ShutdownRequests++;
        return Task.FromResult(ShutdownAccepted);
    }

    /// <inheritdoc />
    public Task<(IEngineOwnerLease? Lease, EngineDescriptor? ExistingHolder)> TryAcquireEngineAsync(
        string stateDir,
        string version,
        string exePath,
        Action onShutdownRequested,
        CancellationToken cancellationToken = default)
    {
        _ = version;
        _ = exePath;
        _ = onShutdownRequested;
        _ = cancellationToken;
        return Task.FromResult<(IEngineOwnerLease?, EngineDescriptor?)>((null, ProbeResult.Holder));
    }

    /// <inheritdoc />
    public IDisposable? TryAcquireJobsLease(string stateDir)
    {
        _ = stateDir;
        JobsLeaseRequests++;
        return JobsLeaseAvailable ? new NoopLease() : null;
    }

    /// <summary>A lease handle that does nothing, standing in for the lock file.</summary>
    public class NoopLease : IDisposable
    {
        /// <summary>Whether the lease was released.</summary>
        public bool Released { get; private set; }

        /// <inheritdoc />
        public void Dispose()
        {
            Released = true;
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary><see cref="ITranscriptReaderRepository"/> returning prepared sources and envelopes.</summary>
public class FakeTranscriptReaderRepository : ITranscriptReaderRepository
{
    /// <summary>Sources returned by <see cref="DiscoverLocalSources"/>.</summary>
    public IReadOnlyList<RefreshSourceInput> Sources { get; set; } = [];

    /// <summary>Envelope lookbacks requested by the caller.</summary>
    public List<DateTimeOffset?> DiscoverCalls { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<ClaudeTurnGroup> ReadClaudeTurns(
        string filePath,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null)
    {
        _ = filePath;
        _ = windowStart;
        _ = windowEnd;
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceRecordEnvelope> ReadClaudeEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null)
    {
        _ = filePath;
        _ = sourceKey;
        _ = sourceRevision;
        _ = importedAt;
        _ = windowStart;
        _ = windowEnd;
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<ReaderTurn> ReadCodexTurns(string filePath)
    {
        _ = filePath;
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceRecordEnvelope> ReadCodexEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null)
    {
        _ = filePath;
        _ = sourceKey;
        _ = sourceRevision;
        _ = importedAt;
        _ = windowStart;
        _ = windowEnd;
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<RefreshSourceInput> DiscoverLocalSources(DateTimeOffset? sinceUtc = null)
    {
        DiscoverCalls.Add(sinceUtc);
        return Sources;
    }
}
