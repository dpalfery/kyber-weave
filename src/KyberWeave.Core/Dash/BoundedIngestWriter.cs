using System.Globalization;

namespace KyberWeave.Core.Dash;

/// <summary>
/// Result of attempting to admit an OTLP span or log batch into <see cref="BoundedIngestWriter"/>.
/// </summary>
/// <param name="Accepted"><see langword="true"/> when queued; <see langword="false"/> when rejected by payload or backlog caps.</param>
/// <param name="HttpStatusCode">HTTP status code to return (<c>200</c> accepted, <c>413</c> permanently oversize, <c>503</c> transient backpressure).</param>
/// <param name="RetryAfterSeconds">Seconds advertised in <c>Retry-After</c> when <paramref name="HttpStatusCode"/> is <c>503</c>.</param>
/// <param name="ErrorMessage">Diagnostic explanation when rejected.</param>
public sealed record IngestAdmissionResult(
    bool Accepted,
    int HttpStatusCode,
    int? RetryAfterSeconds = null,
    string? ErrorMessage = null);

/// <summary>
/// Thread-safe, memory-bounded ingest queue that enforces per-request (64 MiB) and total
/// backlog (256 MiB / item ceiling) limits and restores batches without loss on store failure
/// (<c>dash/src/otel/writer.ts</c>).
/// </summary>
public sealed class BoundedIngestWriter
{
    /// <summary>Maximum decoded/wire batch size admitted for a single request (64 MiB).</summary>
    public const long DefaultMaxBodyBytes = OtlpCollector.DefaultMaxBodyBytes;

    /// <summary>Maximum retained in-flight + queued backlog bytes before shedding with 503 (256 MiB).</summary>
    public const long DefaultMaxPendingBytes = 256L * 1024 * 1024;

    /// <summary>Default flush batch size.</summary>
    public const int DefaultBatchSize = 512;

    /// <summary>Default soft high-water mark before producers should await flush.</summary>
    public const int DefaultHighWaterMark = DefaultBatchSize * 8;

    /// <summary>Default total queued + in-flight item cap.</summary>
    public const int DefaultMaxPendingItems = DefaultHighWaterMark * 4;

    /// <summary>Retry-After header value in seconds advertised on HTTP 503 backpressure.</summary>
    public const int RetryAfterSeconds = 1;

    private readonly long maxBodyBytes;
    private readonly long maxPendingBytes;
    private readonly int batchSize;
    private readonly int maxPendingItems;

    private readonly Lock stateGate = new();
    private readonly Lock flushGate = new();

    private readonly List<OtlpSpan> spanQueue = [];
    private readonly List<long> spanQueueBytes = [];
    private readonly List<OtlpLog> logQueue = [];
    private readonly List<long> logQueueBytes = [];
    private readonly List<OtlpMetric> metricQueue = [];
    private readonly List<long> metricQueueBytes = [];

    private long queuedBytes;
    private long inflightBytes;
    private int inflightSpans;
    private int inflightLogs;
    private int inflightMetrics;

    /// <summary>
    /// Initializes a new <see cref="BoundedIngestWriter"/> with configurable batch and backlog bounds.
    /// </summary>
    public BoundedIngestWriter(
        int batchSize = DefaultBatchSize,
        long maxBodyBytes = DefaultMaxBodyBytes,
        long maxPendingBytes = DefaultMaxPendingBytes,
        int maxPendingItems = DefaultMaxPendingItems)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBodyBytes, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPendingBytes, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPendingItems, 0);

        this.batchSize = batchSize;
        this.maxBodyBytes = maxBodyBytes;
        this.maxPendingBytes = maxPendingBytes;
        this.maxPendingItems = maxPendingItems;
    }

    /// <summary>Total accounted bytes currently queued or in flight to the store.</summary>
    public long PendingBytes
    {
        get
        {
            lock (stateGate)
                return queuedBytes + inflightBytes;
        }
    }

    /// <summary>Total decoded spans currently queued or in flight.</summary>
    public int PendingSpans
    {
        get
        {
            lock (stateGate)
                return spanQueue.Count + inflightSpans;
        }
    }

    /// <summary>Total decoded logs currently queued or in flight.</summary>
    public int PendingLogs
    {
        get
        {
            lock (stateGate)
                return logQueue.Count + inflightLogs;
        }
    }

    /// <summary>Total decoded metrics currently queued or in flight.</summary>
    public int PendingMetrics
    {
        get
        {
            lock (stateGate)
                return metricQueue.Count + inflightMetrics;
        }
    }

    /// <summary>Total queued + in-flight spans, logs, and metrics combined.</summary>
    public int PendingItems
    {
        get
        {
            lock (stateGate)
                return spanQueue.Count + logQueue.Count + metricQueue.Count + inflightSpans + inflightLogs + inflightMetrics;
        }
    }

    /// <summary>
    /// Attempts to admit a span batch under <see cref="DefaultMaxBodyBytes"/> and
    /// <see cref="DefaultMaxPendingBytes"/> without blocking or dropping telemetry.
    /// </summary>
    public IngestAdmissionResult TryEnqueueSpans(IReadOnlyList<OtlpSpan> spans, long wireBytes)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentOutOfRangeException.ThrowIfNegative(wireBytes);

        lock (stateGate)
        {
            if (wireBytes > maxBodyBytes || wireBytes > maxPendingBytes || spans.Count > maxPendingItems)
            {
                return new IngestAdmissionResult(
                    Accepted: false,
                    HttpStatusCode: 413,
                    ErrorMessage: string.Create(
                        CultureInfo.InvariantCulture,
                        $"Batch of {spans.Count} spans ({wireBytes} bytes) exceeds the ingest ceiling; shrink or split the batch."));
            }

            if (spans.Count == 0)
                return new IngestAdmissionResult(Accepted: true, HttpStatusCode: 200);

            long currentBytes = queuedBytes + inflightBytes;
            int currentItems = spanQueue.Count + logQueue.Count + inflightSpans + inflightLogs;
            if (currentBytes + wireBytes > maxPendingBytes || currentItems + spans.Count > maxPendingItems)
            {
                return new IngestAdmissionResult(
                    Accepted: false,
                    HttpStatusCode: 503,
                    RetryAfterSeconds: RetryAfterSeconds,
                    ErrorMessage: string.Create(
                        CultureInfo.InvariantCulture,
                        $"Ingest backlog is full ({currentItems} items, {currentBytes} bytes retained); retry after {RetryAfterSeconds}s."));
            }

            PushWithBytes(spanQueue, spanQueueBytes, spans, wireBytes);
            queuedBytes += wireBytes;
            return new IngestAdmissionResult(Accepted: true, HttpStatusCode: 200);
        }
    }

    /// <summary>
    /// Attempts to admit a log batch under the same request and backlog ceilings.
    /// </summary>
    public IngestAdmissionResult TryEnqueueLogs(IReadOnlyList<OtlpLog> logs, long wireBytes)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentOutOfRangeException.ThrowIfNegative(wireBytes);

        lock (stateGate)
        {
            if (wireBytes > maxBodyBytes || wireBytes > maxPendingBytes || logs.Count > maxPendingItems)
            {
                return new IngestAdmissionResult(
                    Accepted: false,
                    HttpStatusCode: 413,
                    ErrorMessage: string.Create(
                        CultureInfo.InvariantCulture,
                        $"Batch of {logs.Count} logs ({wireBytes} bytes) exceeds the ingest ceiling; shrink or split the batch."));
            }

            if (logs.Count == 0)
                return new IngestAdmissionResult(Accepted: true, HttpStatusCode: 200);

            long currentBytes = queuedBytes + inflightBytes;
            int currentItems = spanQueue.Count + logQueue.Count + inflightSpans + inflightLogs;
            if (currentBytes + wireBytes > maxPendingBytes || currentItems + logs.Count > maxPendingItems)
            {
                return new IngestAdmissionResult(
                    Accepted: false,
                    HttpStatusCode: 503,
                    RetryAfterSeconds: RetryAfterSeconds,
                    ErrorMessage: string.Create(
                        CultureInfo.InvariantCulture,
                        $"Ingest backlog is full ({currentItems} items, {currentBytes} bytes retained); retry after {RetryAfterSeconds}s."));
            }

            PushWithBytes(logQueue, logQueueBytes, logs, wireBytes);
            queuedBytes += wireBytes;
            return new IngestAdmissionResult(Accepted: true, HttpStatusCode: 200);
        }
    }

    /// <summary>
    /// Attempts to admit a metric batch under the same request and backlog ceilings.
    /// </summary>
    public IngestAdmissionResult TryEnqueueMetrics(IReadOnlyList<OtlpMetric> metrics, long wireBytes)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentOutOfRangeException.ThrowIfNegative(wireBytes);

        lock (stateGate)
        {
            if (wireBytes > maxBodyBytes || wireBytes > maxPendingBytes || metrics.Count > maxPendingItems)
            {
                return new IngestAdmissionResult(
                    Accepted: false,
                    HttpStatusCode: 413,
                    ErrorMessage: string.Create(
                        CultureInfo.InvariantCulture,
                        $"Batch of {metrics.Count} metrics ({wireBytes} bytes) exceeds the ingest ceiling; shrink or split the batch."));
            }

            if (metrics.Count == 0)
                return new IngestAdmissionResult(Accepted: true, HttpStatusCode: 200);

            long currentBytes = queuedBytes + inflightBytes;
            int currentItems = spanQueue.Count + logQueue.Count + metricQueue.Count + inflightSpans + inflightLogs + inflightMetrics;
            if (currentBytes + wireBytes > maxPendingBytes || currentItems + metrics.Count > maxPendingItems)
            {
                return new IngestAdmissionResult(
                    Accepted: false,
                    HttpStatusCode: 503,
                    RetryAfterSeconds: RetryAfterSeconds,
                    ErrorMessage: string.Create(
                        CultureInfo.InvariantCulture,
                        $"Ingest backlog is full ({currentItems} items, {currentBytes} bytes retained); retry after {RetryAfterSeconds}s."));
            }

            PushWithBytes(metricQueue, metricQueueBytes, metrics, wireBytes);
            queuedBytes += wireBytes;
            return new IngestAdmissionResult(Accepted: true, HttpStatusCode: 200);
        }
    }

    /// <summary>
    /// Drains queued spans in <see cref="DefaultBatchSize"/> chunks through
    /// <see cref="CanonIngest.IngestBatch"/> (and retires admitted logs/metrics), releasing accounted
    /// bytes only after persistence succeeds and restoring any failed batch to the head of the queue.
    /// </summary>
    public IngestOutcome Flush(
        CanonStore store,
        IReadOnlyList<IHarnessAdapter>? adapters = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        lock (flushGate)
        {
            int totalAccepted = 0;
            int totalQuarantined = 0;
            int totalRejected = 0;

            while (true)
            {
                List<OtlpSpan> batchSpans;
                List<long> batchSpanBytes;
                List<OtlpLog> batchLogs;
                List<long> batchLogBytes;
                List<OtlpMetric> batchMetrics;
                List<long> batchMetricBytes;
                long sentBytes;

                lock (stateGate)
                {
                    if (spanQueue.Count == 0 && logQueue.Count == 0 && metricQueue.Count == 0)
                        break;

                    int spanTake = Math.Min(batchSize, spanQueue.Count);
                    batchSpans = spanQueue.GetRange(0, spanTake);
                    batchSpanBytes = spanQueueBytes.GetRange(0, spanTake);
                    spanQueue.RemoveRange(0, spanTake);
                    spanQueueBytes.RemoveRange(0, spanTake);

                    int logTake = Math.Min(batchSize, logQueue.Count);
                    batchLogs = logQueue.GetRange(0, logTake);
                    batchLogBytes = logQueueBytes.GetRange(0, logTake);
                    logQueue.RemoveRange(0, logTake);
                    logQueueBytes.RemoveRange(0, logTake);

                    int metricTake = Math.Min(batchSize, metricQueue.Count);
                    batchMetrics = metricQueue.GetRange(0, metricTake);
                    batchMetricBytes = metricQueueBytes.GetRange(0, metricTake);
                    metricQueue.RemoveRange(0, metricTake);
                    metricQueueBytes.RemoveRange(0, metricTake);

                    sentBytes = SumBytes(batchSpanBytes) + SumBytes(batchLogBytes) + SumBytes(batchMetricBytes);
                    queuedBytes -= sentBytes;
                    inflightSpans += batchSpans.Count;
                    inflightLogs += batchLogs.Count;
                    inflightMetrics += batchMetrics.Count;
                    inflightBytes += sentBytes;
                }

                try
                {
                    if (batchSpans.Count > 0)
                    {
                        IngestOutcome outcome = CanonIngest.IngestBatch(batchSpans, store, adapters);
                        totalAccepted += outcome.Accepted;
                        totalQuarantined += outcome.Quarantined;
                        totalRejected += outcome.Rejected;
                    }

                    lock (stateGate)
                    {
                        inflightSpans -= batchSpans.Count;
                        inflightLogs -= batchLogs.Count;
                        inflightMetrics -= batchMetrics.Count;
                        inflightBytes -= sentBytes;
                    }
                }
                catch
                {
                    lock (stateGate)
                    {
                        inflightSpans -= batchSpans.Count;
                        inflightLogs -= batchLogs.Count;
                        inflightMetrics -= batchMetrics.Count;
                        inflightBytes -= sentBytes;

                        spanQueue.InsertRange(0, batchSpans);
                        spanQueueBytes.InsertRange(0, batchSpanBytes);
                        logQueue.InsertRange(0, batchLogs);
                        logQueueBytes.InsertRange(0, batchLogBytes);
                        metricQueue.InsertRange(0, batchMetrics);
                        metricQueueBytes.InsertRange(0, batchMetricBytes);
                        queuedBytes += sentBytes;
                    }

                    throw;
                }
            }

            return new IngestOutcome(totalAccepted, totalQuarantined, totalRejected);
        }
    }

    private static void PushWithBytes<T>(
        List<T> queue,
        List<long> byteLedger,
        IReadOnlyList<T> items,
        long totalBytes)
    {
        if (items.Count == 0)
            return;

        long share = totalBytes / items.Count;
        long remainder = totalBytes - (share * items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            queue.Add(items[index]);
            byteLedger.Add(index == items.Count - 1 ? share + remainder : share);
        }
    }

    private static long SumBytes(List<long> bytes)
    {
        long total = 0;
        for (int i = 0; i < bytes.Count; i++)
            total += bytes[i];
        return total;
    }
}
