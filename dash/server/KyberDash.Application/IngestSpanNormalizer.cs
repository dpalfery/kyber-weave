using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Turns decoded OTLP wire spans into the canonical records the ingest pipeline would have accepted,
/// without persisting anything.
/// </summary>
/// <remarks>
/// The legacy receiver admitted spans through <see cref="CanonIngestPipeline.IngestBatch"/> directly.
/// In the layered application the same admission has to be expressed as "normalize, then hand the
/// records to <c>ITranscriptIngestionService</c>", because that service is the only thing allowed to
/// write records. Running the pipeline against a throwaway sink produces exactly the records the
/// pipeline would have written, so the ID contract, per-span accounting, and quarantine reasons stay
/// identical while the write goes through the ingestion service.
/// </remarks>
internal static class IngestSpanNormalizer
{
    /// <summary>
    /// Normalizes <paramref name="spans"/> into accepted records, quarantined spans, and problems.
    /// </summary>
    /// <param name="spans">Decoded OTLP spans to normalize.</param>
    /// <returns>Records the pipeline accepted plus everything it would have quarantined or reported.</returns>
    public static NormalizedSpanBatch Normalize(IReadOnlyList<OtlpSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count == 0)
            return NormalizedSpanBatch.Empty;

        PipelineSink sink = new();
        IngestOutcome outcome = CanonIngestPipeline.IngestBatch(spans, sink);
        return new NormalizedSpanBatch(sink.Records, sink.Quarantined, sink.Problems, outcome);
    }

    /// <summary>A normalization pass's accepted, quarantined, and rejected material.</summary>
    internal class NormalizedSpanBatch
    {
        /// <summary>An empty pass.</summary>
        public static NormalizedSpanBatch Empty { get; } = new([], [], [], new IngestOutcome(0, 0, 0));

        /// <summary>Initializes a batch from one normalization pass.</summary>
        /// <param name="records">Accepted canonical records, in input order.</param>
        /// <param name="quarantined">Spans moved to quarantine.</param>
        /// <param name="problems">Problems recorded for rejected spans.</param>
        /// <param name="outcome">Per-batch accepted, quarantined, and rejected counters.</param>
        public NormalizedSpanBatch(
            IReadOnlyList<CanonicalRecord> records,
            IReadOnlyList<QuarantineRow> quarantined,
            IReadOnlyList<SpanProblemRow> problems,
            IngestOutcome outcome)
        {
            Records = records;
            Quarantined = quarantined;
            Problems = problems;
            Outcome = outcome;
        }

        /// <summary>Accepted canonical records, in input order.</summary>
        public IReadOnlyList<CanonicalRecord> Records { get; }

        /// <summary>Spans the pipeline would have quarantined.</summary>
        public IReadOnlyList<QuarantineRow> Quarantined { get; }

        /// <summary>Problems the pipeline would have recorded for rejected spans.</summary>
        public IReadOnlyList<SpanProblemRow> Problems { get; }

        /// <summary>Per-batch accepted, quarantined, and rejected counters.</summary>
        public IngestOutcome Outcome { get; }

        /// <summary>Whether the pass carried no telemetry at all.</summary>
        public bool IsEmpty => Records.Count == 0 && Quarantined.Count == 0 && Problems.Count == 0;
    }

    /// <summary>
    /// Canonical store repository that keeps writes in memory so the pipeline can be run over
    /// decoded spans without touching a database.
    /// </summary>
    private class PipelineSink : ICanonStoreRepository
    {
        private readonly List<CanonicalRecord> records = [];
        private readonly List<QuarantineRow> quarantined = [];
        private readonly List<SpanProblemRow> problems = [];

        /// <summary>Records the pipeline accepted.</summary>
        public IReadOnlyList<CanonicalRecord> Records => records;

        /// <summary>Spans the pipeline quarantined.</summary>
        public IReadOnlyList<QuarantineRow> Quarantined => quarantined;

        /// <summary>Problems the pipeline recorded.</summary>
        public IReadOnlyList<SpanProblemRow> Problems => problems;

        /// <inheritdoc />
        public string DatabasePath => ":memory:";

        /// <inheritdoc />
        public void EnsureInitialized()
        {
        }

        /// <inheritdoc />
        public string? GetMetadata(string key) => null;

        /// <inheritdoc />
        public void SetMetadata(string key, string value)
        {
        }

        /// <inheritdoc />
        public bool CompareAndSwapMetadata(string key, string? expected, string? newValue) => false;

        /// <inheritdoc />
        public long RecordsWriteMarker() => 0;

        /// <inheritdoc />
        public int CountRecords() => records.Count;

        /// <inheritdoc />
        public IReadOnlyList<string> ListObservedHarnesses() =>
            [.. records.Select(static record => record.Harness).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        /// <inheritdoc />
        public void PutRecord(CanonicalRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            records.Add(record);
        }

        /// <inheritdoc />
        public void PutRecords(IEnumerable<CanonicalRecord> batch)
        {
            ArgumentNullException.ThrowIfNull(batch);
            foreach (CanonicalRecord record in batch)
                records.Add(record);
        }

        /// <inheritdoc />
        public CanonicalRecord? GetRecord(string spanId) =>
            records.FirstOrDefault(record => string.Equals(record.SpanId, spanId, StringComparison.Ordinal));

        /// <inheritdoc />
        public bool HasRecord(string spanId) => GetRecord(spanId) is not null;

        /// <inheritdoc />
        public IReadOnlyList<CanonicalRecord> QueryAllRecords() => records;

        /// <inheritdoc />
        public IReadOnlyList<CanonicalRecord> ListRecordsBySessionKey(string sessionKey) =>
            [.. records.Where(record =>
                string.Equals(record.SessionId ?? record.TraceId, sessionKey, StringComparison.Ordinal))];

        /// <inheritdoc />
        public void SetCosts(IReadOnlyDictionary<string, CostBlock> updates)
        {
        }

        /// <inheritdoc />
        public void QuarantineAndDelete(QuarantineRow row)
        {
            ArgumentNullException.ThrowIfNull(row);
            quarantined.Add(row);
        }

        /// <inheritdoc />
        public IReadOnlyList<QuarantineRow> GetQuarantined(string? harness = null) =>
            harness is null
                ? quarantined
                : [.. quarantined.Where(row => string.Equals(row.Harness, harness, StringComparison.Ordinal))];

        /// <inheritdoc />
        public void RecordProblem(SpanProblemRow problem)
        {
            ArgumentNullException.ThrowIfNull(problem);
            problems.Add(problem);
        }

        /// <inheritdoc />
        public void RecordProblems(IEnumerable<SpanProblemRow> batch)
        {
            ArgumentNullException.ThrowIfNull(batch);
            problems.AddRange(batch);
        }

        /// <inheritdoc />
        public IReadOnlyList<SpanProblemRow> GetProblems(string? spanId = null) =>
            spanId is null
                ? problems
                : [.. problems.Where(row => string.Equals(row.SpanId, spanId, StringComparison.Ordinal))];

        /// <inheritdoc />
        public void RecordIngest(string source, int count, string timestamp)
        {
        }

        /// <inheritdoc />
        public IReadOnlyList<IngestLogEntry> GetIngestLog(string? source = null) => [];

        /// <inheritdoc />
        public long? GetCachedTokenCount(string hash) => null;

        /// <inheritdoc />
        public void PutCachedTokenCounts(IReadOnlyDictionary<string, long> counts, string model)
        {
        }

        /// <inheritdoc />
        public void PutSession(SessionSummaryRow row)
        {
        }

        /// <inheritdoc />
        public void PutSessions(IEnumerable<SessionSummaryRow> rows)
        {
        }

        /// <inheritdoc />
        public SessionSummaryRow? GetSession(string sessionId) => null;

        /// <inheritdoc />
        public IReadOnlyList<SessionSummaryRow> ListSessions(string? harness = null) => [];

        /// <inheritdoc />
        public void DeleteSession(string sessionId)
        {
        }

        /// <inheritdoc />
        public void DeleteSessions(IEnumerable<string> sessionIds)
        {
        }

        /// <inheritdoc />
        public IReadOnlyList<(int TurnIndex, string TurnJson)> GetSessionTurns(string sessionId) => [];

        /// <inheritdoc />
        public DirtyProjectionState GetDirtyProjectionState() => new(0, 0, null, true);

        /// <inheritdoc />
        public void MarkProjectionClean(long writeSeq, string schemaVersion)
        {
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> GetProjectionSessionKeyMap() =>
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public void SaveProjectionSessionKeyMap(IReadOnlyDictionary<string, string> map)
        {
        }

        /// <inheritdoc />
        public void PutFinding(Finding finding)
        {
        }

        /// <inheritdoc />
        public void PutFindings(IEnumerable<Finding> findings)
        {
        }

        /// <inheritdoc />
        public Finding? GetFinding(string id) => null;

        /// <inheritdoc />
        public IReadOnlyList<Finding> ListFindings(string? runId = null, string? sessionId = null) => [];

        /// <inheritdoc />
        public void DeleteFindingsForSession(string sessionId)
        {
        }

        /// <inheritdoc />
        public void UpsertRuns(IEnumerable<RunRow> runs)
        {
        }

        /// <inheritdoc />
        public RunRow? GetRun(string runId) => null;

        /// <inheritdoc />
        public IReadOnlyList<RunRow> ListRuns(string? harness = null) => [];

        /// <inheritdoc />
        public void UpsertExecutions(IEnumerable<ExecutionRow> executions)
        {
        }

        /// <inheritdoc />
        public IReadOnlyList<ExecutionRow> ListExecutionsForRun(string runId) => [];

        /// <inheritdoc />
        public IReadOnlyList<ExecutionRow> ListExecutionsByHarness(string harness) => [];

        /// <inheritdoc />
        public void UpsertHarnessRollups(IEnumerable<HarnessRollupRow> rollups)
        {
        }

        /// <inheritdoc />
        public HarnessRollupRow? GetHarnessRollup(string harness) => null;

        /// <inheritdoc />
        public IReadOnlyList<HarnessRollupRow> ListHarnessRollups() => [];

        /// <inheritdoc />
        public void PutPrediction(PredictionRow prediction)
        {
        }

        /// <inheritdoc />
        public IReadOnlyList<PredictionRow> ListPredictions(string? runId = null) => [];

        /// <inheritdoc />
        public IReadOnlyList<StoredProblemRow> ListStoredProblems(string? harness = null, string? spanId = null) => [];

        /// <inheritdoc />
        public StoredProblemRow? GetStoredProblem(int id) => null;

        /// <inheritdoc />
        public IReadOnlyList<QuarantineRow> ListQuarantine(string? harness = null) => GetQuarantined(harness);

        /// <inheritdoc />
        public QuarantineRow? GetQuarantineItem(string spanId) =>
            quarantined.FirstOrDefault(row => string.Equals(row.SpanId, spanId, StringComparison.Ordinal));

        /// <inheritdoc />
        public SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey) => null;

        /// <inheritdoc />
        public void UpsertSourceCheckpoint(SourceCheckpoint checkpoint)
        {
        }

        /// <inheritdoc />
        public IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null) => [];

        /// <inheritdoc />
        public void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows)
        {
        }

        /// <inheritdoc />
        public void RecordRefreshRun(RefreshRunRow row)
        {
        }

        /// <inheritdoc />
        public IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20) => [];

        /// <inheritdoc />
        public void StorePendingLogs(IEnumerable<OtlpLog> logs, string? harness = null)
        {
        }

        /// <inheritdoc />
        public IReadOnlyList<OtlpLog> TakePendingLogs(int limit = 500) => [];

        /// <inheritdoc />
        public void StoreQuarantinedLogs(IEnumerable<(OtlpLog Log, string Reason)> logs, string? harness = null)
        {
        }

        /// <inheritdoc />
        public bool IsLogEnriched(string logId) => false;

        /// <inheritdoc />
        public void MarkLogsEnriched(IEnumerable<string> logIds)
        {
        }

        /// <inheritdoc />
        public void StoreMetrics(IEnumerable<OtlpMetric> metrics, string? source = null)
        {
        }

        /// <inheritdoc />
        public int PurgeContentBefore(string cutoffUtc) => 0;

        /// <inheritdoc />
        public StoreWipeReport WipeAll() =>
            new(0, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        /// <inheritdoc />
        public StoreWipeReport WipeHarnesses(IEnumerable<string> harnesses) => WipeAll();

        /// <inheritdoc />
        public void Vacuum()
        {
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetBuiltSessionIds() => [];

        /// <inheritdoc />
        public IReadOnlyList<string> GetDistinctSessionKeys() =>
            [.. records
                .Select(static record => !string.IsNullOrEmpty(record.SessionId) ? record.SessionId : record.TraceId)
                .Where(static key => !string.IsNullOrEmpty(key))
                .Select(static key => key!)
                .Distinct(StringComparer.Ordinal)];

        /// <inheritdoc />
        public IReadOnlyList<(string Key, string Harness)> GetSessionKeyHarnesses() =>
            [.. records
                .Select(static record => (!string.IsNullOrEmpty(record.SessionId) ? record.SessionId : record.TraceId, record.Harness))
                .Where(static pair => !string.IsNullOrEmpty(pair.Item1))
                .Select(static pair => (pair.Item1!, pair.Harness))
                .Distinct()];

        /// <inheritdoc />
        public bool HasCurrentDetectorVersion() => false;

        /// <inheritdoc />
        public void SetDetectorVersion(int version = CanonSchema.DetectorVersion)
        {
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? GetProjectedSessionKeyMap() => null;

        /// <inheritdoc />
        public void RecordProjectedSessionKeys(long seq, IReadOnlyDictionary<string, IReadOnlyList<string>> sessionIdsByKey)
        {
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, int> CountRecordsBySource() =>
            records
                .Where(static record => !string.IsNullOrEmpty(record.Source))
                .GroupBy(static record => record.Source, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

        /// <inheritdoc />
        public long? GetMaxRecordRowId() => records.Count > 0 ? records.Count : null;

        /// <inheritdoc />
        public uint ComputeStoreGeneration() => (uint)records.Count;

        /// <inheritdoc />
        public IReadOnlyList<JsonElement> ListSessionTurnPayloads(string sessionId) => [];

        /// <inheritdoc />
        public JsonElement? GetSessionTurnPayload(string sessionId, int turnIndex) => null;

        /// <inheritdoc />
        public void DeleteHarnessRollup(string harness)
        {
        }

        /// <inheritdoc />
        public int CountQuarantine(string? harness = null) => GetQuarantined(harness).Count;

        /// <inheritdoc />
        public IReadOnlyList<QuarantineRow> ListQuarantinePage(string? harness = null, int? limit = null, int? offset = null)
        {
            IEnumerable<QuarantineRow> query = GetQuarantined(harness);
            if (offset.HasValue && offset.Value > 0)
            {
                query = query.Skip(offset.Value);
            }

            if (limit.HasValue)
            {
                query = query.Take(Math.Max(1, limit.Value));
            }

            return [.. query];
        }

        /// <inheritdoc />
        public int CountProblems(string? harness = null) => ListStoredProblems(harness).Count;

        /// <inheritdoc />
        public StoredProblemRow? GetProblemById(int id) => GetStoredProblem(id);

        /// <inheritdoc />
        public IReadOnlyList<StoredProblemRow> ListProblems(string? harness = null, int? limit = null, int? offset = null)
        {
            IEnumerable<StoredProblemRow> query = ListStoredProblems(harness);
            if (offset.HasValue && offset.Value > 0)
            {
                query = query.Skip(offset.Value);
            }

            if (limit.HasValue)
            {
                query = query.Take(Math.Max(1, limit.Value));
            }

            return [.. query];
        }

        /// <inheritdoc />
        public IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey) => [];

        /// <inheritdoc />
        public IReadOnlyList<CanonicalRecord> ListOtlpRecords() =>
            [.. records.Where(static record => !record.SpanId.StartsWith("synth:", StringComparison.Ordinal))];

        /// <inheritdoc />
        public void CommitSourceUnit(
            SourceCheckpoint checkpoint,
            IReadOnlyList<CanonicalRecord> batch,
            IReadOnlyList<RecordProvenanceRow> provenances,
            IReadOnlyList<SpanProblemRow>? batchProblems = null)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(provenances);
            PutRecords(batch);
            if (batchProblems is not null)
            {
                RecordProblems(batchProblems);
            }
        }

        /// <inheritdoc />
        public string StartRefreshRun(string id, string trigger, string startedAtUtc, int pid, int? historyWeeks = null) => id;

        /// <inheritdoc />
        public void CompleteRefreshRun(string id, string status, string completedAtUtc, string? summary)
        {
        }

        /// <inheritdoc />
        public int ReconcileDeadRefreshRuns(string completedAtUtc, long maxAgeMs = 3_600_000) => 0;
    }
}
