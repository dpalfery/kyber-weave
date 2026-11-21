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

/// <summary>Canonical span normalization and local transcript ingestion application service contract.</summary>
public interface ITranscriptIngestionService
{
    /// <summary>Runs adapter attribution, validation, quarantine, and persistence for decoded OTLP spans.</summary>
    IngestOutcome IngestSpans(IReadOnlyList<OtlpSpan> spans);

    /// <summary>Refreshes local harness transcript sources within <paramref name="historyWeeks"/> using checkpoints.</summary>
    RefreshReport RefreshHarnessSources(int historyWeeks = 2);

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

    /// <summary>Returns hierarchical root-first sessions with nested subagents.</summary>
    JsonElement GetSessions(int? limit = null, string? harness = null);

    /// <summary>Returns full session detail payload.</summary>
    JsonElement? GetSessionDetail(string sessionId);

    /// <summary>Returns lightweight session summary payload.</summary>
    JsonElement? GetSessionSummary(string sessionId);

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

    /// <summary>Returns the catalog-driven stored-fields view for a session, or <see langword="null"/> when unknown.</summary>
    JsonElement? GetStoredFields(string sessionKey);
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
    /// <summary>Returns capture receiver & plugin status payload.</summary>
    JsonElement GetCaptureStatus(string stateDir);

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
