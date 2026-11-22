using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// OTLP admission and flushing: decodes JSON or Protobuf signal payloads, enforces the per-request
/// and backlog byte ceilings, and flushes queued telemetry into the canonical store.
/// </summary>
/// <remarks>
/// Admission returns the HTTP status the receiver should answer with (<c>200</c>, <c>413</c> for an
/// oversize payload, <c>503</c> with <c>Retry-After</c> when the backlog is full) instead of throwing,
/// so the transport layer stays free of policy. Pausing does not drop data: it flushes what is queued
/// and refuses new admissions until <see cref="ResumeIngestion"/>.
/// <para>
/// Admission only queues; <see cref="FlushPendingAsync"/> is the only writer. Nothing else drives it
/// between a request and shutdown, so a flush runs on <see cref="FlushInterval"/>, and an admission
/// wakes it at once when it is idle. Without that timer accepted telemetry sat in the queue until
/// the process exited, invisible to every reader of the store.
/// </para>
/// </remarks>
public class OtlpCollectorService : IOtlpCollectorService
{
    /// <summary><c>Retry-After</c> seconds advertised while ingestion is administratively paused.</summary>
    public const int PauseRetryAfterSeconds = 5;

    /// <summary>How long accepted telemetry may wait in the queue before it is written to the store.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly BoundedIngestWriter writer;
    private readonly ITranscriptIngestionService ingestion;
    private readonly IRelationalStoreWriter relationalWriter;
    private readonly IOperationalStore operationalStore;
    private readonly long maxBodyBytes;
    private readonly Lock stateGate = new();
    private readonly CancellationTokenSource flushShutdown = new();
    private readonly SemaphoreSlim flushWake = new(0, 1);
    private Task? flushLoop;
    private bool paused;
    private string? lastAcceptedAt;

    /// <summary>
    /// Initializes a new <see cref="OtlpCollectorService"/>.
    /// </summary>
    /// <param name="relationalWriter">Relational store writer receiving mapped log batches and diagnostic problems.</param>
    /// <param name="operationalStore">Operational store receiving quarantined spans, metric points, and ingest audit rows.</param>
    /// <param name="ingestion">Ingestion service receiving admitted spans.</param>
    /// <param name="options">Composition options carrying the payload and backlog ceilings.</param>
    public OtlpCollectorService(
        IRelationalStoreWriter relationalWriter,
        IOperationalStore operationalStore,
        ITranscriptIngestionService ingestion,
        DashOptions options)
    {
        ArgumentNullException.ThrowIfNull(relationalWriter);
        ArgumentNullException.ThrowIfNull(operationalStore);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(options);

        this.relationalWriter = relationalWriter;
        this.operationalStore = operationalStore;
        this.ingestion = ingestion;
        maxBodyBytes = options.MaxOtlpBodyBytes > 0 ? options.MaxOtlpBodyBytes : OtlpCollector.DefaultMaxBodyBytes;

        long maxPendingBytes = options.MaxPendingBytes > 0 ? options.MaxPendingBytes : BoundedIngestWriter.DefaultMaxPendingBytes;
        writer = new BoundedIngestWriter(
            maxBodyBytes: maxBodyBytes,
            maxPendingBytes: maxPendingBytes,
            ingestion: ingestion);
    }

    /// <inheritdoc />
    public long PendingBytes => writer.PendingBytes;

    /// <inheritdoc />
    public int PendingCount => writer.PendingItems;

    /// <inheritdoc />
    public bool IsPaused
    {
        get
        {
            lock (stateGate)
            {
                return paused;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? StoreUpdated;

    /// <summary>UTC ISO-8601 timestamp of the most recent persisted batch, when any.</summary>
    public string? LastAcceptedAt
    {
        get
        {
            lock (stateGate)
            {
                return lastAcceptedAt;
            }
        }
    }

    /// <summary>Maximum request payload bytes accepted on any signal path.</summary>
    public long MaxBodyBytes => maxBodyBytes;

    /// <inheritdoc />
    public Task<IngestAdmissionResult> AdmitTracesAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        const string Signal = "traces";
        return Task.Run(() => Admit(payload, contentType, Signal), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IngestAdmissionResult> AdmitLogsAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        const string Signal = "logs";
        return Task.Run(() => Admit(payload, contentType, Signal), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IngestAdmissionResult> AdmitMetricsAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        const string Signal = "metrics";
        return Task.Run(() => Admit(payload, contentType, Signal), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> FlushPendingAsync(CancellationToken cancellationToken = default) =>
        Task.Run(FlushPending, cancellationToken);

    /// <inheritdoc />
    public void PauseIngestion()
    {
        lock (stateGate)
        {
            paused = true;
        }

        FlushPending();
    }

    /// <inheritdoc />
    public void ResumeIngestion()
    {
        lock (stateGate)
        {
            paused = false;
        }
    }

    /// <summary>Decodes and admits one payload, then flushes the queue synchronously (legacy request path).</summary>
    /// <param name="payload">Raw request body.</param>
    /// <param name="contentType">Request content type, with or without parameters.</param>
    /// <returns>Admission result carrying the HTTP status the receiver should answer with.</returns>
    /// <exception cref="OtlpPayloadTooLargeException">The payload exceeds <see cref="MaxBodyBytes"/>.</exception>
    /// <exception cref="OtlpDecodeException">The payload is malformed or not OTLP.</exception>
    /// <exception cref="NotSupportedException">The content type names neither JSON nor Protobuf.</exception>
    public IngestAdmissionResult AdmitAndFlush(ReadOnlyMemory<byte> payload, string contentType)
    {
        IngestAdmissionResult admission = Admit(payload, contentType, signal: null);
        if (!admission.Accepted)
        {
            return admission;
        }

        FlushPending();
        return admission;
    }

    /// <summary>Starts the background flush loop that turns admitted telemetry into store rows.</summary>
    /// <remarks>
    /// Safe to call more than once: the second call is a no-op. A collector used only through
    /// <see cref="FlushPendingAsync"/> (as the collector's own tests do) works the same with or
    /// without the loop, because both run the same <see cref="FlushPending"/>.
    /// </remarks>
    public void Start()
    {
        lock (stateGate)
        {
            flushLoop ??= Task.Run(FlushLoopAsync);
        }
    }

    /// <summary>Stops the flush loop, writes whatever it still holds, and releases its wait handles.</summary>
    /// <returns>A task that completes once the collector is stopped.</returns>
    public async ValueTask StopAsync()
    {
        Task? loop;
        lock (stateGate)
        {
            loop = flushLoop;
            flushLoop = null;
        }

        if (loop is not null)
        {
            await flushShutdown.CancelAsync().ConfigureAwait(false);
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // The loop has stopped, so this is the only writer now and the last flush cannot race it.
        await FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
        flushWake.Dispose();
        flushShutdown.Dispose();
    }

    /// <summary>Wakes the flush loop without blocking the request that admitted the telemetry.</summary>
    private void WakeFlushLoop()
    {
        if (flushLoop is null || flushShutdown.IsCancellationRequested)
            return;

        try
        {
            _ = flushWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A flush is already owed: the loop reads newest-only, so one pending wake is enough.
        }
    }

    private async Task FlushLoopAsync()
    {
        CancellationToken stopping = flushShutdown.Token;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await flushWake.WaitAsync(FlushInterval, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await FlushPendingAsync(stopping).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed write must not kill the loop: the rows stay queued for the next tick,
                // and the receiving surfaces report the failure through the store they read.
                await Console.Error.WriteLineAsync(
                    $"kyberdash: OTLP flush failed; the batch stays queued: {exception.Message}").ConfigureAwait(false);
            }
        }
    }

    /// <summary>Admits <paramref name="payload"/> for one signal without decoding when it is paused or oversize.</summary>
    /// <param name="payload">Raw request body.</param>
    /// <param name="contentType">Request content type, with or without parameters.</param>
    /// <param name="signal">Signal name used for the decode step, or <see langword="null"/> to sniff the JSON shape.</param>
    /// <returns>Admission result.</returns>
    private IngestAdmissionResult Admit(ReadOnlyMemory<byte> payload, string contentType, string? signal)
    {
        if (IsPaused)
        {
            return new IngestAdmissionResult(
                false,
                503,
                PauseRetryAfterSeconds,
                "OTLP receiver paused for a database clean; retry");
        }

        if (payload.Length > maxBodyBytes)
        {
            return new IngestAdmissionResult(
                false,
                413,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"OTLP payload of {payload.Length} bytes exceeds the {maxBodyBytes} byte limit."));
        }

        bool isJson = IsJsonContentType(contentType);
        if (!isJson && !IsProtobufContentType(contentType))
        {
            throw new NotSupportedException(string.Create(
                CultureInfo.InvariantCulture,
                $"OTLP content type '{contentType}' is not supported: use application/json or application/x-protobuf."));
        }

        int max = (int)Math.Min(maxBodyBytes, int.MaxValue);
        int wireBytes = payload.Length;
        string? resolvedSignal = signal ?? SniffJsonSignal(payload.Span);

        IngestAdmissionResult admission = resolvedSignal switch
        {
            "logs" => writer.TryEnqueueLogs(
                isJson ? OtlpCollector.DecodeLogsJson(payload.Span, max) : OtlpCollector.DecodeLogsProtobuf(payload.Span, max),
                wireBytes),
            "metrics" => writer.TryEnqueueMetrics(
                isJson ? OtlpCollector.DecodeMetricsJson(payload.Span, max) : OtlpCollector.DecodeMetricsProtobuf(payload.Span, max),
                wireBytes),
            _ => writer.TryEnqueueSpans(
                isJson ? OtlpCollector.DecodeTracesJson(payload.Span, max) : OtlpCollector.DecodeTracesProtobuf(payload.Span, max),
                wireBytes),
        };

        // The exporter already has its 2xx, so this only shortens the wait before the rows are
        // readable; the periodic flush would catch it either way.
        if (admission.Accepted)
            WakeFlushLoop();

        return admission;
    }

    /// <summary>Drains the queue into the relational store and raises <see cref="StoreUpdated"/> when anything landed.</summary>
    /// <returns>Number of telemetry items the flush processed.</returns>
    private int FlushPending()
    {
        int pendingBefore = writer.PendingItems;
        if (pendingBefore == 0)
        {
            return 0;
        }

        (IngestOutcome outcome, int metricPointsWritten) = writer.FlushRelational(relationalWriter, operationalStore);
        int flushed = outcome.Accepted + outcome.Quarantined + outcome.Rejected + metricPointsWritten;
        if (flushed == 0)
        {
            return 0;
        }

        lock (stateGate)
        {
            lastAcceptedAt = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        StoreUpdated?.Invoke(this, EventArgs.Empty);
        return flushed;
    }

    private static bool IsJsonContentType(string? contentType) =>
        string.Equals(MediaType(contentType), "application/json", StringComparison.OrdinalIgnoreCase);

    private static bool IsProtobufContentType(string? contentType)
    {
        string? mediaType = MediaType(contentType);
        return string.Equals(mediaType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/protobuf", StringComparison.OrdinalIgnoreCase);
    }

    private static string? MediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        int semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
    }

    /// <summary>
    /// Picks the signal from the top-level JSON property when the caller did not name one, matching the
    /// three OTLP export request shapes.
    /// </summary>
    private static string? SniffJsonSignal(ReadOnlySpan<byte> payload)
    {
        try
        {
            Utf8JsonReader reader = new(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                // Only a top-level property name decides the signal; a value that merely mentions
                // one of these names (for example in an attribute) must not.
                string? name = reader.GetString();
                if (string.Equals(name, "resourceLogs", StringComparison.Ordinal))
                    return "logs";
                if (string.Equals(name, "resourceMetrics", StringComparison.Ordinal))
                    return "metrics";

                reader.Skip();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
