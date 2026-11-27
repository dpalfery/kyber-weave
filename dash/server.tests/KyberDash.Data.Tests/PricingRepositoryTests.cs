using System.Reflection;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests;

/// <summary>Rate-table loading, pricing-provider registration, and model-catalog status for <see cref="PricingRepository"/>.</summary>
/// <remarks>
/// This class reconfigures <see cref="CanonPricing"/>'s process-wide provider (the constructor
/// registers a store-backed one, and the tests exercise registration), so it joins
/// <see cref="CanonPricingStatics"/> and restores the provider it found: leaving empty tables
/// behind once starved a parallel reader of rates for <c>claude-sonnet-4-5</c>.
/// </remarks>
[Collection(Repositories.CanonPricingStatics.Name)]
public sealed class PricingRepositoryTests : IDisposable
{
    private readonly (Func<PricingTables>? Provider, PricingTables? Cache) savedPricingProvider;

    /// <summary>Captures the process-wide pricing provider so <see cref="Dispose"/> can put it back.</summary>
    public PricingRepositoryTests()
    {
        savedPricingProvider = (
            PricingField("_pricingProvider").GetValue(null) as Func<PricingTables>,
            PricingField("_cachedTables").GetValue(null) as PricingTables);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Restores the process-wide pricing provider the constructor replaced when disposing.</summary>
    /// <param name="disposing">Whether managed state should be released.</param>
    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Restore the process-wide provider so other tests are not left with a repository-backed
            // one (or with empty tables that would price ambient reads as having no rate).
            PricingField("_pricingProvider").SetValue(null, savedPricingProvider.Provider);
            PricingField("_cachedTables").SetValue(null, savedPricingProvider.Cache);
        }
    }

    private static FieldInfo PricingField(string name) =>
        typeof(CanonPricing).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"CanonPricing.{name} is no longer a private static field.");

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
        InMemoryRelationalStore store = new();
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
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("lastRefreshAt").ValueKind);
        Assert.Equal(JsonValueKind.Array, snapshot.GetProperty("models").ValueKind);
    }

    [Fact]
    public void GetModelCatalogSnapshot_MapsEmptyAndBlankStatusToEmbedded()
    {
        InMemoryRelationalStore emptyStore = new();
        PricingRepository emptyRepository = new(emptyStore);

        JsonElement emptySnapshot = emptyRepository.GetModelCatalogSnapshot();
        Assert.Equal("embedded", emptySnapshot.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, emptySnapshot.GetProperty("lastRefreshAt").ValueKind);

        PricingRepository blankRepository = new(new StubCatalogOperationalStore(
            new StoredModelCatalogSnapshot("   ", [new StoredModelCatalogVendorStatus("anthropic", "ok", "   ")], [])));
        JsonElement blankSnapshot = blankRepository.GetModelCatalogSnapshot();
        Assert.Equal("embedded", blankSnapshot.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, blankSnapshot.GetProperty("lastRefreshAt").ValueKind);
    }

    [Fact]
    public void GetModelCatalogSnapshot_ReadsStatusAndLatestVendorRefreshFromTheStore()
    {
        InMemoryRelationalStore store = new();
        store.ReplaceModelContextWindowCatalog(new StoredModelCatalogSnapshot(
            Status: "refreshed",
            Vendors:
            [
                new StoredModelCatalogVendorStatus("openai", "ok", "2026-11-19T10:00:00Z"),
                new StoredModelCatalogVendorStatus("anthropic", "ok", "2026-11-19T13:30:00+02:00"),
                new StoredModelCatalogVendorStatus("google", "error", null, "timeout"),
            ],
            Entries: []));
        PricingRepository repository = new(store);

        JsonElement snapshot = repository.GetModelCatalogSnapshot();

        Assert.Equal("refreshed", snapshot.GetProperty("status").GetString());
        Assert.Equal("2026-11-19T13:30:00+02:00", snapshot.GetProperty("lastRefreshAt").GetString());
    }

    [Theory]
    [InlineData("invalid_operation")]
    [InlineData("io")]
    [InlineData("sqlite")]
    [InlineData("breaker")]
    public void GetModelCatalogSnapshot_StaysAdvisoryWhenTheStoreFails(string faultKind)
    {
        Exception exception = faultKind switch
        {
            "invalid_operation" => new InvalidOperationException("store offline"),
            "io" => new IOException("disk unavailable"),
            "sqlite" => new SqliteExecutionException("sqlite3 failed"),
            "breaker" => new CircuitBreakerOpenException("breaker open"),
            _ => throw new ArgumentOutOfRangeException(nameof(faultKind)),
        };
        PricingRepository repository = new(new ThrowingOperationalStore(exception));

        JsonElement snapshot = repository.GetModelCatalogSnapshot();

        Assert.Equal("unknown", snapshot.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("lastRefreshAt").ValueKind);
    }

    private class StubCatalogOperationalStore : IOperationalStore
    {
        private readonly StoredModelCatalogSnapshot catalog;

        public StubCatalogOperationalStore(StoredModelCatalogSnapshot catalog)
        {
            this.catalog = catalog;
        }

        public StoredModelCatalogSnapshot GetModelContextWindowCatalog() => catalog;

        public SettingsView ReadSettings() => throw new NotSupportedException();

        public void SaveSettings(SettingsView settings) => throw new NotSupportedException();

        public StoreStateView? GetStoreState() => throw new NotSupportedException();

        public void SetStoreState(StoreStateView state) => throw new NotSupportedException();

        public long GetWriteMarker() => throw new NotSupportedException();

        public void SetWriteMarker(long recordsWriteSeq) => throw new NotSupportedException();

        public string? GetLastCleanAtUtc() => throw new NotSupportedException();

        public void SetLastCleanAtUtc(string lastCleanAtUtc) => throw new NotSupportedException();

        public int? GetContentRetentionDays() => throw new NotSupportedException();

        public void SetContentRetentionDays(int retentionDays) => throw new NotSupportedException();

        public string? GetContentPurgeMarkUtc() => throw new NotSupportedException();

        public void SetContentPurgeMarkUtc(string purgedThroughUtc) => throw new NotSupportedException();

        public ProjectionStateView? GetProjectionState() => throw new NotSupportedException();

        public void SetProjectionState(ProjectionStateView state) => throw new NotSupportedException();

        public DirtyProjectionState? GetProjectionDirtyState() => throw new NotSupportedException();

        public void SetProjectionDirtyState(DirtyProjectionState state) => throw new NotSupportedException();

        public void ClearProjectionDirtyState() => throw new NotSupportedException();

        public IReadOnlyList<SessionKeyAlias> ListSessionKeyAliases(string? sessionId = null) => throw new NotSupportedException();

        public void ReplaceSessionKeyAliases(IEnumerable<SessionKeyAlias> aliases) => throw new NotSupportedException();

        public void UpsertRefreshRun(RefreshRunRow row) => throw new NotSupportedException();

        public RefreshRunRow? GetRefreshRun(string refreshRunId) => throw new NotSupportedException();

        public IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20) => throw new NotSupportedException();

        public SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey) => throw new NotSupportedException();

        public void UpsertSourceCheckpoint(SourceCheckpoint checkpoint) => throw new NotSupportedException();

        public IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null) => throw new NotSupportedException();

        public StoredIngestProblem? GetIngestProblem(long problemId) => throw new NotSupportedException();

        public IReadOnlyList<StoredIngestProblem> ListIngestProblems(string? harnessId = null, string? spanId = null, int? limit = null, int? offset = null) => throw new NotSupportedException();

        public int CountIngestProblems(string? harnessId = null) => throw new NotSupportedException();

        public void PutQuarantinedSpan(StoredQuarantinedSpan span) => throw new NotSupportedException();

        public StoredQuarantinedSpan? GetQuarantinedSpan(string spanId) => throw new NotSupportedException();

        public IReadOnlyList<StoredQuarantinedSpan> ListQuarantinedSpans(string? harnessId = null, int? limit = null, int? offset = null) => throw new NotSupportedException();

        public int CountQuarantinedSpans(string? harnessId = null) => throw new NotSupportedException();

        public long? GetCachedTokenCount(string hash) => throw new NotSupportedException();

        public void PutCachedTokenCounts(IEnumerable<StoredTokenCacheEntry> entries) => throw new NotSupportedException();

        public void ReplaceModelContextWindowCatalog(StoredModelCatalogSnapshot snapshot) => throw new NotSupportedException();

        public void PutMetricPoints(IEnumerable<StoredMetricPoint> points) => throw new NotSupportedException();

        public IReadOnlyList<StoredMetricPoint> ListMetricPoints(string? name = null, int? limit = null) => throw new NotSupportedException();

        public void RecordIngest(string source, int count, string timestampUtc) => throw new NotSupportedException();

        public IReadOnlyList<IngestLogEntry> ListIngestLog(string? source = null) => throw new NotSupportedException();

        public void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows) => throw new NotSupportedException();

        public IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey) => throw new NotSupportedException();
    }

    /// <summary>Operational store whose catalog reads fail, to prove catalog status never breaks a read.</summary>
    private class ThrowingOperationalStore : IOperationalStore
    {
        private readonly Exception failure;

        public ThrowingOperationalStore(Exception failure)
        {
            this.failure = failure;
        }

        public SettingsView ReadSettings() => throw failure;

        public void SaveSettings(SettingsView settings) => throw failure;

        public StoreStateView? GetStoreState() => throw failure;

        public void SetStoreState(StoreStateView state) => throw failure;

        public long GetWriteMarker() => throw failure;

        public void SetWriteMarker(long recordsWriteSeq) => throw failure;

        public string? GetLastCleanAtUtc() => throw failure;

        public void SetLastCleanAtUtc(string lastCleanAtUtc) => throw failure;

        public int? GetContentRetentionDays() => throw failure;

        public void SetContentRetentionDays(int retentionDays) => throw failure;

        public string? GetContentPurgeMarkUtc() => throw failure;

        public void SetContentPurgeMarkUtc(string purgedThroughUtc) => throw failure;

        public ProjectionStateView? GetProjectionState() => throw failure;

        public void SetProjectionState(ProjectionStateView state) => throw failure;

        public DirtyProjectionState? GetProjectionDirtyState() => throw failure;

        public void SetProjectionDirtyState(DirtyProjectionState state) => throw failure;

        public void ClearProjectionDirtyState() => throw failure;

        public IReadOnlyList<SessionKeyAlias> ListSessionKeyAliases(string? sessionId = null) => throw failure;

        public void ReplaceSessionKeyAliases(IEnumerable<SessionKeyAlias> aliases) => throw failure;

        public void UpsertRefreshRun(RefreshRunRow row) => throw failure;

        public RefreshRunRow? GetRefreshRun(string refreshRunId) => throw failure;

        public IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20) => throw failure;

        public SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey) => throw failure;

        public void UpsertSourceCheckpoint(SourceCheckpoint checkpoint) => throw failure;

        public IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null) => throw failure;

        public StoredIngestProblem? GetIngestProblem(long problemId) => throw failure;

        public IReadOnlyList<StoredIngestProblem> ListIngestProblems(string? harnessId = null, string? spanId = null, int? limit = null, int? offset = null) => throw failure;

        public int CountIngestProblems(string? harnessId = null) => throw failure;

        public void PutQuarantinedSpan(StoredQuarantinedSpan span) => throw failure;

        public StoredQuarantinedSpan? GetQuarantinedSpan(string spanId) => throw failure;

        public IReadOnlyList<StoredQuarantinedSpan> ListQuarantinedSpans(string? harnessId = null, int? limit = null, int? offset = null) => throw failure;

        public int CountQuarantinedSpans(string? harnessId = null) => throw failure;

        public long? GetCachedTokenCount(string hash) => throw failure;

        public void PutCachedTokenCounts(IEnumerable<StoredTokenCacheEntry> entries) => throw failure;

        public StoredModelCatalogSnapshot GetModelContextWindowCatalog() => throw failure;

        public void ReplaceModelContextWindowCatalog(StoredModelCatalogSnapshot snapshot) => throw failure;

        public void PutMetricPoints(IEnumerable<StoredMetricPoint> points) => throw failure;

        public IReadOnlyList<StoredMetricPoint> ListMetricPoints(string? name = null, int? limit = null) => throw failure;

        public void RecordIngest(string source, int count, string timestampUtc) => throw failure;

        public IReadOnlyList<IngestLogEntry> ListIngestLog(string? source = null) => throw failure;

        public void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows) => throw failure;

        public IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey) => throw failure;
    }
}
