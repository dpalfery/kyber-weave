using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberDash.Data.Tests;

/// <summary>
/// Wiring of <see cref="ServiceCollectionExtensions.AddDashDataServices"/>: every contract resolves, the
/// singletons are shared, and a pre-registered substitute is left in place.
/// </summary>
public class ServiceCollectionExtensionsTests : IDisposable
{
    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AddDashDataServices_ResolvesEveryContract()
    {
        DashOptions relationalOptions = new() { DatabasePath = Path.Combine(directory.Path, "contracts.db") };
        using ServiceProvider relationalProvider = BuildProvider(relationalOptions);

        Assert.NotNull(relationalProvider.GetRequiredService<DashOptions>());
        Assert.IsType<SqliteCircuitBreaker>(relationalProvider.GetRequiredService<ICircuitBreaker>());
        Assert.IsType<SqliteRelationalStore>(relationalProvider.GetRequiredService<SqliteRelationalStore>());
        Assert.IsType<SqliteRelationalStore>(relationalProvider.GetRequiredService<IRelationalStoreWriter>());
        Assert.IsType<SqliteRelationalStore>(relationalProvider.GetRequiredService<IRelationalStoreReader>());
        Assert.IsType<SqliteRelationalStore>(relationalProvider.GetRequiredService<IProjectionStore>());
        Assert.IsType<SqliteRelationalStore>(relationalProvider.GetRequiredService<IOperationalStore>());
        Assert.IsType<TranscriptReaderRepository>(relationalProvider.GetRequiredService<ITranscriptReaderRepository>());
        Assert.IsType<PricingRepository>(relationalProvider.GetRequiredService<IPricingRepository>());
        Assert.IsType<SettingsRepository>(relationalProvider.GetRequiredService<ISettingsRepository>());
        Assert.IsType<ServerStateRepository>(relationalProvider.GetRequiredService<IServerStateRepository>());
        Assert.IsType<WebAssetRepository>(relationalProvider.GetRequiredService<IWebAssetRepository>());

        // Transitional ICanonStoreRepository remains registered until T45, tested on its own DB file
        // because CanonStoreRepository and SqliteRelationalStore define distinct 'session' table schemas.
        DashOptions legacyOptions = new() { DatabasePath = Path.Combine(directory.Path, "legacy-contracts.db") };
        using ServiceProvider legacyProvider = BuildProvider(legacyOptions);
        Assert.IsType<CanonStoreRepository>(legacyProvider.GetRequiredService<ICanonStoreRepository>());
    }

    [Fact]
    public void AddDashDataServices_RegistersSingletons()
    {
        DashOptions relationalOptions = new() { DatabasePath = Path.Combine(directory.Path, "singletons.db") };
        using ServiceProvider relationalProvider = BuildProvider(relationalOptions);

        SqliteRelationalStore relationalStore = relationalProvider.GetRequiredService<SqliteRelationalStore>();
        Assert.Same(relationalStore, relationalProvider.GetRequiredService<SqliteRelationalStore>());
        Assert.Same(relationalStore, relationalProvider.GetRequiredService<IRelationalStoreWriter>());
        Assert.Same(relationalStore, relationalProvider.GetRequiredService<IRelationalStoreReader>());
        Assert.Same(relationalStore, relationalProvider.GetRequiredService<IProjectionStore>());
        Assert.Same(relationalStore, relationalProvider.GetRequiredService<IOperationalStore>());
        Assert.Same(relationalProvider.GetRequiredService<IPricingRepository>(), relationalProvider.GetRequiredService<IPricingRepository>());
        Assert.Same(relationalProvider.GetRequiredService<ISettingsRepository>(), relationalProvider.GetRequiredService<ISettingsRepository>());

        DashOptions legacyOptions = new() { DatabasePath = Path.Combine(directory.Path, "legacy-singletons.db") };
        using ServiceProvider legacyProvider = BuildProvider(legacyOptions);
        Assert.Same(legacyProvider.GetRequiredService<ICanonStoreRepository>(), legacyProvider.GetRequiredService<ICanonStoreRepository>());
    }

    [Fact]
    public void AddDashDataServices_UsesTheConfiguredDatabasePath()
    {
        DashOptions relationalOptions = new() { DatabasePath = Path.Combine(directory.Path, "configured.db") };
        using ServiceProvider relationalProvider = BuildProvider(relationalOptions);

        SqliteRelationalStore relationalStore = relationalProvider.GetRequiredService<SqliteRelationalStore>();

        Assert.Equal(Path.GetFullPath(relationalOptions.DatabasePath), relationalStore.DatabasePath);
        Assert.Equal(SqliteRelationalStore.SchemaVersion, relationalStore.GetStoreState()?.SchemaVersion);
        Assert.Equal(SettingsRepository.DefaultDashboardPort, relationalProvider.GetRequiredService<ISettingsRepository>().ReadSettings().DashboardPort);

        DashOptions legacyOptions = new() { DatabasePath = Path.Combine(directory.Path, "legacy-configured.db") };
        using ServiceProvider legacyProvider = BuildProvider(legacyOptions);
        ICanonStoreRepository legacyStore = legacyProvider.GetRequiredService<ICanonStoreRepository>();

        Assert.Equal(Path.GetFullPath(legacyOptions.DatabasePath), legacyStore.DatabasePath);
        Assert.Equal("24", legacyStore.GetMetadata("schema_version"));
    }

    [Fact]
    public void AddDashDataServices_UsesTheConfiguredStateDirectory()
    {
        DashOptions options = new() { DatabasePath = Path.Combine(directory.Path, "state.db"), StateDirectory = directory.Combine("state") };
        using ServiceProvider provider = BuildProvider(options);

        Assert.Equal(Path.GetFullPath(directory.Combine("state")), provider.GetRequiredService<IServerStateRepository>().DefaultStateDirectory);
    }

    [Fact]
    public void AddDashDataServices_LeavesAPreRegisteredSubstituteInPlace()
    {
        StubTranscriptReader reader = new();
        ServiceCollection services = new();
        services.AddSingleton<ITranscriptReaderRepository>(reader);

        using ServiceProvider provider = services.AddDashDataServices(new DashOptions { DatabasePath = Path.Combine(directory.Path, "sub.db") }).BuildServiceProvider();

        Assert.Same(reader, provider.GetRequiredService<ITranscriptReaderRepository>());
    }

    [Fact]
    public void AddDashDataServices_LeavesAPreRegisteredOperationalStoreInPlace()
    {
        InMemoryRelationalStore customStore = new();
        ServiceCollection services = new();
        services.AddSingleton<IOperationalStore>(customStore);

        using ServiceProvider provider = services.AddDashDataServices(new DashOptions { DatabasePath = Path.Combine(directory.Path, "override.db") }).BuildServiceProvider();

        Assert.Same(customStore, provider.GetRequiredService<IOperationalStore>());

        provider.GetRequiredService<ISettingsRepository>().WriteBooleanSetting(SettingsRepository.JobsPausedKey, true);
        Assert.True(customStore.ReadSettings().JobsPaused);

        customStore.ReplaceModelContextWindowCatalog(new StoredModelCatalogSnapshot(
            "refreshed",
            [new StoredModelCatalogVendorStatus("anthropic", "ok", "2026-11-19T12:00:00Z")],
            []));
        Assert.Equal("refreshed", provider.GetRequiredService<IPricingRepository>().GetModelCatalogSnapshot().GetProperty("status").GetString());
    }

    [Fact]
    public void AddDashDataServices_ReturnsTheSameCollection()
    {
        ServiceCollection services = new();

        Assert.Same(services, services.AddDashDataServices());
    }

    [Fact]
    public void AddDashDataServices_RejectsANullCollection()
    {
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddDashDataServices(null!));
    }

    [Fact]
    public void AddDashDataServices_HonoursCircuitBreakerOptions()
    {
        DashOptions options = new()
        {
            DatabasePath = Path.Combine(directory.Path, "breaker.db"),
            CircuitBreaker = new DashCircuitBreakerOptions { MinimumThroughput = 2 },
        };
        using ServiceProvider provider = BuildProvider(options);
        ICircuitBreaker breaker = provider.GetRequiredService<ICircuitBreaker>();

        Assert.Throws<KyberDash.Core.Exceptions.SqliteExecutionException>(() => breaker.Execute<int>(static () => throw new KyberDash.Core.Exceptions.SqliteExecutionException("down")));
        Assert.Throws<KyberDash.Core.Exceptions.SqliteExecutionException>(() => breaker.Execute<int>(static () => throw new KyberDash.Core.Exceptions.SqliteExecutionException("down")));

        Assert.Equal(DashCircuitState.Open, breaker.State);
    }

    private ServiceProvider BuildProvider(DashOptions options)
    {
        return new ServiceCollection().AddDashDataServices(options).BuildServiceProvider();
    }

    /// <summary>Substitute transcript reader, registered before the data services.</summary>
    private class StubTranscriptReader : ITranscriptReaderRepository
    {
        public IReadOnlyList<ClaudeTurnGroup> ReadClaudeTurns(string filePath, DateTimeOffset? windowStart = null, DateTimeOffset? windowEnd = null) => [];

        public IReadOnlyList<SourceRecordEnvelope> ReadClaudeEnvelopes(string filePath, string? sourceKey = null, string? sourceRevision = null, string? importedAt = null, DateTimeOffset? windowStart = null, DateTimeOffset? windowEnd = null) => [];

        public IReadOnlyList<ReaderTurn> ReadCodexTurns(string filePath) => [];

        public IReadOnlyList<SourceRecordEnvelope> ReadCodexEnvelopes(string filePath, string? sourceKey = null, string? sourceRevision = null, string? importedAt = null, DateTimeOffset? windowStart = null, DateTimeOffset? windowEnd = null) => [];

        public IReadOnlyList<RefreshSourceInput> DiscoverLocalSources(DateTimeOffset? sinceUtc = null) => [];
    }
}
