using Xunit;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Tests;

/// <summary>Rate-table loading, pricing-provider registration, and model-catalog status for <see cref="PricingRepository"/>.</summary>
public class PricingRepositoryTests : IDisposable
{
    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        // Restore the process-wide provider so other tests are not left with a repository-backed one.
        CanonPricing.ConfigurePricingProvider(() => CanonPricing.BuildPricingTables(
            new Dictionary<string, ModelCosts>(StringComparer.Ordinal),
            new Dictionary<string, ModelCosts>(StringComparer.Ordinal)));
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void LoadPricingTables_ReadsEmbeddedSnapshotAndFallback()
    {
        PricingRepository repository = new();

        PricingTables tables = repository.LoadPricingTables();

        Assert.True(tables.PricingCache.Count > 1000);
        Assert.NotEmpty(tables.LowercaseIndex);
        Assert.NotEmpty(tables.SortedKeys);
        Assert.Contains("anthropic", tables.KnownNamespaces);
        Assert.DoesNotContain("ollama", tables.KnownNamespaces);
    }

    [Fact]
    public void LoadPricingTables_IncludesComposerOverridesAndCacheMultipliers()
    {
        PricingRepository repository = new();

        PricingTables tables = repository.LoadPricingTables();

        Assert.True(tables.PricingCache.TryGetValue("composer-2.5", out ModelCosts? composer));
        Assert.Equal(0.5e-6, composer!.InputCostPerToken);
        Assert.Equal(2.5e-6, composer.OutputCostPerToken);
        Assert.True(composer.CacheWriteCostIsExplicit);

        ModelCosts derived = tables.PricingCache.Values.First(static costs => !costs.CacheWriteCostIsExplicit && costs.InputCostPerToken > 0);
        Assert.Equal(derived.InputCostPerToken * 1.25, derived.CacheWriteCostPerToken);
        Assert.Equal(derived.InputCostPerToken * 0.1, derived.CacheReadCostPerToken);
    }

    [Fact]
    public void LoadPricingTables_IsMemoizedPerRepository()
    {
        PricingRepository repository = new();

        PricingTables first = repository.LoadPricingTables();
        PricingTables second = repository.LoadPricingTables();

        Assert.Same(first, second);
    }

    [Fact]
    public void Constructor_RegistersItselfAsTheProcessPricingProvider()
    {
        CanonStoreRepository store = new(Path.Combine(directory.Path, "canon.db"), new KyberDash.Core.Resilience.SqliteCircuitBreaker());
        _ = new PricingRepository(store);
        TokenUsage tokens = new(FreshInput: 1_000_000, CacheRead: 0, CacheCreation: 0, Output: 1_000_000, ReportedInput: 1_000_000, ReportedOutput: 1_000_000);

        CostBlock published = CanonPricing.PriceTurn(tokens, "claude-opus-5", "claude-code");
        CostBlock withoutStore = CanonPricing.PriceTurn(tokens, "claude-opus-5", "codex-cli");

        Assert.Equal(CostBasis.Published, published.Basis);
        Assert.Equal(CostStatus.Priced, published.Status);
        Assert.True(published.Value > 0);
        Assert.Equal(published.Value, withoutStore.Value);
    }

    [Fact]
    public void GetModelCatalogSnapshot_ReportsTheEmbeddedSourceWithoutAStore()
    {
        PricingRepository repository = new();

        JsonElement snapshot = repository.GetModelCatalogSnapshot();

        Assert.Equal(PricingRepository.EmbeddedSnapshotSource, snapshot.GetProperty("source").GetString());
        Assert.Equal("embedded", snapshot.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Array, snapshot.GetProperty("models").ValueKind);
    }

    [Fact]
    public void GetModelCatalogSnapshot_ReadsStatusFromTheStore()
    {
        CanonStoreRepository store = new(Path.Combine(directory.Path, "canon.db"), new KyberDash.Core.Resilience.SqliteCircuitBreaker());
        store.SetMetadata(PricingRepository.CatalogStatusMetadataKey, "refreshed");
        PricingRepository repository = new(store);

        JsonElement snapshot = repository.GetModelCatalogSnapshot();

        Assert.Equal("refreshed", snapshot.GetProperty("status").GetString());
    }

    [Fact]
    public void GetModelCatalogSnapshot_StaysAdvisoryWhenTheStoreFails()
    {
        PricingRepository repository = new(new ThrowingStoreRepository());

        JsonElement snapshot = repository.GetModelCatalogSnapshot();

        Assert.Equal("unknown", snapshot.GetProperty("status").GetString());
    }

    /// <summary>Store whose metadata reads fail, to prove catalog status never breaks a read.</summary>
    private class ThrowingStoreRepository : ICanonStoreRepository
    {
        public string DatabasePath => ":memory:";

        public string? GetMetadata(string key) => throw new InvalidOperationException("store offline");

        public void EnsureInitialized() => throw new InvalidOperationException("store offline");

        public void SetMetadata(string key, string value) => throw new InvalidOperationException("store offline");

        public bool CompareAndSwapMetadata(string key, string? expected, string? newValue) => throw new InvalidOperationException("store offline");

        public long RecordsWriteMarker() => throw new InvalidOperationException("store offline");

        public int CountRecords() => throw new InvalidOperationException("store offline");

        public IReadOnlyList<string> ListObservedHarnesses() => throw new InvalidOperationException("store offline");

        public void PutRecord(CanonicalRecord record) => throw new InvalidOperationException("store offline");

        public void PutRecords(IEnumerable<CanonicalRecord> records) => throw new InvalidOperationException("store offline");

        public CanonicalRecord? GetRecord(string spanId) => throw new InvalidOperationException("store offline");

        public bool HasRecord(string spanId) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<CanonicalRecord> QueryAllRecords() => throw new InvalidOperationException("store offline");

        public IReadOnlyList<CanonicalRecord> ListRecordsBySessionKey(string sessionKey) => throw new InvalidOperationException("store offline");

        public void SetCosts(IReadOnlyDictionary<string, CostBlock> updates) => throw new InvalidOperationException("store offline");

        public void QuarantineAndDelete(QuarantineRow row) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<QuarantineRow> GetQuarantined(string? harness = null) => throw new InvalidOperationException("store offline");

        public void RecordProblem(SpanProblemRow problem) => throw new InvalidOperationException("store offline");

        public void RecordProblems(IEnumerable<SpanProblemRow> problems) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<SpanProblemRow> GetProblems(string? spanId = null) => throw new InvalidOperationException("store offline");

        public void RecordIngest(string source, int count, string timestamp) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<IngestLogEntry> GetIngestLog(string? source = null) => throw new InvalidOperationException("store offline");

        public long? GetCachedTokenCount(string hash) => throw new InvalidOperationException("store offline");

        public void PutCachedTokenCounts(IReadOnlyDictionary<string, long> counts, string model) => throw new InvalidOperationException("store offline");

        public void PutSession(SessionSummaryRow row) => throw new InvalidOperationException("store offline");

        public void PutSessions(IEnumerable<SessionSummaryRow> rows) => throw new InvalidOperationException("store offline");

        public SessionSummaryRow? GetSession(string sessionId) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<SessionSummaryRow> ListSessions(string? harness = null) => throw new InvalidOperationException("store offline");

        public void DeleteSession(string sessionId) => throw new InvalidOperationException("store offline");

        public void DeleteSessions(IEnumerable<string> sessionIds) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<(int TurnIndex, string TurnJson)> GetSessionTurns(string sessionId) => throw new InvalidOperationException("store offline");

        public DirtyProjectionState GetDirtyProjectionState() => throw new InvalidOperationException("store offline");

        public void MarkProjectionClean(long writeSeq, string schemaVersion) => throw new InvalidOperationException("store offline");

        public IReadOnlyDictionary<string, string> GetProjectionSessionKeyMap() => throw new InvalidOperationException("store offline");

        public void SaveProjectionSessionKeyMap(IReadOnlyDictionary<string, string> map) => throw new InvalidOperationException("store offline");

        public void PutFinding(Finding finding) => throw new InvalidOperationException("store offline");

        public void PutFindings(IEnumerable<Finding> findings) => throw new InvalidOperationException("store offline");

        public Finding? GetFinding(string id) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<Finding> ListFindings(string? runId = null, string? sessionId = null) => throw new InvalidOperationException("store offline");

        public void DeleteFindingsForSession(string sessionId) => throw new InvalidOperationException("store offline");

        public void UpsertRuns(IEnumerable<RunRow> runs) => throw new InvalidOperationException("store offline");

        public RunRow? GetRun(string runId) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<RunRow> ListRuns(string? harness = null) => throw new InvalidOperationException("store offline");

        public void UpsertExecutions(IEnumerable<ExecutionRow> executions) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<ExecutionRow> ListExecutionsForRun(string runId) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<ExecutionRow> ListExecutionsByHarness(string harness) => throw new InvalidOperationException("store offline");

        public void UpsertHarnessRollups(IEnumerable<HarnessRollupRow> rollups) => throw new InvalidOperationException("store offline");

        public HarnessRollupRow? GetHarnessRollup(string harness) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<HarnessRollupRow> ListHarnessRollups() => throw new InvalidOperationException("store offline");

        public void PutPrediction(PredictionRow prediction) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<PredictionRow> ListPredictions(string? runId = null) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<StoredProblemRow> ListStoredProblems(string? harness = null, string? spanId = null) => throw new InvalidOperationException("store offline");

        public StoredProblemRow? GetStoredProblem(int id) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<QuarantineRow> ListQuarantine(string? harness = null) => throw new InvalidOperationException("store offline");

        public QuarantineRow? GetQuarantineItem(string spanId) => throw new InvalidOperationException("store offline");

        public SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey) => throw new InvalidOperationException("store offline");

        public void UpsertSourceCheckpoint(SourceCheckpoint checkpoint) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null) => throw new InvalidOperationException("store offline");

        public void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows) => throw new InvalidOperationException("store offline");

        public void RecordRefreshRun(RefreshRunRow row) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20) => throw new InvalidOperationException("store offline");

        public void StorePendingLogs(IEnumerable<OtlpLog> logs, string? harness = null) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<OtlpLog> TakePendingLogs(int limit = 500) => throw new InvalidOperationException("store offline");

        public void StoreQuarantinedLogs(IEnumerable<(OtlpLog Log, string Reason)> logs, string? harness = null) => throw new InvalidOperationException("store offline");

        public bool IsLogEnriched(string logId) => throw new InvalidOperationException("store offline");

        public void MarkLogsEnriched(IEnumerable<string> logIds) => throw new InvalidOperationException("store offline");

        public void StoreMetrics(IEnumerable<OtlpMetric> metrics, string? source = null) => throw new InvalidOperationException("store offline");

        public int PurgeContentBefore(string cutoffUtc) => throw new InvalidOperationException("store offline");

        public StoreWipeReport WipeAll() => throw new InvalidOperationException("store offline");

        public StoreWipeReport WipeHarnesses(IEnumerable<string> harnesses) => throw new InvalidOperationException("store offline");

        public void Vacuum() => throw new InvalidOperationException("store offline");

        public IReadOnlyList<string> GetBuiltSessionIds() => throw new InvalidOperationException("store offline");

        public IReadOnlyList<string> GetDistinctSessionKeys() => throw new InvalidOperationException("store offline");

        public IReadOnlyList<(string Key, string Harness)> GetSessionKeyHarnesses() => throw new InvalidOperationException("store offline");

        public bool HasCurrentDetectorVersion() => throw new InvalidOperationException("store offline");

        public void SetDetectorVersion(int version = CanonSchema.DetectorVersion) => throw new InvalidOperationException("store offline");

        public IReadOnlyDictionary<string, IReadOnlyList<string>>? GetProjectedSessionKeyMap() => throw new InvalidOperationException("store offline");

        public void RecordProjectedSessionKeys(long seq, IReadOnlyDictionary<string, IReadOnlyList<string>> sessionIdsByKey) => throw new InvalidOperationException("store offline");

        public IReadOnlyDictionary<string, int> CountRecordsBySource() => throw new InvalidOperationException("store offline");

        public long? GetMaxRecordRowId() => throw new InvalidOperationException("store offline");

        public uint ComputeStoreGeneration() => throw new InvalidOperationException("store offline");

        public IReadOnlyList<JsonElement> ListSessionTurnPayloads(string sessionId) => throw new InvalidOperationException("store offline");

        public JsonElement? GetSessionTurnPayload(string sessionId, int turnIndex) => throw new InvalidOperationException("store offline");

        public void DeleteHarnessRollup(string harness) => throw new InvalidOperationException("store offline");

        public int CountQuarantine(string? harness = null) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<QuarantineRow> ListQuarantinePage(string? harness = null, int? limit = null, int? offset = null) => throw new InvalidOperationException("store offline");

        public int CountProblems(string? harness = null) => throw new InvalidOperationException("store offline");

        public StoredProblemRow? GetProblemById(int id) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<StoredProblemRow> ListProblems(string? harness = null, int? limit = null, int? offset = null) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey) => throw new InvalidOperationException("store offline");

        public IReadOnlyList<CanonicalRecord> ListOtlpRecords() => throw new InvalidOperationException("store offline");

        public void CommitSourceUnit(
            SourceCheckpoint checkpoint,
            IReadOnlyList<CanonicalRecord> records,
            IReadOnlyList<RecordProvenanceRow> provenances,
            IReadOnlyList<SpanProblemRow>? problems = null) => throw new InvalidOperationException("store offline");

        public string StartRefreshRun(string id, string trigger, string startedAtUtc, int pid, int? historyWeeks = null) => throw new InvalidOperationException("store offline");

        public void CompleteRefreshRun(string id, string status, string completedAtUtc, string? summary) => throw new InvalidOperationException("store offline");

        public int ReconcileDeadRefreshRuns(string completedAtUtc, long maxAgeMs = 3_600_000) => throw new InvalidOperationException("store offline");
    }
}
