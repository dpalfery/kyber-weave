using Xunit;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Resilience;
using KyberDash.Data;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;

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
        using ServiceProvider provider = BuildProvider(new DashOptions());

        Assert.NotNull(provider.GetRequiredService<DashOptions>());
        Assert.IsType<SqliteCircuitBreaker>(provider.GetRequiredService<ICircuitBreaker>());
        Assert.IsType<CanonStoreRepository>(provider.GetRequiredService<ICanonStoreRepository>());
        Assert.IsType<TranscriptReaderRepository>(provider.GetRequiredService<ITranscriptReaderRepository>());
        Assert.IsType<PricingRepository>(provider.GetRequiredService<IPricingRepository>());
        Assert.IsType<SettingsRepository>(provider.GetRequiredService<ISettingsRepository>());
        Assert.IsType<ServerStateRepository>(provider.GetRequiredService<IServerStateRepository>());
        Assert.IsType<WebAssetRepository>(provider.GetRequiredService<IWebAssetRepository>());
    }

    [Fact]
    public void AddDashDataServices_RegistersSingletons()
    {
        using ServiceProvider provider = BuildProvider(new DashOptions());

        Assert.Same(provider.GetRequiredService<ICanonStoreRepository>(), provider.GetRequiredService<ICanonStoreRepository>());
        Assert.Same(provider.GetRequiredService<IPricingRepository>(), provider.GetRequiredService<IPricingRepository>());
    }

    [Fact]
    public void AddDashDataServices_UsesTheConfiguredDatabasePath()
    {
        DashOptions options = new() { DatabasePath = Path.Combine(directory.Path, "configured.db") };
        using ServiceProvider provider = BuildProvider(options);

        ICanonStoreRepository store = provider.GetRequiredService<ICanonStoreRepository>();

        Assert.Equal(Path.GetFullPath(options.DatabasePath), store.DatabasePath);
        Assert.Equal("24", store.GetMetadata("schema_version"));
        Assert.Equal(0, provider.GetRequiredService<ISettingsRepository>().ReadSettings().DashboardPort - SettingsRepository.DefaultDashboardPort);
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

        using ServiceProvider provider = services.AddDashDataServices(new DashOptions()).BuildServiceProvider();

        Assert.Same(reader, provider.GetRequiredService<ITranscriptReaderRepository>());
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
