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
/// singletons are shared, the filesystem seam is the physical singleton, and a pre-registered substitute
/// is left in place.
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
        DashOptions options = new() { DatabasePath = Path.Combine(directory.Path, "contracts.db") };
        using ServiceProvider provider = BuildProvider(options);

        Assert.NotNull(provider.GetRequiredService<DashOptions>());
        Assert.IsType<SqliteCircuitBreaker>(provider.GetRequiredService<ICircuitBreaker>());
        Assert.IsType<SqliteRelationalStore>(provider.GetRequiredService<SqliteRelationalStore>());
        Assert.IsType<SqliteRelationalStore>(provider.GetRequiredService<IRelationalStoreWriter>());
        Assert.IsType<SqliteRelationalStore>(provider.GetRequiredService<IRelationalStoreReader>());
        Assert.IsType<SqliteRelationalStore>(provider.GetRequiredService<IProjectionStore>());
        Assert.IsType<SqliteRelationalStore>(provider.GetRequiredService<IOperationalStore>());
        Assert.IsType<PhysicalRelationalStoreFileSystem>(provider.GetRequiredService<IRelationalStoreFileSystem>());
        Assert.IsType<TranscriptReaderRepository>(provider.GetRequiredService<ITranscriptReaderRepository>());
        Assert.IsType<PricingRepository>(provider.GetRequiredService<IPricingRepository>());
        Assert.IsType<SettingsRepository>(provider.GetRequiredService<ISettingsRepository>());
        Assert.IsType<ServerStateRepository>(provider.GetRequiredService<IServerStateRepository>());
        Assert.IsType<WebAssetRepository>(provider.GetRequiredService<IWebAssetRepository>());
    }

    [Fact]
    public void AddDashDataServices_RegistersSingletons()
    {
        DashOptions options = new() { DatabasePath = Path.Combine(directory.Path, "singletons.db") };
        using ServiceProvider provider = BuildProvider(options);

        SqliteRelationalStore store = provider.GetRequiredService<SqliteRelationalStore>();
        Assert.Same(store, provider.GetRequiredService<SqliteRelationalStore>());
        Assert.Same(store, provider.GetRequiredService<IRelationalStoreWriter>());
        Assert.Same(store, provider.GetRequiredService<IRelationalStoreReader>());
        Assert.Same(store, provider.GetRequiredService<IProjectionStore>());
        Assert.Same(store, provider.GetRequiredService<IOperationalStore>());
        Assert.Same(PhysicalRelationalStoreFileSystem.Instance, provider.GetRequiredService<IRelationalStoreFileSystem>());
        Assert.Same(provider.GetRequiredService<IPricingRepository>(), provider.GetRequiredService<IPricingRepository>());
        Assert.Same(provider.GetRequiredService<ISettingsRepository>(), provider.GetRequiredService<ISettingsRepository>());
    }

    [Fact]
    public void AddDashDataServices_UsesTheConfiguredDatabasePath()
    {
        DashOptions options = new() { DatabasePath = Path.Combine(directory.Path, "configured.db") };
        using ServiceProvider provider = BuildProvider(options);

        SqliteRelationalStore store = provider.GetRequiredService<SqliteRelationalStore>();

        Assert.Equal(Path.GetFullPath(options.DatabasePath), store.DatabasePath);
        Assert.Equal(SqliteRelationalStore.SchemaVersion, store.GetStoreState()?.SchemaVersion);
        Assert.Equal(SettingsRepository.DefaultDashboardPort, provider.GetRequiredService<ISettingsRepository>().ReadSettings().DashboardPort);
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
    public void AddDashDataServices_LeavesAPreRegisteredFileSystemInPlace()
    {
        StubRelationalStoreFileSystem fileSystem = new();
        ServiceCollection services = new();
        services.AddSingleton<IRelationalStoreFileSystem>(fileSystem);

        using ServiceProvider provider = services.AddDashDataServices(new DashOptions { DatabasePath = Path.Combine(directory.Path, "fs-override.db") }).BuildServiceProvider();

        Assert.Same(fileSystem, provider.GetRequiredService<IRelationalStoreFileSystem>());
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

    /// <summary>Substitute filesystem seam, registered before the data services.</summary>
    private class StubRelationalStoreFileSystem : IRelationalStoreFileSystem
    {
        public bool FileExists(string path) => false;

        public void CreateDirectory(string path)
        {
        }

        public void DeleteFile(string path)
        {
        }
    }
}
