using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberDash.Domain;

// Ported DTOs from CanonStore.cs
public record SessionSummaryRow(
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
public record DirtyProjectionState(
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

// Ported DTOs from CanonStore.ServerQueries.cs
/// <summary>Summary counts returned by a full or harness-scoped store wipe.</summary>
public record StoreWipeReport(
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
public record RunRow(
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
public record ExecutionRow(
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
public record HarnessRollupRow(
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
public record PredictionRow(
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
public record StoredProblemRow(
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
public record SourceCheckpoint(
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
public record RecordProvenanceRow(
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
public record RefreshRunRow(
    string Id,
    string StartedAt,
    string? CompletedAt,
    string Status,
    int Pid,
    string Trigger,
    string? Summary,
    int? HistoryWeeks);


// Ported DTOs from CanonClean.cs
/// <summary>Requested clean scope and optional history backfill window.</summary>
public record CleanScope(
    bool All = false,
    IReadOnlyList<string>? Harnesses = null,
    int? ReingestWeeks = null);

/// <summary>Result summary returned after a validated database clean.</summary>
public record CleanReport(
    IReadOnlyList<string> Harnesses,
    StoreWipeReport Wipe,
    bool Reingested,
    int? HistoryWeeks);


// Ported DTOs from CanonSettings.cs
/// <summary>
/// Typed view of operator settings persisted in <c>canon.db</c> <c>metadata</c>.
/// </summary>
public record SettingsView(
    [property: JsonPropertyName("folderImportScheduled")] bool FolderImportScheduled,
    [property: JsonPropertyName("jobsPaused")] bool JobsPaused,
    [property: JsonPropertyName("refreshCadenceMinutes")] int RefreshCadenceMinutes,
    [property: JsonPropertyName("receiverHosted")] bool ReceiverHosted,
    [property: JsonPropertyName("dashboardPort")] int DashboardPort);


// Ported DTOs from CanonIngest.cs
/// <summary>
/// Per-batch ingest counters returned by a canonical ingest batch.
/// </summary>
/// <param name="Accepted">Number of validated canonical records persisted to <c>records</c>.</param>
/// <param name="Quarantined">Number of unclaimed, non-model, or unsupported-version spans routed to <c>quarantine</c>.</param>
/// <param name="Rejected">Number of claimed spans whose normalization threw or token validation failed.</param>
public record IngestOutcome(int Accepted, int Quarantined, int Rejected);


// Ported DTOs from CanonRefresh.cs
/// <summary>Half-open or closed UTC interval for checkpoint coverage arithmetic.</summary>
/// <param name="FromUtc">ISO-8601 UTC start bound.</param>
/// <param name="ThroughUtc">ISO-8601 UTC end bound.</param>
public record CoverageInterval(string FromUtc, string ThroughUtc);

/// <summary>Coverage request parameters identifying revision and parser contract.</summary>
/// <param name="RevisionToken">Current revision fingerprint of the native source unit.</param>
/// <param name="ParserContractVersion">Parser contract version expected by the caller.</param>
public record CoverageRequest(string RevisionToken, string ParserContractVersion);

/// <summary>Result returned by a retention purge pass.</summary>
/// <param name="Purged">Number of records whose content columns were cleared.</param>
/// <param name="CutoffUtc">ISO-8601 UTC retention cutoff applied by the pass.</param>
public record PurgeExpiredContentResult(int Purged, string CutoffUtc);

/// <summary>Declarative input describing one refreshable native source unit.</summary>
public record RefreshSourceInput
{
    /// <summary>Canonical harness identifier (for example <c>"claude-code"</c>, <c>"codex-cli"</c>).</summary>
    public required string HarnessId { get; init; }

    /// <summary>Stable unit source key within <see cref="HarnessId"/>.</summary>
    public required string SourceKey { get; init; }

    /// <summary>Upstream provider identifier (for example <c>"claude"</c>, <c>"codex"</c>).</summary>
    public required string ProviderId { get; init; }

    /// <summary>Parser implementation identifier.</summary>
    public required string ParserId { get; init; }

    /// <summary>Parser contract version; changing this invalidates prior checkpoints.</summary>
    public required string ParserContractVersion { get; init; }

    /// <summary>Source format string (for example <c>"claude-jsonl"</c>, <c>"codex-jsonl"</c>).</summary>
    public required string Format { get; init; }

    /// <summary>Human-readable root label (non-PII relative or logical source name).</summary>
    public required string SourceRootLabel { get; init; }

    /// <summary>Revision fingerprint token of the source unit.</summary>
    public required string RevisionToken { get; init; }

    /// <summary>Lazy loader returning source envelopes for synthesis when the checkpoint is stale or incomplete.</summary>
    public required Func<IReadOnlyList<SourceRecordEnvelope>> LoadEnvelopes { get; init; }
}

/// <summary>Per-source outcome row recorded inside <see cref="RefreshReport"/>.</summary>
public record RefreshSourceStatusRow(
    string HarnessId,
    string SourceKey,
    string Status,
    int RecordsWritten,
    string? ErrorMessage = null);

/// <summary>Summary returned by a harness source refresh.</summary>
public record RefreshReport(
    string RunId,
    int HistoryWeeks,
    string CommandStartedAt,
    IReadOnlyList<RefreshSourceStatusRow> Rows,
    int PurgedContentCount,
    int Sessions,
    int Runs,
    int Executions,
    int Rollups,
    int ExitCode);


// Ported DTOs from CanonSessionProjector.cs
/// <summary>Summary returned by an incremental projection pass.</summary>
/// <param name="DeferredKeys">Session keys a read-path pass deferred under its record-count budget; they stay dirty and <see langword="null"/> on every other pass.</param>
public record ProjectionPassReport(
    int Built,
    int Skipped,
    int Pruned,
    int Findings,
    IReadOnlyList<string> RebuiltKeys,
    IReadOnlyList<string> SkippedKeys,
    IReadOnlyList<string>? DeferredKeys = null);


// Ported DTOs from CanonRollups.cs
/// <summary>Derived or explicit run grouping result produced under Decision D13.</summary>
public record DerivedRunIdentity(
    string RunId,
    string GroupingBasis,
    string GroupingRule,
    string? WorkingDirectory,
    OutcomeBlock Outcome);


// Ported DTOs from BoundedIngestWriter.cs
/// <summary>
/// Result of attempting to admit an OTLP span or log batch into the ingest buffer.
/// </summary>
/// <param name="Accepted"><see langword="true"/> when queued; <see langword="false"/> when rejected by payload or backlog caps.</param>
/// <param name="HttpStatusCode">HTTP status code to return (<c>200</c> accepted, <c>413</c> permanently oversize, <c>503</c> transient backpressure).</param>
/// <param name="RetryAfterSeconds">Seconds advertised in <c>Retry-After</c> when <paramref name="HttpStatusCode"/> is <c>503</c>.</param>
/// <param name="ErrorMessage">Diagnostic explanation when rejected.</param>
public record IngestAdmissionResult(
    bool Accepted,
    int HttpStatusCode,
    int? RetryAfterSeconds = null,
    string? ErrorMessage = null);


// Ported DTOs from KyberJobHost.cs
/// <summary>Published local server endpoint descriptor written to <c>server.json</c>.</summary>
public record ServerInfo(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("startedAt")] string StartedAt,
    [property: JsonPropertyName("url")] Uri? Url = null,
    [property: JsonPropertyName("apiVersion")] int ApiVersion = 1);

/// <summary>Snapshot of the in-process background job host state.</summary>
public record JobHostStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("lastSuccessAt")] string? LastSuccessAt,
    [property: JsonPropertyName("lastFailure")] string? LastFailure,
    [property: JsonPropertyName("nextDueAt")] string? NextDueAt,
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("hostedElsewhere")] bool HostedElsewhere,
    [property: JsonPropertyName("lastMaintenanceFailure")] string? LastMaintenanceFailure,
    [property: JsonPropertyName("lastSyncFailure")] string? LastSyncFailure,
    [property: JsonPropertyName("lastFailedUnits")] int LastFailedUnits = 0);

/// <summary>
/// Result returned by an in-process job trigger (<c>succeeded</c>, <c>partial</c>, <c>busy</c>, <c>failed</c>, <c>declined</c>, <c>hosted-elsewhere</c>).
/// <c>partial</c> means the pass completed but <see cref="FailedUnits"/> source units failed and were skipped.
/// </summary>
public record JobOutcome(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null,
    [property: JsonPropertyName("failedUnits")] int FailedUnits = 0);


// Ported DTOs from CanonTranscriptReaders.cs
/// <summary>Result of parsing a single Claude Code turn group from a JSONL transcript.</summary>
public record ClaudeTurnGroup(
    string? SessionId,
    string? NativeRecordId,
    string Timestamp,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    ReaderTurn Turn,
    string? SessionTitle = null);


// Additional DTOs for Clean Architecture presentation/application contracts

/// <summary>Published listening state emitted when the HTTP/OTLP API host binds.</summary>
public record ListeningEndpointInfo(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("url")] Uri Url,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("apiVersion")] int ApiVersion,
    [property: JsonPropertyName("otlpUrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Uri? OtlpUrl = null);

/// <summary>Embedded or disk web asset payload served by the dashboard host.</summary>
/// <param name="ContentType">MIME type the response is served with.</param>
/// <param name="Content">The asset bytes.</param>
/// <param name="IsCompressed">Whether <paramref name="Content"/> is already content-encoded.</param>
/// <param name="ResolvedPath">
/// Normalized relative path of the asset actually returned, which is <c>index.html</c> when a
/// client-side route was answered with the app shell. A request for a file answered with anything
/// but that file is a missing asset, so the host needs this to answer 404 instead of HTML.
/// </param>
public record WebAssetDocument(
    string ContentType,
    ReadOnlyMemory<byte> Content,
    bool IsCompressed = false,
    string ResolvedPath = "index.html");

/// <summary>Result of evaluating or applying an analytics operation.</summary>
public record AnalyticsExecutionResult(
    int StatusCode,
    JsonElement Envelope);

/// <summary>
/// Rebuild summary of the most recent automatic destructive older-schema store rebuild, mirroring
/// the data layer's <c>StoreRebuildInfo</c> without taking a data-layer reference.
/// </summary>
/// <param name="DatabasePath">Full path to the rebuilt SQLite database file.</param>
/// <param name="OldSchemaVersion">Schema version discovered in the pre-existing store.</param>
/// <param name="NewSchemaVersion">Schema version stamped after the rebuild.</param>
/// <param name="SettingsResetToDefaults">Whether operator settings were reset to defaults.</param>
/// <param name="UnrecoverableHistoryClasses">History classes that could not be preserved across the rebuild.</param>
/// <param name="ReingestTriggered">Whether the post-rebuild source re-ingest has already been triggered.</param>
public record StoreRebuildSummary(
    string DatabasePath,
    int OldSchemaVersion,
    int NewSchemaVersion,
    bool SettingsResetToDefaults,
    IReadOnlyList<string> UnrecoverableHistoryClasses,
    bool ReingestTriggered);

/// <summary>Diagnostics summary returned by the doctor service.</summary>
/// <param name="LastRebuild">Summary of the most recent destructive older-schema rebuild, or <see langword="null"/> when none occurred.</param>
public record DoctorDiagnosticsReport(
    string DatabasePath,
    int Records,
    int Sessions,
    IReadOnlyList<string> ObservedHarnesses,
    string CircuitBreakerState,
    StoreRebuildSummary? LastRebuild = null);

/// <summary>Engine liveness status returned by engine probe/ensure.</summary>
public enum EngineProbeState
{
    /// <summary>No engine is currently holding the socket or descriptor.</summary>
    NotRunning,

    /// <summary>An engine answered on the control endpoint.</summary>
    Running,

    /// <summary>A live or stale control descriptor did not answer.</summary>
    Unresponsive,
}

/// <summary>Persisted engine descriptor read from <c>engine.json</c> and <c>engine.sock</c>.</summary>
public record EngineDescriptor(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("exe")] string Exe,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("startedAt")] string StartedAt,
    [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Uri? Url = null);

/// <summary>Outcome of ensuring the running KyberDash API engine matches the active version.</summary>
/// <param name="Outcome">What <c>engine verify</c> found; <see langword="null"/> for every other engine verb.</param>
public record EngineEnsureReport(
    bool Succeeded,
    string Message,
    bool Replaced = false,
    EngineDescriptor? ActiveEngine = null,
    EngineVerifyOutcome? Outcome = null);

/// <summary>What <c>kyberdash engine verify</c> found the engine answering as.</summary>
public enum EngineVerifyOutcome
{
    /// <summary>An engine answered on the expected version.</summary>
    Running,

    /// <summary>No engine holds the endpoint.</summary>
    NotRunning,

    /// <summary>An engine answered on a different version.</summary>
    OtherVersion,

    /// <summary>The endpoint is held by a process that does not answer.</summary>
    Unresponsive,
}

/// <summary>Outcome of installing, updating, or checking the desktop tray binary.</summary>
public record MenubarInstallOutcome(
    int ExitCode,
    string Message);
