using System.Globalization;
using System.Net;
using System.Text.Json;

namespace KyberWeave.Core.Dash;

/// <summary>
/// Loopback OTLP/HTTP receiver (<c>dash/src/otel/receiver.ts</c>) accepting JSON and Protobuf
/// payloads on <c>/v1/traces</c>, <c>/v1/logs</c>, and <c>/v1/metrics</c>, plus <c>/healthz</c>
/// and loopback-only maintenance <c>/v1/admin/pause</c> and <c>/v1/admin/resume</c>.
/// </summary>
public sealed class OtlpHttpServer : IAsyncDisposable, IDisposable
{
    /// <summary>Standard OTLP/HTTP receiver port.</summary>
    public const int DefaultOtlpPort = OtlpCollector.DefaultOtlpPort;

    /// <summary>Default maintenance pause lease duration (10 minutes in milliseconds).</summary>
    public const long DefaultPauseLeaseMs = 600_000;

    /// <summary>Default maximum OTLP request body size (64 MiB).</summary>
    public const int DefaultMaxBodyBytes = OtlpCollector.DefaultMaxBodyBytes;

    /// <summary>Admin pause path.</summary>
    public const string AdminPausePath = "/v1/admin/pause";

    /// <summary>Admin resume path.</summary>
    public const string AdminResumePath = "/v1/admin/resume";

    private readonly CanonStore _store;
    private readonly BoundedIngestWriter _writer;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action? _onPersisted;
    private readonly LoopbackHttpHost _host;
    private readonly Lock _stateLock = new();
    private readonly int _maxBodyBytes;
    private long _pausedUntilMs;
    private string? _lastAcceptedAt;

    /// <summary>
    /// Initializes a new <see cref="OtlpHttpServer"/> bound to <see cref="IPAddress.Loopback"/>.
    /// Pass <paramref name="port"/> = <c>0</c> to bind an ephemeral loopback port.
    /// </summary>
    public OtlpHttpServer(
        CanonStore store,
        int port = DefaultOtlpPort,
        int maxBodyBytes = DefaultMaxBodyBytes,
        BoundedIngestWriter? writer = null,
        Func<DateTimeOffset>? clock = null,
        Action? onPersisted = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBodyBytes);

        _store = store;
        _maxBodyBytes = maxBodyBytes;
        _writer = writer ?? new BoundedIngestWriter(maxBodyBytes: maxBodyBytes);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _onPersisted = onPersisted;
        _host = new LoopbackHttpHost(port, _ => _maxBodyBytes, HandleRequestAsync);
    }

    /// <summary>Bound TCP loopback port once started.</summary>
    public int Port => _host.Port;

    /// <summary>Whether the TCP listener is currently active.</summary>
    public bool IsListening => _host.IsListening;

    /// <summary>Base loopback HTTP endpoint URL.</summary>
    public string Endpoint => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}");

    /// <summary>UTC ISO-8601 timestamp of the most recent persisted batch, if any.</summary>
    public string? LastAcceptedAt
    {
        get
        {
            lock (_stateLock)
                return _lastAcceptedAt;
        }
    }

    /// <summary>Whether ingestion is currently paused under an unexpired lease.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_stateLock)
            {
                if (_pausedUntilMs <= 0)
                    return false;

                long now = _clock().ToUnixTimeMilliseconds();
                if (now >= _pausedUntilMs)
                {
                    _pausedUntilMs = 0;
                    return false;
                }

                return true;
            }
        }
    }

    /// <summary>Starts listening on loopback.</summary>
    public void Start() => _host.Start();

    /// <summary>Pauses ingestion for <paramref name="leaseMs"/> milliseconds and flushes buffered items.</summary>
    public void Pause(long leaseMs = DefaultPauseLeaseMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseMs);
        lock (_stateLock)
        {
            _pausedUntilMs = _clock().ToUnixTimeMilliseconds() + leaseMs;
            _writer.Flush(_store);
        }
    }

    /// <summary>Clears any active pause lease so ingestion resumes immediately.</summary>
    public void Resume()
    {
        lock (_stateLock)
            _pausedUntilMs = 0;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <inheritdoc/>
    public void Dispose() => _host.Dispose();

    private Task<LoopbackHttpResponse> HandleRequestAsync(LoopbackHttpRequest request, CancellationToken token)
    {
        if (string.Equals(request.Path, OtlpCollector.HealthzPath, StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return Task.FromResult(MethodNotAllowed("GET"));

            return Task.FromResult(LoopbackHttpResponse.Json(200, new
            {
                service = "kyberdash-otlp",
                paused = IsPaused,
                endpoint = Endpoint,
                listening = IsListening,
                lastAcceptedAt = LastAcceptedAt,
                checkedAt = _clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            }));
        }

        if (string.Equals(request.Path, AdminPausePath, StringComparison.Ordinal)
            || string.Equals(request.Path, AdminResumePath, StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
                return Task.FromResult(MethodNotAllowed("POST"));

            if (request.GetHeader("Origin") is not null
                || (request.RemoteAddress is not null && !IPAddress.IsLoopback(request.RemoteAddress)))
            {
                return Task.FromResult(LoopbackHttpResponse.Json(403, new
                {
                    error = new
                    {
                        code = "OTLP_FORBIDDEN",
                        message = "admin routes accept loopback requests without browser Origin headers only",
                    },
                }));
            }

            if (string.Equals(request.Path, AdminPausePath, StringComparison.Ordinal))
            {
                long leaseMs = ParseLeaseMs(request.Body);
                Pause(leaseMs);
                return Task.FromResult(LoopbackHttpResponse.Json(200, new { paused = IsPaused, leaseMs }));
            }

            Resume();
            return Task.FromResult(LoopbackHttpResponse.Json(200, new { paused = IsPaused }));
        }

        bool isSignal = string.Equals(request.Path, OtlpCollector.TracesPath, StringComparison.Ordinal)
            || string.Equals(request.Path, OtlpCollector.LogsPath, StringComparison.Ordinal)
            || string.Equals(request.Path, OtlpCollector.MetricsPath, StringComparison.Ordinal);

        if (!isSignal)
        {
            return Task.FromResult(LoopbackHttpResponse.Json(404, new
            {
                error = new
                {
                    code = "OTLP_NOT_FOUND",
                    message = $"unknown endpoint {request.Method} {request.Path}",
                },
            }));
        }

        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            return Task.FromResult(MethodNotAllowed("POST"));

        if (IsPaused)
        {
            LoopbackHttpResponse pausedResponse = LoopbackHttpResponse.Json(503, new
            {
                error = new
                {
                    code = "OTLP_PAUSED",
                    message = "receiver paused for a database clean; retry",
                },
            });
            pausedResponse.Headers["Retry-After"] = "5";
            return Task.FromResult(pausedResponse);
        }

        string? mediaType = ParseMediaType(request.GetHeader("Content-Type"));
        bool isJson = string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        bool isProtobuf = string.Equals(mediaType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/protobuf", StringComparison.OrdinalIgnoreCase);

        if (!isJson && !isProtobuf)
        {
            return Task.FromResult(LoopbackHttpResponse.Json(415, new
            {
                error = new
                {
                    code = "OTLP_UNSUPPORTED_MEDIA_TYPE",
                    message = $"content type {mediaType ?? "(none)"} is not supported: use application/json or application/x-protobuf",
                },
            }));
        }

        try
        {
            return Task.FromResult(ProcessSignal(request.Path, isJson, request.Body));
        }
        catch (OtlpPayloadTooLargeException ex)
        {
            return Task.FromResult(LoopbackHttpResponse.Json(
                413,
                new { error = new { code = "OTLP_PAYLOAD_TOO_LARGE", message = ex.Message } },
                forceClose: true));
        }
        catch (OtlpDecodeException ex)
        {
            return Task.FromResult(LoopbackHttpResponse.Json(400, new
            {
                error = new { code = "OTLP_MALFORMED_PAYLOAD", message = ex.Message },
            }));
        }
    }

    private LoopbackHttpResponse ProcessSignal(string path, bool isJson, byte[] body)
    {
        if (string.Equals(path, OtlpCollector.TracesPath, StringComparison.Ordinal))
        {
            IReadOnlyList<OtlpSpan> spans = isJson
                ? OtlpCollector.DecodeTracesJson(body, _maxBodyBytes)
                : OtlpCollector.DecodeTracesProtobuf(body, _maxBodyBytes);

            return ExecuteAdmittedIngest(
                () => _writer.TryEnqueueSpans(spans, body.Length),
                new { acceptedSpans = spans.Count });
        }

        if (string.Equals(path, OtlpCollector.LogsPath, StringComparison.Ordinal))
        {
            IReadOnlyList<OtlpLog> logs = isJson
                ? OtlpCollector.DecodeLogsJson(body, _maxBodyBytes)
                : OtlpCollector.DecodeLogsProtobuf(body, _maxBodyBytes);

            return ExecuteAdmittedIngest(
                () => _writer.TryEnqueueLogs(logs, body.Length),
                new { acceptedLogs = logs.Count });
        }

        IReadOnlyList<OtlpMetric> metrics = isJson
            ? OtlpCollector.DecodeMetricsJson(body, _maxBodyBytes)
            : OtlpCollector.DecodeMetricsProtobuf(body, _maxBodyBytes);

        return ExecuteAdmittedIngest(
            () => _writer.TryEnqueueMetrics(metrics, body.Length),
            new { acceptedMetrics = metrics.Count });
    }

    private LoopbackHttpResponse ExecuteAdmittedIngest(Func<IngestAdmissionResult> tryEnqueue, object successPayload)
    {
        lock (_stateLock)
        {
            IngestAdmissionResult admission = tryEnqueue();
            if (!admission.Accepted)
                return BackpressureResponse(admission);

            _writer.Flush(_store);
            _lastAcceptedAt = _clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        _onPersisted?.Invoke();
        return LoopbackHttpResponse.Json(200, successPayload);
    }

    private static LoopbackHttpResponse BackpressureResponse(IngestAdmissionResult admission)
    {
        bool oversize = admission.HttpStatusCode == 413;
        LoopbackHttpResponse response = LoopbackHttpResponse.Json(
            admission.HttpStatusCode,
            new
            {
                error = new
                {
                    code = oversize ? "OTLP_PAYLOAD_TOO_LARGE" : "OTLP_BACKPRESSURE",
                    message = admission.ErrorMessage ?? "Ingestion buffer full; retry",
                },
            },
            forceClose: oversize);
        if (admission.RetryAfterSeconds.HasValue)
            response.Headers["Retry-After"] = admission.RetryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        return response;
    }

    private static LoopbackHttpResponse MethodNotAllowed(string allowed)
    {
        LoopbackHttpResponse response = LoopbackHttpResponse.Json(405, new
        {
            error = new { code = "OTLP_METHOD_NOT_ALLOWED", message = $"Method is not supported: use {allowed}" },
        });
        response.Headers["Allow"] = allowed;
        return response;
    }

    private static string? ParseMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        int semi = contentType.IndexOf(';', StringComparison.Ordinal);
        return (semi >= 0 ? contentType[..semi] : contentType).Trim();
    }

    private static long ParseLeaseMs(byte[] body)
    {
        if (body.Length == 0)
            return DefaultPauseLeaseMs;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("leaseMs", out JsonElement el)
                && el.ValueKind == JsonValueKind.Number
                && el.TryGetInt64(out long ms)
                && ms > 0)
            {
                return ms;
            }
        }
        catch (JsonException)
        {
        }

        return DefaultPauseLeaseMs;
    }
}
