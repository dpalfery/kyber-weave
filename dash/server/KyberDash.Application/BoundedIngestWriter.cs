using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Bounded in-memory OTLP ingest buffer that admits decoded spans, logs, and metrics
/// subject to per-batch and backlog ceilings, then flushes them into the relational store.
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

    private static readonly JsonWriterOptions CanonicalWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
    /// <param name="batchSize">Maximum spans drained per <see cref="CanonIngestPipeline"/> batch.</param>
    /// <param name="maxBodyBytes">Per-admission decoded payload ceiling in bytes.</param>
    /// <param name="maxPendingBytes">Queued plus in-flight byte ceiling triggering 503 backpressure.</param>
    /// <param name="maxPendingItems">Queued plus in-flight item count ceiling triggering 503 backpressure.</param>
    /// <param name="ingestion">
    /// Ingestion service spans are handed to on flush. When omitted, spans are normalized with the
    /// canonical pipeline and persisted straight to the store.
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

            if (spans.Count > 0)
            {
                queuedBytes += wireBytes;
            }

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

            // Same accounting as spans: charge the request against its first item so the drain
            // releases exactly the bytes admission charged.
            for (int index = 0; index < logs.Count; index++)
            {
                logQueue.Enqueue((logs[index], index == 0 ? wireBytes : 0L));
            }

            if (logs.Count > 0)
            {
                queuedBytes += wireBytes;
            }

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

            for (int index = 0; index < metrics.Count; index++)
            {
                metricQueue.Enqueue((metrics[index], index == 0 ? wireBytes : 0L));
            }

            if (metrics.Count > 0)
            {
                queuedBytes += wireBytes;
            }

            return new IngestAdmissionResult(true, 200);
        }
    }

    /// <summary>
    /// Drains queued spans, logs, and metrics into <paramref name="writer"/> and
    /// <paramref name="operationalStore"/> in bounded batches, mapping logs on receipt via
    /// <see cref="OtlpLogMapper"/> without writing to removed pending log tables.
    /// </summary>
    /// <param name="writer">Relational store writer receiving normalized spans, mapped logs, and problems.</param>
    /// <param name="operationalStore">Operational store receiving quarantined spans, metric points, and ingest audit rows.</param>
    /// <returns>Aggregate ingest outcome across drained span and log batches.</returns>
    public IngestOutcome Flush(IRelationalStoreWriter writer, IOperationalStore operationalStore) =>
        FlushRelational(writer, operationalStore).Outcome;

    /// <summary>
    /// Transitional overload draining queued telemetry into <paramref name="store"/> for remaining
    /// legacy test callers not yet migrated to <see cref="IRelationalStoreWriter"/>.
    /// </summary>
    /// <param name="store">Legacy canonical store repository.</param>
    /// <returns>Aggregate ingest outcome across drained span and log batches.</returns>
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
                (List<OtlpSpan> batch, long batchBytes) = DequeueSpanBatch();
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
                    ReleaseInflightSpans(batch.Count, batchBytes);
                }
            }

            (List<OtlpLog> logs, long drainedLogBytes) = DequeueAllLogs();
            try
            {
                if (logs.Count > 0)
                {
                    OtlpLogMapResult mapped = OtlpLogMapper.Map(logs);
                    foreach (CollectedBatch batch in mapped.Batches)
                    {
                        foreach (CollectedProblem problem in batch.Problems)
                        {
                            store.RecordProblem(ToLegacyProblem(problem));
                        }
                    }

                    foreach (CollectedProblem problem in mapped.StandaloneProblems)
                    {
                        store.RecordProblem(ToLegacyProblem(problem));
                    }

                    rejected += mapped.Outcome.Rejected;
                }
            }
            finally
            {
                ReleaseInflightLogs(logs.Count, drainedLogBytes);
            }

            (List<OtlpMetric> metrics, long drainedMetricBytes) = DequeueAllMetrics();
            try
            {
                if (metrics.Count > 0)
                {
                    store.StoreMetrics(metrics);
                }
            }
            finally
            {
                ReleaseInflightMetrics(metrics.Count, drainedMetricBytes);
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

    internal (IngestOutcome Outcome, int MetricPointsWritten) FlushRelational(
        IRelationalStoreWriter writer,
        IOperationalStore operationalStore)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(operationalStore);

        lock (flushGate)
        {
            int accepted = 0;
            int quarantined = 0;
            int rejected = 0;
            int metricPointsWritten = 0;

            while (true)
            {
                (List<OtlpSpan> batch, long batchBytes) = DequeueSpanBatch();
                if (batch.Count == 0)
                {
                    break;
                }

                try
                {
                    IngestOutcome outcome = IngestDrainedBatch(batch, writer, operationalStore);
                    accepted += outcome.Accepted;
                    quarantined += outcome.Quarantined;
                    rejected += outcome.Rejected;
                }
                finally
                {
                    ReleaseInflightSpans(batch.Count, batchBytes);
                }
            }

            (List<OtlpLog> logs, long drainedLogBytes) = DequeueAllLogs();
            try
            {
                if (logs.Count > 0)
                {
                    OtlpLogMapResult mapped = OtlpLogMapper.Map(logs);
                    foreach (CollectedBatch batch in mapped.Batches)
                    {
                        writer.WriteBatch(batch);
                    }

                    foreach (CollectedProblem problem in mapped.StandaloneProblems)
                    {
                        writer.WriteProblem(problem);
                    }

                    accepted += mapped.Outcome.Accepted;
                    rejected += mapped.Outcome.Rejected;
                }
            }
            finally
            {
                ReleaseInflightLogs(logs.Count, drainedLogBytes);
            }

            (List<OtlpMetric> metrics, long drainedMetricBytes) = DequeueAllMetrics();
            try
            {
                if (metrics.Count > 0)
                {
                    IReadOnlyList<StoredMetricPoint> points = MapMetricPoints(metrics);
                    if (points.Count > 0)
                    {
                        operationalStore.PutMetricPoints(points);
                        metricPointsWritten = points.Count;
                    }
                }
            }
            finally
            {
                ReleaseInflightMetrics(metrics.Count, drainedMetricBytes);
            }

            if (accepted > 0)
            {
                operationalStore.RecordIngest(
                    "otlp",
                    accepted,
                    DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            }

            return (new IngestOutcome(accepted, quarantined, rejected), metricPointsWritten);
        }
    }

    private (List<OtlpSpan> Batch, long Bytes) DequeueSpanBatch()
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

        return (batch, batchBytes);
    }

    private void ReleaseInflightSpans(int count, long bytes)
    {
        lock (stateGate)
        {
            inflightSpans -= count;
            inflightBytes -= bytes;
        }
    }

    private (List<OtlpLog> Logs, long Bytes) DequeueAllLogs()
    {
        List<OtlpLog> logs = [];
        long drainedBytes = 0L;
        lock (stateGate)
        {
            while (logQueue.Count > 0)
            {
                (OtlpLog log, long bytes) = logQueue.Dequeue();
                logs.Add(log);
                drainedBytes += bytes;
            }

            inflightLogs += logs.Count;
            inflightBytes += drainedBytes;
            queuedBytes -= drainedBytes;
        }

        return (logs, drainedBytes);
    }

    private void ReleaseInflightLogs(int count, long bytes)
    {
        lock (stateGate)
        {
            inflightLogs -= count;
            inflightBytes -= bytes;
        }
    }

    private (List<OtlpMetric> Metrics, long Bytes) DequeueAllMetrics()
    {
        List<OtlpMetric> metrics = [];
        long drainedBytes = 0L;
        lock (stateGate)
        {
            while (metricQueue.Count > 0)
            {
                (OtlpMetric metric, long bytes) = metricQueue.Dequeue();
                metrics.Add(metric);
                drainedBytes += bytes;
            }

            inflightMetrics += metrics.Count;
            inflightBytes += drainedBytes;
            queuedBytes -= drainedBytes;
        }

        return (metrics, drainedBytes);
    }

    private void ReleaseInflightMetrics(int count, long bytes)
    {
        lock (stateGate)
        {
            inflightMetrics -= count;
            inflightBytes -= bytes;
        }
    }

    private IngestOutcome IngestDrainedBatch(
        IReadOnlyList<OtlpSpan> batch,
        IRelationalStoreWriter writer,
        IOperationalStore operationalStore)
    {
        if (ingestion is null)
        {
            return CanonIngestPipeline.IngestBatch(batch, writer, operationalStore);
        }

        IngestSpanNormalizer.NormalizedSpanBatch normalized = IngestSpanNormalizer.Normalize(batch);
        if (normalized.Records.Count == 0)
        {
            foreach (StoredQuarantinedSpan quarantinedSpan in normalized.QuarantinedSpans)
            {
                operationalStore.PutQuarantinedSpan(quarantinedSpan);
            }

            foreach (CollectedProblem problem in normalized.StandaloneProblems)
            {
                writer.WriteProblem(problem);
            }

            return normalized.Outcome;
        }

        _ = ingestion.IngestSpans(batch);
        return normalized.Outcome;
    }

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

    private static List<StoredMetricPoint> MapMetricPoints(IReadOnlyList<OtlpMetric> metrics)
    {
        List<StoredMetricPoint> points = [];
        foreach (OtlpMetric metric in metrics)
        {
            ArgumentNullException.ThrowIfNull(metric);
            if (string.IsNullOrWhiteSpace(metric.Name)
                || JsonDocumentText.IsDocument(metric.Name)
                || string.IsNullOrWhiteSpace(metric.Kind)
                || JsonDocumentText.IsDocument(metric.Kind))
            {
                continue;
            }

            string? unit = SafeOptionalText(metric.Unit);
            string? source = ExtractMetricSource(metric.Resource);

            if (metric.DataPoints.Count == 0)
            {
                points.Add(new StoredMetricPoint(
                    PointKey: metric.Name,
                    Name: metric.Name,
                    Kind: metric.Kind,
                    Unit: unit,
                    AggregationTemporality: metric.AggregationTemporality,
                    Source: source));
                continue;
            }

            foreach (OtlpMetricDataPoint pt in metric.DataPoints)
            {
                ArgumentNullException.ThrowIfNull(pt);
                string attrsHash = HashAttributes(pt.Attributes);
                string? timeUnixNano = SafeOptionalText(pt.TimeUnixNano);
                string? startTimeUnixNano = SafeOptionalText(pt.StartTimeUnixNano);
                string? timestampUtc = TryFormatNanoTimestampUtc(pt.TimeUnixNano) ?? timeUnixNano;
                string pointKey = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{metric.Name}|{timeUnixNano ?? string.Empty}|{attrsHash}");

                double? value = pt.Value is double v && double.IsFinite(v) ? v : null;
                long? count = pt.Count is double c && double.IsFinite(c) && c >= long.MinValue && c <= long.MaxValue
                    ? (long)Math.Truncate(c)
                    : null;
                double? sum = pt.Sum is double s && double.IsFinite(s) ? s : null;

                points.Add(new StoredMetricPoint(
                    PointKey: pointKey,
                    Name: metric.Name,
                    Kind: metric.Kind,
                    Unit: unit,
                    AggregationTemporality: metric.AggregationTemporality,
                    TimeUnixNano: timeUnixNano,
                    StartTimeUnixNano: startTimeUnixNano,
                    TimestampUtc: timestampUtc,
                    Value: value,
                    PointCount: count,
                    PointSum: sum,
                    Source: source));
            }
        }

        return points;
    }

    private static string? ExtractMetricSource(IReadOnlyDictionary<string, JsonElement> resource)
    {
        if (resource.TryGetValue("service.name", out JsonElement serviceEl)
            && serviceEl.ValueKind == JsonValueKind.String)
        {
            return SafeOptionalText(serviceEl.GetString());
        }

        return null;
    }

    private static string? TryFormatNanoTimestampUtc(string? nanoText)
    {
        if (string.IsNullOrWhiteSpace(nanoText)
            || !BigInteger.TryParse(nanoText, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger nanos))
        {
            return null;
        }

        BigInteger millis = nanos / 1_000_000;
        if (millis < long.MinValue || millis > long.MaxValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)millis)
                .UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string HashAttributes(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, CanonicalWriterOptions))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, JsonElement> entry in attributes.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(entry.Key);
                entry.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan))[..16];
    }

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;

    private static SpanProblemRow ToLegacyProblem(CollectedProblem problem) =>
        new(
            SpanId: problem.SpanId ?? problem.Location ?? problem.ProblemKey,
            Severity: problem.Severity,
            Code: problem.Code,
            Message: problem.Message,
            Stack: problem.Stack,
            Location: problem.Location,
            SessionId: problem.SessionKey,
            Harness: problem.HarnessId,
            Timestamp: problem.TimestampUtc);
}
