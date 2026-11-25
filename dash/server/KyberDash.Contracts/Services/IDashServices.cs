using System.Text.Json;
using KyberDash.Core.Resilience;
using KyberDash.Domain;

namespace KyberDash.Contracts.Services;

/// <summary>DI composition options shared across <c>kyberdash-api</c>, <c>kyberdash-mcp</c>, and <c>kyberdash</c>.</summary>
public class DashOptions
{
    /// <summary>Explicit SQLite canonical store path (<c>KYBER_CANON_DB</c> fallback when omitted).</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Explicit KyberDash state directory (<c>~/.kyberdash</c> fallback when omitted).</summary>
    public string? StateDirectory { get; set; }

    /// <summary>Override dashboard SPA directory (<c>KYBERDASH_DASH_DIR</c> fallback when omitted).</summary>
    public string? DashboardDir { get; set; }

    /// <summary>Reported binary/engine semantic version.</summary>
    public string Version { get; set; } = "0.21.0";

    /// <summary>Maximum OTLP per-request payload size in bytes.</summary>
    public long MaxOtlpBodyBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Maximum OTLP in-flight/queued backlog size in bytes.</summary>
    public long MaxPendingBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// Collector port the process was configured with. <see langword="null"/> when this process has no
    /// collector port to advertise. <c>0</c> means the OS has not chosen an ephemeral port yet.
    /// </summary>
    public int? OtlpPort { get; set; }

    /// <summary>Polly circuit breaker options protecting SQLite operations.</summary>
    public DashCircuitBreakerOptions CircuitBreaker { get; set; } = new();
}

/// <summary>Handle representing an acquired single-instance <c>kyberdash-api</c> IPC lock.</summary>
public interface IEngineOwnerLease : IAsyncDisposable, IDisposable
{
    /// <summary>Publishes the bound HTTP dashboard URI onto the engine IPC descriptor.</summary>
    void UpdateUrl(Uri endpointUrl);
}

/// <summary>Static dashboard SPA resolution service contract for Presentation hosts.</summary>
public interface IWebAssetService
{
    /// <summary>Resolves a static web asset or SPA HTML document for <paramref name="requestPath"/>.</summary>
    WebAssetDocument? TryResolveAsset(string requestPath, string? overrideDir = null);
}

/// <summary>Bounded OTLP telemetry ingestion and flushing application service contract.</summary>
public interface IOtlpCollectorService
{
    /// <summary>Current total queued plus in-flight bytes awaiting flush.</summary>
    long PendingBytes { get; }

    /// <summary>Current number of queued plus in-flight telemetry items.</summary>
    int PendingCount { get; }

    /// <summary>Whether OTLP ingestion is currently paused by administrative command or wipe.</summary>
    bool IsPaused { get; }

    /// <summary>UTC timestamp of the most recent batch that was written to the store, when any.</summary>
    string? LastAcceptedAt { get; }

    /// <summary>Code of the most recent rejected batch, when any.</summary>
    string? LastErrorCode { get; }

    /// <summary>Message of the most recent rejected batch, when any.</summary>
    string? LastError { get; }

    /// <summary>UTC timestamp of the most recent rejected batch, when any.</summary>
    string? LastErrorAt { get; }

    /// <summary>Counts admission failures recorded at or after <paramref name="cutoff"/>.</summary>
    /// <param name="cutoff">Inclusive start of the window, in UTC.</param>
    /// <returns>The number of rejected batches in the window.</returns>
    int CountFailuresSince(DateTimeOffset cutoff);

    /// <summary>Raised whenever a flushed batch modifies canonical store state.</summary>
    event EventHandler? StoreUpdated;

    /// <summary>Decodes and queues an OTLP traces request payload.</summary>
    Task<IngestAdmissionResult> AdmitTracesAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Decodes and queues an OTLP logs request payload.</summary>
    Task<IngestAdmissionResult> AdmitLogsAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Decodes and queues an OTLP metrics request payload.</summary>
    Task<IngestAdmissionResult> AdmitMetricsAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Flushes queued telemetry items into the canonical store and returns flushed item count.</summary>
    Task<int> FlushPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Pauses ingestion and drains active flushes.</summary>
    void PauseIngestion();

    /// <summary>Resumes OTLP admission.</summary>
    void ResumeIngestion();
}

/// <summary>
/// Whether this process has bound the OTLP listener. The host sets it after Kestrel starts;
/// a reader treats an unset state as "not this process".
/// </summary>
public interface IOtlpListenerState
{
    /// <summary>Whether this process is bound to the collector port.</summary>
    bool IsListening { get; }

    /// <summary>The collector port this process bound, when <see cref="IsListening"/> is true.</summary>
    int? BoundPort { get; }

    /// <summary>Records that this process is listening on <paramref name="port"/>.</summary>
    /// <param name="port">The bound collector port.</param>
    void NoteListening(int port);
}

/// <summary>Canonical span normalization and local transcript ingestion application service contract.</summary>
public interface ITranscriptIngestionService
{
    /// <summary>Runs adapter attribution, validation, quarantine, and persistence for decoded OTLP spans.</summary>
    IngestOutcome IngestSpans(IReadOnlyList<OtlpSpan> spans);

    /// <summary>Refreshes local harness transcript sources within <paramref name="historyWeeks"/> using checkpoints.</summary>
    RefreshReport RefreshHarnessSources(int historyWeeks = 2);

    /// <summary>
    /// Reads the transcript files of the sessions a plugin-captured OTLP source already recorded
    /// for <paramref name="harnessId"/>, so those sessions gain their file rows, models, and
    /// companion usage without a folder import.
    /// </summary>
    /// <remarks>
    /// Discovery runs through the harness's registered reader only, keeping the units whose
    /// transcript file stem is one of <paramref name="sessionKeys"/>; each kept unit then goes
    /// through the standard checkpoint check, so an unchanged file writes nothing and a later
    /// full refresh replaces the rows under the same source id. No <c>refresh_run</c> row is
    /// written and no history coverage is claimed. A session id is data: files are matched by
    /// comparing stems against discovery results, never by joining the id into a path.
    /// </remarks>
    /// <param name="harnessId">Harness whose registered reader discovers the files.</param>
    /// <param name="sessionKeys">Candidate session keys, as recorded by the plugin-captured source.</param>
    /// <returns>Per-unit status rows with no run id and no history claim.</returns>
    RefreshReport RefreshPluginSessionFiles(string harnessId, IReadOnlyCollection<string> sessionKeys);

    /// <summary>
    /// Re-reads the source units of <paramref name="harnessIds"/> that already hold a checkpoint,
    /// so a newer parser contract version replaces rows the harness stored with estimated tokens.
    /// </summary>
    /// <remarks>
    /// Discovery runs through each named harness's registered reader only, so no other harness's
    /// files are touched, and is not limited to the history window, so a checkpointed unit last
    /// written before it is still replaced. A discovered unit is read only when the operational store already holds
    /// its checkpoint; the standard checkpoint check then decides whether that checkpoint is still
    /// reusable, which is how the parser contract version change forces a re-read. A checkpointed
    /// unit whose source key discovery no longer returns (a deleted file, or a file now keyed
    /// differently) cannot be re-read, so the token values its stored version wrote
    /// as estimates are cleared to not measured instead, keeping its sessions, records and text; that
    /// unit's row reads <c>cleared</c>, or <c>failed</c> when the clear fails. No
    /// <c>refresh_run</c> row is written and no history coverage is claimed.
    /// </remarks>
    /// <param name="harnessIds">Harnesses whose checkpointed source units are re-read.</param>
    /// <returns>Per-unit status rows with no run id and no history claim.</returns>
    RefreshReport RereadCheckpointedSources(IReadOnlyCollection<string> harnessIds);

    /// <summary>Applies rolling 14-day content retention without dropping token or cost counters.</summary>
    PurgeExpiredContentResult PurgeExpiredContent(DateTimeOffset? now = null);
}

/// <summary>Incremental session, finding, run, execution, and harness rollup projection service contract.</summary>
public interface ICanonProjectionService
{
    /// <summary>Incrementally projects dirty sessions and recomputes affected findings.</summary>
    ProjectionPassReport ProjectIncremental();

    /// <summary>Projects runs, executions, and per-harness efficiency rollups.</summary>
    IReadOnlyList<HarnessRollupRow> ProjectRunsAndHarnesses();

    /// <summary>Executes both incremental session projection and run/harness rollups.</summary>
    (ProjectionPassReport Projection, IReadOnlyList<HarnessRollupRow> Rollups) ProjectAll();
}

/// <summary>Dashboard read/query service contract consumed by Minimal APIs and reporting commands.</summary>
public interface IDashboardQueryService
{
    /// <summary>Ensures projections and rollups are up to date before reads.</summary>
    void EnsureProjected();

    /// <summary>
    /// Returns one page of root sessions, each with its first 25 subagents inline and its subagent
    /// total.
    /// </summary>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Roots on the page; 100 unless given.</param>
    /// <param name="harness">Optional harness filter, which also scopes the root rule.</param>
    /// <param name="search">Optional text a root's label or session id must contain, matched literally.</param>
    JsonElement GetSessionPage(int page = 1, int limit = 100, string? harness = null, string? search = null);

    /// <summary>Returns one page of a session's subagents: the sessions that attach to it, oldest start first.</summary>
    /// <param name="parentId">Session whose subagents are paged.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Subagents on the page; 25 unless given.</param>
    JsonElement GetSubagentPage(string parentId, int page = 1, int limit = 25);

    /// <summary>Returns lightweight session summary payload.</summary>
    JsonElement? GetSessionSummary(string sessionId);

    /// <summary>Returns the charts section payload: the session's turn list and context object.</summary>
    JsonElement? GetSessionCharts(string sessionId);

    /// <summary>Returns one page of the session's request rows, each with the prompt preview its page read carries.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page; 25 unless given.</param>
    JsonElement? GetSessionRequests(string sessionId, int page = 1, int limit = 25);

    /// <summary>Returns one page of the session's measurable reconciliation rows.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page; 25 unless given.</param>
    JsonElement? GetSessionReconciliation(string sessionId, int page = 1, int limit = 25);

    /// <summary>Returns one page of the session's stored ingest problems.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page; 25 unless given.</param>
    JsonElement? GetSessionProblems(string sessionId, int page = 1, int limit = 25);

    /// <summary>Returns one page of the session's ranked tool rows with the section's page-independent aggregate.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page; 25 unless given.</param>
    JsonElement? GetSessionTools(string sessionId, int page = 1, int limit = 25);

    /// <summary>Returns one page of the session's per-server schema rollup rows.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page; 25 unless given.</param>
    JsonElement? GetSessionServers(string sessionId, int page = 1, int limit = 25);

    /// <summary>Returns one page of the session's distinct skill rows.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page; 25 unless given.</param>
    JsonElement? GetSessionSkills(string sessionId, int page = 1, int limit = 25);

    /// <summary>Returns one page of the session's KW skill loads (D17), in call order, apart from the harness skills section.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page; 25 unless given.</param>
    JsonElement? GetSessionKwSkills(string sessionId, int page = 1, int limit = 25);

    /// <summary>Returns one page of one level of the session's timeline tree, with each node's child count and the tree's bounds.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="parentNodeId">Span id whose children the page lists; the top level when null or empty.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Nodes on the page; 25 unless given.</param>
    JsonElement? GetSessionTimeline(string sessionId, string? parentNodeId = null, int page = 1, int limit = 25);

    /// <summary>Returns metrics and bucket figures for a specific turn.</summary>
    JsonElement? GetTurnFigures(string sessionId, int turnIndex);

    /// <summary>Returns bounded turn content payload.</summary>
    JsonElement? GetTurnContent(string sessionId, int turnIndex, int budget = 32_768, bool full = false);

    /// <summary>Returns bounded session content payload.</summary>
    JsonElement? GetSessionContent(string sessionId, int? turn = null, string? spanId = null, string? part = null, bool full = false);

    /// <summary>Returns raw span attributes payload.</summary>
    JsonElement? GetSpanAttributes(string spanId);

    /// <summary>Returns paginated findings payload.</summary>
    JsonElement GetFindings(
        string? runId = null,
        string? sessionId = null,
        string? detector = null,
        string? harness = null,
        int limit = 50,
        int offset = 0,
        int page = 1);

    /// <summary>Reads a single finding by identifier.</summary>
    Finding? GetFindingById(string findingId);

    /// <summary>Returns aggregated harness rollups payload.</summary>
    JsonElement GetHarnesses();

    /// <summary>Returns detail payload for one harness.</summary>
    JsonElement? GetHarnessById(string harnessId);

    /// <summary>Returns runs list payload.</summary>
    JsonElement GetRuns(string? harness = null);

    /// <summary>Returns detail payload for one run.</summary>
    JsonElement? GetRunById(string runId);

    /// <summary>Returns default comparison payload across recent runs.</summary>
    JsonElement GetCompare();

    /// <summary>Returns comparison payload between two explicit run IDs.</summary>
    JsonElement? GetCompareRuns(string runA, string runB);

    /// <summary>Returns paginated quarantine entries.</summary>
    JsonElement GetQuarantinePage(string? harness, int limit, int offset, int page);

    /// <summary>Returns detail for one quarantined span.</summary>
    JsonElement? GetQuarantineDetail(string spanId);

    /// <summary>Returns paginated problem entries.</summary>
    JsonElement GetProblemsPage(string? harness, int limit, int offset, int page);

    /// <summary>Returns detail for one problem entry.</summary>
    JsonElement? GetProblemDetail(string rawId);

    /// <summary>Returns API metadata payload.</summary>
    JsonElement GetMeta();

    /// <summary>Returns API version payload.</summary>
    JsonElement GetVersion();

    /// <summary>Returns telemetry and source coverage payload.</summary>
    JsonElement GetCoverage();

    /// <summary>Returns summary report payload.</summary>
    JsonElement GetReport(string? harness = null, string? sessionId = null, string? runId = null, int limit = 50);

    /// <summary>Returns summary report payload scoped to <paramref name="days"/>.</summary>
    /// <param name="harness">Optional harness filter.</param>
    /// <param name="sessionId">Optional session filter.</param>
    /// <param name="runId">Optional run filter.</param>
    /// <param name="limit">Maximum findings included.</param>
    /// <param name="days">Positive lookback window in whole days.</param>
    JsonElement GetReport(string? harness, string? sessionId, string? runId, int limit, int days);

    /// <summary>Returns session execution timeline tree payload.</summary>
    JsonElement GetTimeline(string? sessionId = null);

    /// <summary>Returns prediction list payload.</summary>
    JsonElement GetPredictions(string? runId = null);

    /// <summary>Records a prediction entry when valid.</summary>
    bool TryRecordPrediction(JsonElement body, out PredictionRow? created, out string? errorMessage);

    /// <summary>Returns prediction calibration payload.</summary>
    JsonElement GetCalibration();

    /// <summary>Returns model context-window catalog payload.</summary>
    JsonElement GetModelCatalog();

    /// <summary>
    /// Returns the content-free stored-fields inventory for a session — every catalog table's
    /// name, columns, scoped row count and missing parent keys, never a row value (decision D15)
    /// — or <see langword="null"/> when the session is unknown.
    /// </summary>
    /// <param name="sessionKey">Canonical session identifier.</param>
    JsonElement? GetStoredFieldsInventory(string sessionKey);

    /// <summary>
    /// Returns one page of a single catalog table's stored rows for a session, limited in SQL, or
    /// <see langword="null"/> when the session or the table is unknown (decision D15).
    /// </summary>
    /// <param name="sessionKey">Canonical session identifier.</param>
    /// <param name="table">Exact catalog table name; the allowlist is the catalog.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page; 25 unless given.</param>
    JsonElement? GetStoredFieldRows(string sessionKey, string table, int page = 1, int limit = 25);
}

/// <summary>Shared contract-v1 analytics service powering both HTTP analytics endpoints and <c>kyberdash-mcp</c>.</summary>
public interface IAnalyticsService
{
    /// <summary>Executes one analytics operation (<c>status</c>, <c>catalog</c>, <c>query</c>, <c>search-sessions</c>, <c>inspect</c>, <c>findings</c>, <c>compare</c>, <c>outcomes</c>).</summary>
    AnalyticsExecutionResult Execute(string operation, JsonElement? request);
}

/// <summary>Database wipe application service contract pausing ingest cleanly around wipes.</summary>
public interface ICleanService
{
    /// <summary>Validates scope and executes a pause -> wipe -> project -> optional reingest -> resume cycle.</summary>
    CleanReport CleanDatabase(
        CleanScope scope,
        Action? pauseIngestion = null,
        Action? resumeIngestion = null,
        Func<IReadOnlyList<string>?, int, int>? reingest = null);
}

/// <summary>Operator settings application service contract.</summary>
public interface ISettingsService
{
    /// <summary>Reads current operator settings.</summary>
    SettingsView GetSettings();

    /// <summary>Validates and applies a partial JSON settings update.</summary>
    SettingsView UpdateSettings(JsonElement patch);
}

/// <summary>Capture plugin installation, consent, and statusline event service contract.</summary>
public interface ICaptureService
{
    /// <summary>Returns capture receiver and plugin status. <paramref name="otlpPort"/> is the collector port to advertise, when the host knows it.</summary>
    /// <param name="stateDir">State directory holding consent.</param>
    /// <param name="otlpPort">Configured collector port, or <see langword="null"/> when this caller has none.</param>
    JsonElement GetCaptureStatus(string stateDir, int? otlpPort = null);

    /// <summary>Applies capture consent or configuration update from REST payload.</summary>
    JsonElement ConfigureCapture(JsonElement body, string stateDir);

    /// <summary>Installs or checks harness capture plugin files.</summary>
    (int ExitCode, string Output) InstallPlugin(string harness, bool ifInstalled, string? configDir, string? stateDir = null);

    /// <summary>Updates Factory capture plugin content consent.</summary>
    (int ExitCode, string Output) SetFactoryConsent(bool enabled, string? stateDir = null);

    /// <summary>Records an incoming Antigravity statusline JSON payload silently to local cache.</summary>
    Task RecordAntigravityStatusLineAsync(
        string rawPayload,
        string? stateDir = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Background projection/refresh scheduler application service contract.</summary>
public interface IJobSchedulerService : IAsyncDisposable, IDisposable
{
    /// <summary>Returns current job host runtime status.</summary>
    JobHostStatus GetStatus();

    /// <summary>Triggers an immediate incremental refresh pass.</summary>
    Task<JobOutcome> TriggerRefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Triggers an immediate history backfill pass.</summary>
    Task<JobOutcome> TriggerImportHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts the periodic background maintenance timer.</summary>
    void Start();

    /// <summary>Stops the periodic background maintenance timer and releases leases.</summary>
    Task StopAsync();
}

/// <summary>Health and integrity diagnostics service contract.</summary>
public interface IDoctorService
{
    /// <summary>Collects store, projection, and circuit-breaker diagnostics.</summary>
    DoctorDiagnosticsReport RunDiagnostics();

    /// <summary>Triggers SQLite vacuum / WAL checkpoint maintenance.</summary>
    void RunVacuum();
}

/// <summary>Formatted CLI report generation service contract.</summary>
public interface IReportService
{
    /// <summary>Renders a report in <c>json</c>, <c>markdown</c>, or <c>text</c> format.</summary>
    (int ExitCode, string Output) GenerateReport(
        string format,
        string? harness = null,
        string? sessionId = null,
        string? runId = null,
        int limit = 5);
}

/// <summary>Tray/menubar installation and update coordinator contract.</summary>
public interface IMenubarService
{
    /// <summary>Installs, updates, or verifies the KyberDash tray binary for macOS/Windows.</summary>
    Task<MenubarInstallOutcome> InstallOrUpdateAsync(
        bool force,
        bool update,
        string? requestedVersion,
        string cliVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>Process coordination service for checking and replacing the live <c>kyberdash-api</c> engine.</summary>
public interface IEngineLifecycleService
{
    /// <summary>Default state directory path (<c>~/.kyberdash</c>).</summary>
    string DefaultStateDirectory { get; }

    /// <summary>Ensures any live <c>kyberdash-api</c> engine matches <paramref name="expectedVersion"/>.</summary>
    Task<EngineEnsureReport> EnsureRunningEngineMatchesVersionAsync(
        string expectedVersion,
        string? stateDir = null,
        string? apiBinaryPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts to acquire the single-engine IPC socket lock, returning either a lease or existing holder.</summary>
    Task<(IEngineOwnerLease? Lease, EngineDescriptor? ExistingHolder)> TryAcquireEngineAsync(
        string stateDir,
        string version,
        string exePath,
        Action onShutdownRequested,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes <c>server.json</c> when the API server begins listening.</summary>
    void PublishServerInfo(string stateDir, ServerInfo info);

    /// <summary>Removes <c>server.json</c> when the API server shuts down cleanly.</summary>
    void ClearServerInfo(string stateDir);

    /// <summary>Resolves a live API base URI from <c>server.json</c> or <c>engine.sock</c> for external clients.</summary>
    Task<Uri?> ResolveLiveServerUriAsync(string? stateDir = null, CancellationToken cancellationToken = default);
}

/// <summary>Playwright E2E isolated world seeding service contract.</summary>
public interface IE2eSeederService
{
    /// <summary>Seeds an isolated canonical store for <paramref name="world"/>.</summary>
    int SeedWorld(string world, string dbPath, string repoRoot);
}
