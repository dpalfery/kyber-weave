using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace KyberWeave.Core.Dash;

/// <summary>
/// Loopback HTTP/1.1 API server exposing the full <c>/api/kyber/*</c> REST and analytics surface
/// plus <c>GET /api/events</c> (SSE) over <see cref="KyberBridge"/> and <see cref="KyberJobHost"/>
/// (<c>dash/src/server/routes.ts</c>, <c>dash/src/server/analytics-routes.ts</c>).
/// </summary>
public sealed class KyberApiServer : IAsyncDisposable, IDisposable
{
    /// <summary>Maximum JSON request body size accepted on mutating and analytics routes (64 KiB).</summary>
    public const int DefaultMaxApiBodyBytes = 64 * 1024;

    private readonly CanonStore _store;
    private readonly KyberBridge _bridge;
    private readonly KyberJobHost? _jobHost;
    private readonly Action? _pauseIngestion;
    private readonly Action? _resumeIngestion;
    private readonly Func<IReadOnlyList<string>?, int, int>? _reingest;
    private readonly LoopbackHttpHost _host;
    private readonly int _maxBodyBytes;
    private readonly string? _stateDir;
    private readonly Lock _sseLock = new();
    private readonly List<TaskCompletionSource> _sseWaiters = [];
    private string _reviewState = "idle";

    /// <summary>
    /// Initializes a new <see cref="KyberApiServer"/> bound to <see cref="IPAddress.Loopback"/>.
    /// Pass <paramref name="port"/> = <c>0</c> to bind an ephemeral loopback port.
    /// </summary>
    public KyberApiServer(
        CanonStore store,
        KyberBridge? bridge = null,
        KyberJobHost? jobHost = null,
        int port = 0,
        int maxBodyBytes = DefaultMaxApiBodyBytes,
        string? stateDir = null,
        Action? pauseIngestion = null,
        Action? resumeIngestion = null,
        Func<IReadOnlyList<string>?, int, int>? reingest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBodyBytes);

        _store = store;
        _bridge = bridge ?? new KyberBridge(store);
        _jobHost = jobHost;
        _maxBodyBytes = maxBodyBytes;
        _stateDir = stateDir;
        _pauseIngestion = pauseIngestion;
        _resumeIngestion = resumeIngestion;
        _reingest = reingest;
        _host = new LoopbackHttpHost(port, _ => _maxBodyBytes, HandleRequestAsync);
    }

    /// <summary>Bound TCP loopback port once started.</summary>
    public int Port => _host.Port;

    /// <summary>Whether the server listener is currently active.</summary>
    public bool IsListening => _host.IsListening;

    /// <summary>Base loopback HTTP endpoint URL.</summary>
    public string Endpoint => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}");

    /// <summary>Starts listening on loopback and publishes <c>server.json</c> when <c>stateDir</c> is set.</summary>
    public void Start()
    {
        _host.Start();
        if (!string.IsNullOrWhiteSpace(_stateDir))
            KyberJobHost.PublishServerInfo(_stateDir, Port, Environment.ProcessId);
    }

    /// <summary>Broadcasts an SSE update notification to connected <c>/api/events</c> clients.</summary>
    public void NotifyUpdate()
    {
        List<TaskCompletionSource> waiters;
        lock (_sseLock)
        {
            waiters = [.. _sseWaiters];
            _sseWaiters.Clear();
        }

        foreach (TaskCompletionSource waiter in waiters)
            waiter.TrySetResult();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        NotifyUpdate();
        await _host.DisposeAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(_stateDir))
            KyberJobHost.RemoveServerInfo(_stateDir, Environment.ProcessId);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private Task<LoopbackHttpResponse> HandleRequestAsync(LoopbackHttpRequest request, CancellationToken token)
    {
        string rawPath = RawPathOnly(request.RawTarget);

        if (string.Equals(rawPath, "/api/events", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Sse(StreamEventsAsync));
        }

        if (string.Equals(rawPath, "/api/kyber/sessions", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            int? limit = ParseOptionalPositiveInt(request.GetQuery("limit"));
            string? harness = request.GetQuery("harness");
            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetSessions(limit, harness)));
        }

        if (rawPath.StartsWith("/api/kyber/span/", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            string suffix = rawPath["/api/kyber/span/".Length..];
            if (!suffix.EndsWith("/attributes", StringComparison.Ordinal))
                return Task.FromResult(JsonError(404, "Not found"));

            string encodedId = suffix[..^"/attributes".Length];
            if (!TryDecodeSegment(encodedId, out string? spanId) || string.IsNullOrEmpty(spanId))
                return Task.FromResult(JsonError(400, "Missing span id"));

            JsonElement? attrs = _bridge.GetSpanAttributes(spanId);
            return Task.FromResult(attrs.HasValue
                ? LoopbackHttpResponse.Json(200, attrs.Value)
                : JsonError(404, "Span not found"));
        }

        if (string.Equals(rawPath, "/api/kyber/session", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetSessions()));
        }

        if (rawPath.StartsWith("/api/kyber/session/", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(HandleSessionRoute(rawPath["/api/kyber/session/".Length..], request));
        }

        if (string.Equals(rawPath, "/api/kyber/compare/runs", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            string? runA = (request.GetQuery("runA") ?? request.GetQuery("a"))?.Trim();
            string? runB = (request.GetQuery("runB") ?? request.GetQuery("b"))?.Trim();
            if (string.IsNullOrEmpty(runA) || string.IsNullOrEmpty(runB))
                return Task.FromResult(JsonError(400, "Missing runA or runB"));

            if (request.Query.ContainsKey("completedPairCount"))
                return Task.FromResult(JsonError(400, "completedPairCount is not accepted: recommendation history is not caller-supplied (issue #190)"));

            JsonElement? comparison = _bridge.GetCompareRuns(runA, runB);
            return Task.FromResult(comparison.HasValue
                ? LoopbackHttpResponse.Json(200, comparison.Value)
                : JsonError(404, "Run not found"));
        }

        if (string.Equals(rawPath, "/api/kyber/compare", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetCompare()));
        }

        if (string.Equals(rawPath, "/api/kyber/quarantine", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            string? harness = NormalizeOptionalFilter(request.GetQuery("harness"));
            (int limit, int offset, int page) = ParsePagination(request, defaultLimit: 200);
            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetQuarantinePage(harness, limit, offset, page)));
        }

        if (rawPath.StartsWith("/api/kyber/quarantine/", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            if (!TryDecodeSegment(rawPath["/api/kyber/quarantine/".Length..], out string? spanId) || string.IsNullOrEmpty(spanId))
                return Task.FromResult(JsonError(400, "Missing quarantine span id"));

            JsonElement? entry = _bridge.GetQuarantineDetail(spanId);
            return Task.FromResult(entry.HasValue
                ? LoopbackHttpResponse.Json(200, entry.Value)
                : JsonError(404, "Quarantine entry not found"));
        }

        if (string.Equals(rawPath, "/api/kyber/problems", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            string? harness = NormalizeOptionalFilter(request.GetQuery("harness"));
            (int limit, int offset, int page) = ParsePagination(request, defaultLimit: 200);
            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetProblemsPage(harness, limit, offset, page)));
        }

        if (rawPath.StartsWith("/api/kyber/problem/", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            if (!TryDecodeSegment(rawPath["/api/kyber/problem/".Length..], out string? rawId) || string.IsNullOrEmpty(rawId))
                return Task.FromResult(JsonError(400, "Missing problem id"));

            JsonElement? detail = _bridge.GetProblemDetail(rawId);
            return Task.FromResult(detail.HasValue
                ? LoopbackHttpResponse.Json(200, detail.Value)
                : JsonError(404, "Problem not found"));
        }

        if (string.Equals(rawPath, "/api/kyber/meta", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetMeta()));
        }

        if (string.Equals(rawPath, "/api/kyber/version", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetVersion()));
        }

        if (string.Equals(rawPath, "/api/kyber/coverage", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetCoverage()));
        }

        if (string.Equals(rawPath, "/api/kyber/report", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            string? harness = request.GetQuery("harness");
            string? sessionId = request.GetQuery("session");
            string? runId = request.GetQuery("run");
            int limit = ParseOptionalPositiveInt(request.GetQuery("limit")) ?? 50;
            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetReport(harness, sessionId, runId, limit)));
        }

        if (string.Equals(rawPath, "/api/kyber/findings", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            string? runId = request.GetQuery("runId");
            string? sessionId = request.GetQuery("sessionId");
            string? detector = request.GetQuery("detector");
            string? harness = request.GetQuery("harness");
            (int limit, int offset, _) = ParsePagination(request, defaultLimit: 100);
            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetFindings(runId, sessionId, detector, harness, limit, offset)));
        }

        if (rawPath.StartsWith("/api/kyber/finding/", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            if (!TryDecodeSegment(rawPath["/api/kyber/finding/".Length..], out string? findingId) || string.IsNullOrEmpty(findingId))
                return Task.FromResult(JsonError(400, "Missing finding id"));

            Finding? finding = _bridge.GetFindingById(findingId);
            return Task.FromResult(finding is not null
                ? LoopbackHttpResponse.Json(200, finding)
                : JsonError(404, "Finding not found"));
        }

        if (string.Equals(rawPath, "/api/kyber/predictions", StringComparison.Ordinal))
        {
            if (string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetPredictions(request.GetQuery("runId"))));

            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            if (!IsAllowedMutationRequest(request, out LoopbackHttpResponse? forbidden))
                return Task.FromResult(forbidden!);

            if (!TryParseBodyJson(request.Body, out JsonElement body, out LoopbackHttpResponse? badBody))
                return Task.FromResult(badBody!);

            if (!_bridge.TryRecordPrediction(body, out PredictionRow? created, out string? error))
                return Task.FromResult(JsonError(400, error ?? "Invalid prediction"));

            NotifyUpdate();
            return Task.FromResult(LoopbackHttpResponse.Json(201, created!));
        }

        if (string.Equals(rawPath, "/api/kyber/calibration", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetCalibration()));
        }

        if (string.Equals(rawPath, "/api/kyber/harnesses", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetHarnesses()));
        }

        if (rawPath.StartsWith("/api/kyber/harness/", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            if (!TryDecodeSegment(rawPath["/api/kyber/harness/".Length..], out string? harnessId) || string.IsNullOrEmpty(harnessId))
                return Task.FromResult(JsonError(400, "Missing harness id"));

            JsonElement? row = _bridge.GetHarnessById(harnessId);
            return Task.FromResult(row.HasValue
                ? LoopbackHttpResponse.Json(200, row.Value)
                : JsonError(404, "Harness not found"));
        }

        if (string.Equals(rawPath, "/api/kyber/runs", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetRuns(request.GetQuery("harness"))));
        }

        if (rawPath.StartsWith("/api/kyber/run/", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            if (!TryDecodeSegment(rawPath["/api/kyber/run/".Length..], out string? runId) || string.IsNullOrEmpty(runId))
                return Task.FromResult(JsonError(400, "Missing run id"));

            JsonElement? detail = _bridge.GetRunById(runId);
            return Task.FromResult(detail.HasValue
                ? LoopbackHttpResponse.Json(200, detail.Value)
                : JsonError(404, "Run not found"));
        }

        if (string.Equals(rawPath, "/api/kyber/timeline", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, _bridge.GetTimeline(request.GetQuery("sessionId"))));
        }

        if (string.Equals(rawPath, "/api/kyber/review/status", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, new { state = _reviewState }));
        }

        if (string.Equals(rawPath, "/api/kyber/review", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            if (!IsAllowedMutationRequest(request, out LoopbackHttpResponse? forbidden))
                return Task.FromResult(forbidden!);

            _bridge.EnsureProjected();
            _reviewState = "idle";
            return Task.FromResult(LoopbackHttpResponse.Json(200, new { state = _reviewState, ok = true }));
        }

        if (string.Equals(rawPath, "/api/kyber/model-catalog", StringComparison.Ordinal)
            || string.Equals(rawPath, "/api/kyber/model-catalog/refresh", StringComparison.Ordinal))
        {
            if (string.Equals(rawPath, "/api/kyber/model-catalog/refresh", StringComparison.Ordinal))
            {
                if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
                    return Task.FromResult(JsonError(405, "Method Not Allowed"));
                if (!IsAllowedMutationRequest(request, out LoopbackHttpResponse? forbidden))
                    return Task.FromResult(forbidden!);
            }
            else if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonError(405, "Method Not Allowed"));
            }

            return Task.FromResult(LoopbackHttpResponse.Json(200, new
            {
                source = "embedded_snapshot",
                models = Array.Empty<object>(),
            }));
        }

        if (string.Equals(rawPath, "/api/kyber/capture", StringComparison.Ordinal))
        {
            if (string.Equals(request.Method, "GET", StringComparison.Ordinal))
            {
                SettingsView settings = CanonSettings.ReadSettings(_store);
                return Task.FromResult(LoopbackHttpResponse.Json(200, new
                {
                    receiverHosted = settings.ReceiverHosted,
                    jobsPaused = settings.JobsPaused,
                }));
            }

            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));
            if (!IsAllowedMutationRequest(request, out LoopbackHttpResponse? forbidden))
                return Task.FromResult(forbidden!);

            SettingsView updated = CanonSettings.ReadSettings(_store);
            return Task.FromResult(LoopbackHttpResponse.Json(200, new
            {
                ok = true,
                receiverHosted = updated.ReceiverHosted,
            }));
        }

        if (string.Equals(rawPath, "/api/kyber/settings", StringComparison.Ordinal))
        {
            if (string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(LoopbackHttpResponse.Json(200, CanonSettings.ReadSettings(_store)));

            if (!string.Equals(request.Method, "PUT", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));
            if (!IsAllowedMutationRequest(request, out LoopbackHttpResponse? forbidden))
                return Task.FromResult(forbidden!);
            if (!TryParseBodyJson(request.Body, out JsonElement patch, out LoopbackHttpResponse? badBody))
                return Task.FromResult(badBody!);

            if (!CanonSettings.TryApplyPartialSettings(_store, patch, out SettingsView? next, out string? error))
                return Task.FromResult(JsonError(400, error ?? "Invalid settings patch"));

            NotifyUpdate();
            return Task.FromResult(LoopbackHttpResponse.Json(200, next!));
        }

        if (string.Equals(rawPath, "/api/kyber/jobs", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));

            JobHostStatus status = _jobHost?.GetStatus()
                ?? new JobHostStatus("idle", null, null, null, CanonSettings.ReadSettings(_store).JobsPaused, false, null, null);
            return Task.FromResult(LoopbackHttpResponse.Json(200, status));
        }

        if (string.Equals(rawPath, "/api/kyber/refresh", StringComparison.Ordinal)
            || string.Equals(rawPath, "/api/kyber/import-history", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));
            if (!IsAllowedMutationRequest(request, out LoopbackHttpResponse? forbidden))
                return Task.FromResult(forbidden!);

            JobOutcome outcome;
            if (_jobHost is not null)
            {
                outcome = string.Equals(rawPath, "/api/kyber/import-history", StringComparison.Ordinal)
                    ? _jobHost.TriggerImportHistory()
                    : _jobHost.TriggerRefresh();
            }
            else
            {
                _bridge.EnsureProjected();
                outcome = new JobOutcome("succeeded");
            }

            NotifyUpdate();
            int code = outcome.Outcome switch
            {
                "succeeded" => 200,
                "busy" => 409,
                "declined" => 409,
                "hosted-elsewhere" => 409,
                _ => 500,
            };
            return Task.FromResult(LoopbackHttpResponse.Json(code, outcome));
        }

        if (string.Equals(rawPath, "/api/kyber/clean", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
                return Task.FromResult(JsonError(405, "Method Not Allowed"));
            if (!IsAllowedMutationRequest(request, out LoopbackHttpResponse? forbidden))
                return Task.FromResult(forbidden!);
            if (!TryParseBodyJson(request.Body, out JsonElement cleanJson, out LoopbackHttpResponse? badBody))
                return Task.FromResult(badBody!);

            if (cleanJson.ValueKind != JsonValueKind.Object
                || !cleanJson.TryGetProperty("confirm", out JsonElement confirmEl)
                || confirmEl.ValueKind != JsonValueKind.True)
            {
                return Task.FromResult(JsonError(400, "clean requires explicit confirm: true"));
            }

            CleanScope scope = ParseCleanScope(cleanJson);
            try
            {
                CleanReport report = CanonClean.CleanDatabase(
                    _store,
                    scope,
                    _pauseIngestion,
                    _resumeIngestion,
                    _reingest);
                NotifyUpdate();
                return Task.FromResult(LoopbackHttpResponse.Json(200, report));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(JsonError(400, ex.Message));
            }
        }

        if (rawPath.StartsWith("/api/kyber/analytics/", StringComparison.Ordinal))
            return Task.FromResult(HandleAnalyticsRoute(rawPath["/api/kyber/analytics/".Length..], request));

        if (rawPath.StartsWith("/api/kyber/", StringComparison.Ordinal))
            return Task.FromResult(JsonError(404, "Not found"));

        return Task.FromResult(JsonError(404, "Not found"));
    }

    private LoopbackHttpResponse HandleSessionRoute(string remainder, LoopbackHttpRequest request)
    {
        string[] segments = remainder.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || !TryDecodeSegment(segments[0], out string? sessionId) || string.IsNullOrEmpty(sessionId))
            return JsonError(400, "Missing session id");

        if (segments.Length == 1)
        {
            JsonElement? detail = _bridge.GetSessionDetail(sessionId);
            return detail.HasValue ? LoopbackHttpResponse.Json(200, detail.Value) : JsonError(404, "Session not found");
        }

        if (segments.Length == 2 && string.Equals(segments[1], "summary", StringComparison.Ordinal))
        {
            JsonElement? summary = _bridge.GetSessionSummary(sessionId);
            return summary.HasValue ? LoopbackHttpResponse.Json(200, summary.Value) : JsonError(404, "Session not found");
        }

        if (segments.Length == 2 && string.Equals(segments[1], "content", StringComparison.Ordinal))
        {
            int? turn = ParseOptionalNonNegativeInt(request.GetQuery("turn"));
            string? spanId = request.GetQuery("span");
            string? part = request.GetQuery("part");
            bool full = IsTruthy(request.GetQuery("full"));
            JsonElement? content = _bridge.GetSessionContent(sessionId, turn, spanId, part, full);
            return content.HasValue ? LoopbackHttpResponse.Json(200, content.Value) : JsonError(404, "Session not found");
        }

        if (segments.Length >= 3 && string.Equals(segments[1], "turn", StringComparison.Ordinal))
        {
            if (!int.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out int turnIndex))
                return JsonError(400, "Invalid turn index");

            if (segments.Length == 3)
            {
                JsonElement? figures = _bridge.GetTurnFigures(sessionId, turnIndex);
                return figures.HasValue ? LoopbackHttpResponse.Json(200, figures.Value) : JsonError(404, "Turn not found");
            }

            if (segments.Length == 4 && string.Equals(segments[3], "content", StringComparison.Ordinal))
            {
                int budget = ParseOptionalPositiveInt(request.GetQuery("budget")) ?? KyberBridge.ContentResponseBudget;
                bool full = IsTruthy(request.GetQuery("full"));
                JsonElement? turnContent = _bridge.GetTurnContent(sessionId, turnIndex, budget, full);
                return turnContent.HasValue ? LoopbackHttpResponse.Json(200, turnContent.Value) : JsonError(404, "Turn not found");
            }
        }

        return JsonError(404, "Not found");
    }

    private LoopbackHttpResponse HandleAnalyticsRoute(string operation, LoopbackHttpRequest request)
    {
        bool isGetOp = string.Equals(operation, "status", StringComparison.Ordinal)
            || string.Equals(operation, "catalog", StringComparison.Ordinal);

        if (isGetOp)
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return JsonError(405, "Method Not Allowed");

            (int getStatus, JsonElement getEnvelope) = _bridge.ExecuteAnalytics(operation, null);
            return NoStoreJson(getStatus, getEnvelope);
        }

        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            return JsonError(405, "Method Not Allowed");

        JsonElement? postBody = null;
        if (request.Body.Length > 0)
        {
            if (!TryParseBodyJson(request.Body, out JsonElement parsed, out LoopbackHttpResponse? badBody))
                return badBody!;
            postBody = parsed;
        }
        else
        {
            postBody = JsonSerializer.SerializeToElement(new { });
        }

        (int status, JsonElement envelope) = _bridge.ExecuteAnalytics(operation, postBody);
        return NoStoreJson(status, envelope);
    }

    private async Task StreamEventsAsync(Stream stream, CancellationToken token)
    {
        byte[] hello = Encoding.UTF8.GetBytes(": connected\n\n");
        await stream.WriteAsync(hello, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        while (!token.IsCancellationRequested)
        {
            TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sseLock)
                _sseWaiters.Add(waiter);

            using CancellationTokenRegistration reg = token.Register(() => waiter.TrySetCanceled(token));
            try
            {
                await waiter.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            byte[] evt = Encoding.UTF8.GetBytes("event: update\ndata: {\"type\":\"store_updated\"}\n\n");
            await stream.WriteAsync(evt, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
    }

    private static bool IsAllowedMutationRequest(LoopbackHttpRequest request, out LoopbackHttpResponse? error)
    {
        error = null;
        if (request.GetHeader("Origin") is not null
            || (request.RemoteAddress is not null && !IPAddress.IsLoopback(request.RemoteAddress)))
        {
            error = JsonError(403, "Mutating routes accept same-machine loopback requests without browser Origin headers only");
            return false;
        }

        return true;
    }

    private static bool TryParseBodyJson(byte[] body, out JsonElement parsed, out LoopbackHttpResponse? error)
    {
        parsed = default;
        error = null;
        if (body.Length == 0)
        {
            error = JsonError(400, "Request body is empty");
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            parsed = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = JsonError(400, $"Malformed JSON body: {ex.Message}");
            return false;
        }
    }

    private static CleanScope ParseCleanScope(JsonElement json)
    {
        bool all = json.TryGetProperty("all", out JsonElement allEl) && allEl.ValueKind == JsonValueKind.True;
        List<string>? harnesses = null;
        if (json.TryGetProperty("harnesses", out JsonElement hEl) && hEl.ValueKind == JsonValueKind.Array)
        {
            harnesses = [];
            foreach (JsonElement item in hEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is string s)
                    harnesses.Add(s);
            }
        }

        int? reingestWeeks = null;
        if (json.TryGetProperty("reingestWeeks", out JsonElement wEl) && wEl.ValueKind == JsonValueKind.Number && wEl.TryGetInt32(out int w))
            reingestWeeks = w;

        return new CleanScope(All: all, Harnesses: harnesses, ReingestWeeks: reingestWeeks);
    }

    private static LoopbackHttpResponse JsonError(int statusCode, string message)
    {
        return LoopbackHttpResponse.Json(statusCode, new { error = message });
    }

    private static LoopbackHttpResponse NoStoreJson(int statusCode, object payload)
    {
        LoopbackHttpResponse response = LoopbackHttpResponse.Json(statusCode, payload);
        response.Headers["Cache-Control"] = "no-store";
        return response;
    }

    private static string RawPathOnly(string rawTarget)
    {
        int q = rawTarget.IndexOf('?', StringComparison.Ordinal);
        return q >= 0 ? rawTarget[..q] : rawTarget;
    }

    private static bool TryDecodeSegment(string encoded, out string? decoded)
    {
        try
        {
            decoded = Uri.UnescapeDataString(encoded);
            return true;
        }
        catch (UriFormatException)
        {
            decoded = null;
            return false;
        }
    }

    private static int? ParseOptionalPositiveInt(string? raw)
    {
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v > 0 ? v : null;
    }

    private static int? ParseOptionalNonNegativeInt(string? raw)
    {
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v >= 0 ? v : null;
    }

    private static (int Limit, int Offset, int Page) ParsePagination(LoopbackHttpRequest request, int defaultLimit)
    {
        int limit = Math.Clamp(ParseOptionalPositiveInt(request.GetQuery("limit")) ?? defaultLimit, 1, 500);
        int? rawPage = ParseOptionalPositiveInt(request.GetQuery("page"));
        int offset = rawPage.HasValue
            ? (rawPage.Value - 1) * limit
            : ParseOptionalNonNegativeInt(request.GetQuery("offset")) ?? 0;
        int page = (offset / limit) + 1;
        return (limit, offset, page);
    }

    private static string? NormalizeOptionalFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            return null;

        return raw.Trim();
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);
    }
}
