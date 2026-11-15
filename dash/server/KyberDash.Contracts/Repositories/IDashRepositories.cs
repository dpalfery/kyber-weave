using System.Text.Json;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Contracts.Repositories;

/// <summary>Canonical SQLite store persistence and query contract accepting and returning Domain DTOs only.</summary>
public interface ICanonStoreRepository
{
    /// <summary>Resolved filesystem path to the underlying <c>canon.db</c> file.</summary>
    string DatabasePath { get; }

    /// <summary>Ensures the store file, schema v24 tables, and indexes exist and are compatible.</summary>
    void EnsureInitialized();

    /// <summary>Gets a metadata string value by key, or <see langword="null"/> when absent.</summary>
    string? GetMetadata(string key);

    /// <summary>Upserts a metadata key/value pair.</summary>
    void SetMetadata(string key, string value);

    /// <summary>Atomically replaces a metadata value when the current value matches <paramref name="expected"/>.</summary>
    bool CompareAndSwapMetadata(string key, string? expected, string? newValue);

    /// <summary>Returns the monotonic write sequence of the <c>records</c> table.</summary>
    long RecordsWriteMarker();

    /// <summary>Returns total count of persisted canonical records.</summary>
    int CountRecords();

    /// <summary>Lists distinct normalized harness names observed across records.</summary>
    IReadOnlyList<string> ListObservedHarnesses();

    /// <summary>Persists one canonical record idempotently.</summary>
    void PutRecord(CanonicalRecord record);

    /// <summary>Persists a batch of canonical records in one transaction.</summary>
    void PutRecords(IEnumerable<CanonicalRecord> records);

    /// <summary>Reads one canonical record by <paramref name="spanId"/>.</summary>
    CanonicalRecord? GetRecord(string spanId);

    /// <summary>Returns whether a canonical record with <paramref name="spanId"/> exists.</summary>
    bool HasRecord(string spanId);

    /// <summary>Returns all persisted canonical records.</summary>
    IReadOnlyList<CanonicalRecord> QueryAllRecords();

    /// <summary>Returns all canonical records belonging to a session or trace key ordered by timestamp.</summary>
    IReadOnlyList<CanonicalRecord> ListRecordsBySessionKey(string sessionKey);

    /// <summary>Updates persisted <c>cost_json</c> blocks keyed by <c>span_id</c>.</summary>
    void SetCosts(IReadOnlyDictionary<string, CostBlock> updates);

    /// <summary>Moves an invalid or unclaimed span into <c>quarantine</c> and removes it from <c>records</c>/<c>problems</c>.</summary>
    void QuarantineAndDelete(QuarantineRow row);

    /// <summary>Lists quarantined spans optionally filtered by harness.</summary>
    IReadOnlyList<QuarantineRow> GetQuarantined(string? harness = null);

    /// <summary>Persists one span problem row.</summary>
    void RecordProblem(SpanProblemRow problem);

    /// <summary>Persists a batch of span problem rows.</summary>
    void RecordProblems(IEnumerable<SpanProblemRow> problems);

    /// <summary>Reads problems optionally filtered by <paramref name="spanId"/>.</summary>
    IReadOnlyList<SpanProblemRow> GetProblems(string? spanId = null);

    /// <summary>Records an ingest batch audit entry.</summary>
    void RecordIngest(string source, int count, string timestamp);

    /// <summary>Lists ingest audit entries optionally filtered by source.</summary>
    IReadOnlyList<IngestLogEntry> GetIngestLog(string? source = null);

    /// <summary>Looks up a cached token count by content+model SHA-256 hash.</summary>
    long? GetCachedTokenCount(string hash);

    /// <summary>Persists computed token counts keyed by SHA-256 hash.</summary>
    void PutCachedTokenCounts(IReadOnlyDictionary<string, long> counts, string model);

    /// <summary>Upserts one projected session and its turn rows.</summary>
    void PutSession(SessionSummaryRow row);

    /// <summary>Upserts a batch of projected sessions and turn rows.</summary>
    void PutSessions(IEnumerable<SessionSummaryRow> rows);

    /// <summary>Reads a single projected session summary and payload.</summary>
    SessionSummaryRow? GetSession(string sessionId);

    /// <summary>Lists projected sessions optionally filtered by harness.</summary>
    IReadOnlyList<SessionSummaryRow> ListSessions(string? harness = null);

    /// <summary>Deletes one projected session and its turn rows.</summary>
    void DeleteSession(string sessionId);

    /// <summary>Deletes multiple projected sessions and their turn rows.</summary>
    void DeleteSessions(IEnumerable<string> sessionIds);

    /// <summary>Reads persisted turn JSON entries for <paramref name="sessionId"/> ordered by index.</summary>
    IReadOnlyList<(int TurnIndex, string TurnJson)> GetSessionTurns(string sessionId);

    /// <summary>Reads dirty projection marker state.</summary>
    DirtyProjectionState GetDirtyProjectionState();

    /// <summary>Records clean projection watermark and schema version.</summary>
    void MarkProjectionClean(long writeSeq, string schemaVersion);

    /// <summary>Reads the canonical session-key-to-session-id map.</summary>
    IReadOnlyDictionary<string, string> GetProjectionSessionKeyMap();

    /// <summary>Persists the canonical session-key-to-session-id map.</summary>
    void SaveProjectionSessionKeyMap(IReadOnlyDictionary<string, string> map);

    /// <summary>Upserts one diagnostic finding.</summary>
    void PutFinding(Finding finding);

    /// <summary>Upserts a batch of diagnostic findings.</summary>
    void PutFindings(IEnumerable<Finding> findings);

    /// <summary>Reads one finding by ID.</summary>
    Finding? GetFinding(string id);

    /// <summary>Lists findings filtered by run ID and/or session ID.</summary>
    IReadOnlyList<Finding> ListFindings(string? runId = null, string? sessionId = null);

    /// <summary>Deletes findings associated with <paramref name="sessionId"/>.</summary>
    void DeleteFindingsForSession(string sessionId);

    /// <summary>Upserts derived or explicit run rows.</summary>
    void UpsertRuns(IEnumerable<RunRow> runs);

    /// <summary>Reads a single run row by ID.</summary>
    RunRow? GetRun(string runId);

    /// <summary>Lists run rows optionally filtered by harness.</summary>
    IReadOnlyList<RunRow> ListRuns(string? harness = null);

    /// <summary>Upserts agent execution rows.</summary>
    void UpsertExecutions(IEnumerable<ExecutionRow> executions);

    /// <summary>Lists execution rows belonging to a run.</summary>
    IReadOnlyList<ExecutionRow> ListExecutionsForRun(string runId);

    /// <summary>Lists execution rows belonging to a harness.</summary>
    IReadOnlyList<ExecutionRow> ListExecutionsByHarness(string harness);

    /// <summary>Upserts per-harness efficiency rollups.</summary>
    void UpsertHarnessRollups(IEnumerable<HarnessRollupRow> rollups);

    /// <summary>Reads one harness rollup by harness identifier.</summary>
    HarnessRollupRow? GetHarnessRollup(string harness);

    /// <summary>Lists all per-harness efficiency rollups.</summary>
    IReadOnlyList<HarnessRollupRow> ListHarnessRollups();

    /// <summary>Persists a prediction/calibration row.</summary>
    void PutPrediction(PredictionRow prediction);

    /// <summary>Lists prediction rows optionally filtered by run ID.</summary>
    IReadOnlyList<PredictionRow> ListPredictions(string? runId = null);

    /// <summary>Lists stored problem rows with database IDs.</summary>
    IReadOnlyList<StoredProblemRow> ListStoredProblems(string? harness = null, string? spanId = null);

    /// <summary>Reads one stored problem row by row ID.</summary>
    StoredProblemRow? GetStoredProblem(int id);

    /// <summary>Lists quarantined rows ordered newest first.</summary>
    IReadOnlyList<QuarantineRow> ListQuarantine(string? harness = null);

    /// <summary>Reads one quarantined row by <paramref name="spanId"/>.</summary>
    QuarantineRow? GetQuarantineItem(string spanId);

    /// <summary>Reads a harness source-unit checkpoint.</summary>
    SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey);

    /// <summary>Upserts a harness source-unit checkpoint.</summary>
    void UpsertSourceCheckpoint(SourceCheckpoint checkpoint);

    /// <summary>Lists source checkpoints optionally filtered by harness ID.</summary>
    IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null);

    /// <summary>Persists record provenance entries idempotently.</summary>
    void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows);

    /// <summary>Inserts or updates a refresh run audit record.</summary>
    void RecordRefreshRun(RefreshRunRow row);

    /// <summary>Lists recent refresh run audit records.</summary>
    IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20);

    /// <summary>Stores decoded OTLP logs waiting for span enrichment.</summary>
    void StorePendingLogs(IEnumerable<OtlpLog> logs, string? harness = null);

    /// <summary>Reads pending OTLP logs for correlation.</summary>
    IReadOnlyList<OtlpLog> TakePendingLogs(int limit = 500);

    /// <summary>Moves unmatched or malformed OTLP logs to quarantine.</summary>
    void StoreQuarantinedLogs(IEnumerable<(OtlpLog Log, string Reason)> logs, string? harness = null);

    /// <summary>Checks whether a log identifier has already enriched a span.</summary>
    bool IsLogEnriched(string logId);

    /// <summary>Records log identifiers that have enriched spans.</summary>
    void MarkLogsEnriched(IEnumerable<string> logIds);

    /// <summary>Persists decoded OTLP metric points.</summary>
    void StoreMetrics(IEnumerable<OtlpMetric> metrics, string? source = null);

    /// <summary>Clears content columns for records older than <paramref name="cutoffUtc"/> while keeping token/cost metrics.</summary>
    int PurgeContentBefore(string cutoffUtc);

    /// <summary>Wipes all telemetry, projections, findings, and rollups cleanly.</summary>
    StoreWipeReport WipeAll();

    /// <summary>Wipes telemetry, projections, findings, and rollups for the specified harnesses.</summary>
    StoreWipeReport WipeHarnesses(IEnumerable<string> harnesses);

    /// <summary>Runs SQLite incremental vacuum / checkpoint maintenance.</summary>
    void Vacuum();

    /// <summary>Returns all currently stored session identifiers.</summary>
    IReadOnlyList<string> GetBuiltSessionIds();

    /// <summary>Returns distinct non-null session keys (<c>COALESCE(session_id, trace_id)</c>) ordered newest-started first.</summary>
    IReadOnlyList<string> GetDistinctSessionKeys();

    /// <summary>Returns distinct <c>(sessionKey, harness)</c> pairs present in <c>records</c>.</summary>
    IReadOnlyList<(string Key, string Harness)> GetSessionKeyHarnesses();

    /// <summary>Returns whether the store's stamped detector version matches <see cref="CanonSchema.DetectorVersion"/>.</summary>
    bool HasCurrentDetectorVersion();

    /// <summary>Stamps the detector version into metadata.</summary>
    void SetDetectorVersion(int version = CanonSchema.DetectorVersion);

    /// <summary>Reads the map of session keys to built session IDs, or <see langword="null"/> when absent or invalid.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GetProjectedSessionKeyMap();

    /// <summary>Clears dirty-session keys at <paramref name="seq"/> and stores the built session-ID map atomically.</summary>
    void RecordProjectedSessionKeys(long seq, IReadOnlyDictionary<string, IReadOnlyList<string>> sessionIdsByKey);

    /// <summary>Returns stored record counts grouped by non-empty <c>source</c> column.</summary>
    IReadOnlyDictionary<string, int> CountRecordsBySource();

    /// <summary>Returns the maximum SQLite <c>rowid</c> in <c>records</c>, or <see langword="null"/> if empty.</summary>
    long? GetMaxRecordRowId();

    /// <summary>Computes the deterministic 32-bit FNV-1a store generation over wipe stamp, count, and high-water mark.</summary>
    uint ComputeStoreGeneration();

    /// <summary>Loads decompressed turn payloads ordered by <c>turn_index ASC</c> for <paramref name="sessionId"/>.</summary>
    IReadOnlyList<JsonElement> ListSessionTurnPayloads(string sessionId);

    /// <summary>Loads one decompressed turn payload by <paramref name="sessionId"/> and <paramref name="turnIndex"/>.</summary>
    JsonElement? GetSessionTurnPayload(string sessionId, int turnIndex);

    /// <summary>Deletes a stale harness rollup row.</summary>
    void DeleteHarnessRollup(string harness);

    /// <summary>Counts quarantined spans optionally filtered by <paramref name="harness"/>.</summary>
    int CountQuarantine(string? harness = null);

    /// <summary>Lists quarantined spans ordered newest-first, with optional harness filter and pagination.</summary>
    IReadOnlyList<QuarantineRow> ListQuarantinePage(string? harness = null, int? limit = null, int? offset = null);

    /// <summary>Counts recorded problems optionally filtered by <paramref name="harness"/>.</summary>
    int CountProblems(string? harness = null);

    /// <summary>Loads a single problem row by numeric identifier, or <see langword="null"/> if absent.</summary>
    StoredProblemRow? GetProblemById(int id);

    /// <summary>Lists problems ordered newest-first with optional harness filter and pagination.</summary>
    IReadOnlyList<StoredProblemRow> ListProblems(string? harness = null, int? limit = null, int? offset = null);

    /// <summary>Lists persisted <see cref="RecordProvenanceRow"/> entries for one source unit.</summary>
    IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey);

    /// <summary>
    /// Loads non-<c>synth:</c> (OTLP-ingested) records from <c>records</c> for cross-path
    /// deduplication, optionally restricted to a covered interval so a long history is not
    /// materialized on every refresh.
    /// </summary>
    IReadOnlyList<CanonicalRecord> ListOtlpRecords(string? coveredFromUtc = null, string? coveredThroughUtc = null);

    /// <summary>Atomically commits one checkpointed source unit inside a single store transaction.</summary>
    void CommitSourceUnit(
        SourceCheckpoint checkpoint,
        IReadOnlyList<CanonicalRecord> records,
        IReadOnlyList<RecordProvenanceRow> provenances,
        IReadOnlyList<SpanProblemRow>? problems = null);

    /// <summary>Inserts a new <c>running</c> row into <c>refresh_run</c> and returns <paramref name="id"/>.</summary>
    string StartRefreshRun(string id, string trigger, string startedAtUtc, int pid, int? historyWeeks = null);

    /// <summary>Closes an existing <c>refresh_run</c> row with status, completion timestamp, and summary.</summary>
    void CompleteRefreshRun(string id, string status, string completedAtUtc, string? summary);

    /// <summary>Marks interrupted or stale <c>running</c> rows in <c>refresh_run</c> as <c>failure</c>.</summary>
    int ReconcileDeadRefreshRuns(string completedAtUtc, long maxAgeMs = 3_600_000);
}

/// <summary>Streaming transcript reader contract decoupling JSONL filesystem scanning from application synthesis.</summary>
public interface ITranscriptReaderRepository
{
    /// <summary>Reads Claude Code JSONL transcript turns from <paramref name="filePath"/>.</summary>
    IReadOnlyList<ClaudeTurnGroup> ReadClaudeTurns(
        string filePath,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null);

    /// <summary>Reads Claude Code JSONL transcript turns as classified <see cref="SourceRecordEnvelope"/> entries.</summary>
    IReadOnlyList<SourceRecordEnvelope> ReadClaudeEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null);

    /// <summary>Reads Codex JSONL transcript turns from <paramref name="filePath"/>.</summary>
    IReadOnlyList<ReaderTurn> ReadCodexTurns(string filePath);

    /// <summary>Reads Codex JSONL transcript turns as classified <see cref="SourceRecordEnvelope"/> entries.</summary>
    IReadOnlyList<SourceRecordEnvelope> ReadCodexEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null);

    /// <summary>Discovers local harness transcript sources within <paramref name="sinceUtc"/> lookback.</summary>
    IReadOnlyList<RefreshSourceInput> DiscoverLocalSources(DateTimeOffset? sinceUtc = null);
}

/// <summary>Model rate-table repository contract separating embedded snapshot loading from pricing math.</summary>
public interface IPricingRepository
{
    /// <summary>Loads or returns cached published and fallback model rate tables.</summary>
    PricingTables LoadPricingTables();

    /// <summary>Returns catalog refresh status metadata persisted alongside model context windows.</summary>
    JsonElement GetModelCatalogSnapshot();
}

/// <summary>Operator settings and local plugin consent persistence contract.</summary>
public interface ISettingsRepository
{
    /// <summary>Reads shared operator settings from <see cref="ICanonStoreRepository"/>.</summary>
    SettingsView ReadSettings();

    /// <summary>Persists a validated boolean setting.</summary>
    void WriteBooleanSetting(string key, bool value);

    /// <summary>Persists a validated integer setting.</summary>
    void WriteIntSetting(string key, int value);

    /// <summary>Reads Factory plugin content-capture consent from <c>factory-plugin.json</c>.</summary>
    bool ReadFactoryContentConsent(string stateDir);

    /// <summary>Writes Factory plugin content-capture consent to <c>factory-plugin.json</c>.</summary>
    void WriteFactoryContentConsent(string stateDir, bool enabled);

    /// <summary>Appends one Antigravity statusline event line to the local JSONL cache.</summary>
    void AppendAntigravityStatusLineEvent(string stateDir, JsonElement payload);
}

/// <summary>Process coordination, descriptor publication, and exclusive lock lease repository contract.</summary>
public interface IServerStateRepository
{
    /// <summary>Default state directory path (<c>~/.kyberdash</c>).</summary>
    string DefaultStateDirectory { get; }

    /// <summary>Reads <c>server.json</c> from <paramref name="stateDir"/> when present and valid.</summary>
    ServerInfo? ReadServerInfo(string stateDir);

    /// <summary>Writes <c>server.json</c> atomically with <c>0600</c> permissions and symlink refusal.</summary>
    void WriteServerInfo(string stateDir, ServerInfo info);

    /// <summary>Deletes <c>server.json</c> when present.</summary>
    void DeleteServerInfo(string stateDir);

    /// <summary>Reads <c>engine.json</c> from <paramref name="stateDir"/> when present.</summary>
    EngineDescriptor? ReadEngineDescriptor(string stateDir);

    /// <summary>Writes <c>engine.json</c> atomically with <c>0600</c> permissions and symlink refusal.</summary>
    void WriteEngineDescriptor(string stateDir, EngineDescriptor descriptor);

    /// <summary>Deletes <c>engine.json</c> when present.</summary>
    void DeleteEngineDescriptor(string stateDir);

    /// <summary>Probes whether a running <c>kyberdash-api</c> instance answers on its control endpoint.</summary>
    Task<(EngineProbeState State, EngineDescriptor? Holder)> ProbeEngineAsync(
        string stateDir,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a graceful shutdown request to the live <c>kyberdash-api</c> instance.</summary>
    Task<bool> RequestEngineShutdownAsync(
        string stateDir,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts to acquire the single-engine IPC socket lock.</summary>
    Task<(IEngineOwnerLease? Lease, EngineDescriptor? ExistingHolder)> TryAcquireEngineAsync(
        string stateDir,
        string version,
        string exePath,
        Action onShutdownRequested,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts to acquire the exclusive <c>jobs.lock</c> file lease.</summary>
    IDisposable? TryAcquireJobsLease(string stateDir);
}

/// <summary>Static dashboard SPA asset loader contract supporting disk override and embedded bundles.</summary>
public interface IWebAssetRepository
{
    /// <summary>Attempts to resolve a static file or SPA index asset for <paramref name="requestPath"/>.</summary>
    WebAssetDocument? TryResolveAsset(string requestPath, string? overrideDir = null);
}
