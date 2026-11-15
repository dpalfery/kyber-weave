using System.Globalization;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Bounded in-memory OTLP ingest buffer that admits decoded spans, logs, and metrics
/// subject to per-batch and backlog ceilings, then flushes them into the canonical store.
/// </summary>
public class BoundedIngestWriter
{
    /// <summary>Default per-batch drain size used when flushing queued telemetry.</summary>
    public const int DefaultBatchSize = 512;

    /// <summary>Default maximum decoded payload bytes accepted per admission.</summary>
    public const long DefaultMaxBodyBytes = OtlpCollector.DefaultMaxBodyBytes;

    /// <summary>Default maximum queued plus in-flight bytes before backpressure (503).</summary>
    public const long DefaultMaxPendingBytes = 256L * 1024 * 1024;

    /// <summary>Default high-water mark (multiples of <see cref="DefaultBatchSize"/>) for item backpressure.</summary>
    public const int DefaultHighWaterMark = DefaultBatchSize * 8;

    /// <summary>Default maximum queued plus in-flight items before backpressure (503).</summary>
    public const int DefaultMaxPendingItems = DefaultHighWaterMark * 4;

    /// <summary>Seconds advertised in <c>Retry-After</c> when the backlog is full.</summary>
    public const int RetryAfterSeconds = 1;

    private readonly Lock stateGate = new();
    private readonly Lock flushGate = new();
    private readonly int batchSize;
    private readonly long maxBodyBytes;
    private readonly long maxPendingBytes;
    private readonly int maxPendingItems;
    private readonly ITranscriptIngestionService? ingestion;

    private readonly Queue<(OtlpSpan Span, long Bytes)> spanQueue = new();
    private readonly Queue<(OtlpLog Log, long Bytes)> logQueue = new();
    private readonly Queue<(OtlpMetric Metric, long Bytes)> metricQueue = new();
    private long queuedBytes;
    private long inflightBytes;
    private int inflightSpans;
    private int inflightLogs;
    private int inflightMetrics;

    /// <summary>
    /// Initializes a new <see cref="BoundedIngestWriter"/> with configurable batch and backlog bounds.
    /// </summary>
    /// <param name="batchSize">Number of spans drained per store batch.</param>
    /// <param name="maxBodyBytes">Maximum decoded payload bytes accepted per admission.</param>
    /// <param name="maxPendingBytes">Maximum queued plus in-flight bytes before backpressure.</param>
    /// <param name="maxPendingItems">Maximum queued plus in-flight items before backpressure.</param>
    /// <param name="ingestion">
    /// Ingestion service spans are handed to on flush. When omitted, spans are normalized with the
    /// canonical pipeline and persisted straight to the store, which is what the legacy writer did.
    /// </param>
    public BoundedIngestWriter(
        int batchSize = DefaultBatchSize,
        long maxBodyBytes = DefaultMaxBodyBytes,
        long maxPendingBytes = DefaultMaxPendingBytes,
        int maxPendingItems = DefaultMaxPendingItems,
        ITranscriptIngestionService? ingestion = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBodyBytes, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPendingBytes, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPendingItems, 0);

        this.batchSize = batchSize;
        this.maxBodyBytes = maxBodyBytes;
        this.maxPendingBytes = maxPendingBytes;
        this.maxPendingItems = maxPendingItems;
        this.ingestion = ingestion;
    }

    /// <summary>Total accounted bytes currently queued or in flight to the store.</summary>
    public long PendingBytes
    {
        get
        {
            lock (stateGate)
            {
                return queuedBytes + inflightBytes;
            }
        }
    }

    /// <summary>Total decoded spans currently queued or in flight.</summary>
    public int PendingSpans
    {
        get
        {
            lock (stateGate)
            {
                return spanQueue.Count + inflightSpans;
            }
        }
    }

    /// <summary>Total decoded logs currently queued or in flight.</summary>
    public int PendingLogs
    {
        get
        {
            lock (stateGate)
            {
                return logQueue.Count + inflightLogs;
            }
        }
    }

    /// <summary>Total decoded metrics currently queued or in flight.</summary>
    public int PendingMetrics
    {
        get
        {
            lock (stateGate)
            {
                return metricQueue.Count + inflightMetrics;
            }
        }
    }

    /// <summary>Total queued plus in-flight spans, logs, and metrics combined.</summary>
    public int PendingItems
    {
        get
        {
            lock (stateGate)
            {
                return spanQueue.Count + logQueue.Count + metricQueue.Count
                    + inflightSpans + inflightLogs + inflightMetrics;
            }
        }
    }

    /// <summary>
    /// Attempts to enqueue decoded <paramref name="spans"/> accounting <paramref name="wireBytes"/>.
    /// </summary>
    /// <param name="spans">Decoded OTLP spans to queue.</param>
    /// <param name="wireBytes">Decoded payload size charged against the backlog budget.</param>
    /// <returns>Admission result (200 admitted, 413 oversize, 503 backpressure).</returns>
    public IngestAdmissionResult TryEnqueueSpans(IReadOnlyList<OtlpSpan> spans, long wireBytes)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (wireBytes > maxBodyBytes)
        {
            return new IngestAdmissionResult(
                false,
                413,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"OTLP payload of {wireBytes} bytes exceeds the {maxBodyBytes} byte limit."));
        }

        lock (stateGate)
        {
            if (queuedBytes + inflightBytes + wireBytes > maxPendingBytes
                || spanQueue.Count + logQueue.Count + metricQueue.Count
                    + inflightSpans + inflightLogs + inflightMetrics + spans.Count > maxPendingItems)
            {
                return new IngestAdmissionResult(false, 503, RetryAfterSeconds, "Ingest backlog is full; retry after backoff.");
            }

            // Charge the whole request against the first span of the batch so draining it releases
            // exactly the bytes admission added.
            for (int index = 0; index < spans.Count; index++)
            {
                spanQueue.Enqueue((spans[index], index == 0 ? wireBytes : 0L));
            }

            queuedBytes += wireBytes;
            return new IngestAdmissionResult(true, 200);
        }
    }

    /// <summary>
    /// Attempts to enqueue decoded <paramref name="logs"/> accounting <paramref name="wireBytes"/>.
    /// </summary>
    /// <param name="logs">Decoded OTLP logs to queue.</param>
    /// <param name="wireBytes">Decoded payload size charged against the backlog budget.</param>
    /// <returns>Admission result (200 admitted, 413 oversize, 503 backpressure).</returns>
    public IngestAdmissionResult TryEnqueueLogs(IReadOnlyList<OtlpLog> logs, long wireBytes)
    {
        ArgumentNullException.ThrowIfNull(logs);
        if (wireBytes > maxBodyBytes)
        {
            return new IngestAdmissionResult(
                false,
                413,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"OTLP payload of {wireBytes} bytes exceeds the {maxBodyBytes} byte limit."));
        }

        lock (stateGate)
        {
            if (queuedBytes + inflightBytes + wireBytes > maxPendingBytes
                || spanQueue.Count + logQueue.Count + metricQueue.Count
                    + inflightSpans + inflightLogs + inflightMetrics + logs.Count > maxPendingItems)
            {
                return new IngestAdmissionResult(false, 503, RetryAfterSeconds, "Ingest backlog is full; retry after backoff.");
            }

            foreach (OtlpLog log in logs)
            {
                logQueue.Enqueue((log, 0L));
            }

            queuedBytes += wireBytes;
            return new IngestAdmissionResult(true, 200);
        }
    }

    /// <summary>
    /// Attempts to enqueue decoded <paramref name="metrics"/> accounting <paramref name="wireBytes"/>.
    /// </summary>
    /// <param name="metrics">Decoded OTLP metrics to queue.</param>
    /// <param name="wireBytes">Decoded payload size charged against the backlog budget.</param>
    /// <returns>Admission result (200 admitted, 413 oversize, 503 backpressure).</returns>
    public IngestAdmissionResult TryEnqueueMetrics(IReadOnlyList<OtlpMetric> metrics, long wireBytes)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (wireBytes > maxBodyBytes)
        {
            return new IngestAdmissionResult(
                false,
                413,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"OTLP payload of {wireBytes} bytes exceeds the {maxBodyBytes} byte limit."));
        }

        lock (stateGate)
        {
            if (queuedBytes + inflightBytes + wireBytes > maxPendingBytes
                || spanQueue.Count + logQueue.Count + metricQueue.Count
                    + inflightSpans + inflightLogs + inflightMetrics + metrics.Count > maxPendingItems)
            {
                return new IngestAdmissionResult(false, 503, RetryAfterSeconds, "Ingest backlog is full; retry after backoff.");
            }

            foreach (OtlpMetric metric in metrics)
            {
                metricQueue.Enqueue((metric, 0L));
            }

            queuedBytes += wireBytes;
            return new IngestAdmissionResult(true, 200);
        }
    }

    /// <summary>
    /// Drains queued spans, logs, and metrics into <paramref name="store"/> in bounded batches.
    /// </summary>
    /// <param name="store">Canonical store repository receiving flushed telemetry.</param>
    /// <returns>Aggregate ingest outcome across all drained span batches.</returns>
    public IngestOutcome Flush(ICanonStoreRepository store)
    {
        ArgumentNullException.ThrowIfNull(store);

        lock (flushGate)
        {
            int accepted = 0;
            int quarantined = 0;
            int rejected = 0;

            while (true)
            {
                List<OtlpSpan> batch = [];
                long batchBytes = 0L;
                lock (stateGate)
                {
                    while (batch.Count < batchSize && spanQueue.Count > 0)
                    {
                        (OtlpSpan span, long bytes) = spanQueue.Dequeue();
                        batch.Add(span);
                        batchBytes += bytes;
                    }

                    inflightSpans += batch.Count;
                    inflightBytes += batchBytes;
                    queuedBytes -= batchBytes;
                }

                if (batch.Count == 0)
                {
                    break;
                }

                try
                {
                    IngestOutcome outcome = IngestDrainedBatch(batch, store);
                    accepted += outcome.Accepted;
                    quarantined += outcome.Quarantined;
                    rejected += outcome.Rejected;
                }
                finally
                {
                    lock (stateGate)
                    {
                        inflightSpans -= batch.Count;
                        inflightBytes -= batchBytes;
                    }
                }
            }

            List<OtlpLog> logs = [];
            lock (stateGate)
            {
                while (logQueue.Count > 0)
                {
                    logs.Add(logQueue.Dequeue().Log);
                }

                inflightLogs += logs.Count;
            }

            try
            {
                if (logs.Count > 0)
                {
                    store.StorePendingLogs(logs);
                }
            }
            finally
            {
                lock (stateGate)
                {
                    inflightLogs -= logs.Count;
                }
            }

            List<OtlpMetric> metrics = [];
            lock (stateGate)
            {
                while (metricQueue.Count > 0)
                {
                    metrics.Add(metricQueue.Dequeue().Metric);
                }

                inflightMetrics += metrics.Count;
            }

            try
            {
                if (metrics.Count > 0)
                {
                    store.StoreMetrics(metrics);
                }
            }
            finally
            {
                lock (stateGate)
                {
                    inflightMetrics -= metrics.Count;
                }
            }

            if (accepted > 0)
            {
                store.RecordIngest(
                    "otlp",
                    accepted,
                    DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            }

            return new IngestOutcome(accepted, quarantined, rejected);
        }
    }

    /// <summary>
    /// Ingests one drained span batch, through the ingestion service when one was supplied.
    /// </summary>
    /// <param name="batch">Decoded spans drained from the queue.</param>
    /// <param name="store">Canonical store receiving logs, metrics, and the ingest audit row.</param>
    /// <returns>Accepted, quarantined, and rejected counters for the batch.</returns>
    private IngestOutcome IngestDrainedBatch(IReadOnlyList<OtlpSpan> batch, ICanonStoreRepository store)
    {
        if (ingestion is null)
        {
            return CanonIngestPipeline.IngestBatch(batch, store);
        }

        IngestSpanNormalizer.NormalizedSpanBatch normalized = IngestSpanNormalizer.Normalize(batch);
        foreach (QuarantineRow row in normalized.Quarantined)
        {
            store.QuarantineAndDelete(row);
        }

        if (normalized.Problems.Count > 0)
        {
            store.RecordProblems(normalized.Problems);
        }

        if (normalized.Records.Count == 0)
        {
            return normalized.Outcome;
        }

        _ = ingestion.IngestSpans(batch);
        return normalized.Outcome;
    }
}
