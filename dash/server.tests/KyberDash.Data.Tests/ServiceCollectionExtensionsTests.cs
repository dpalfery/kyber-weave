using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Repositories;
using KyberDash.Data.Tests.Collector;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberDash.Data.Tests;

/// <summary>
/// Wiring of <see cref="ServiceCollectionExtensions.AddDashDataServices"/>: every contract resolves, the
/// singletons are shared, the filesystem seam is the physical singleton, and a pre-registered substitute
/// is left in place.
/// </summary>
[Collection(HomeEnvironmentStatics.Name)]
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
    public void AddDashDataServices_ResolvesHarnessMapperCoverageForEveryEvidencedHarness()
    {
        using ServiceProvider provider = BuildProvider(new DashOptions { DatabasePath = Path.Combine(directory.Path, "mappers.db") });

        IReadOnlyList<IHarnessMapper> mappers = [.. provider.GetServices<IHarnessMapper>()];

        Assert.NotEmpty(mappers);
        HashSet<string> covered = new(mappers.Select(static m => m.HarnessId), StringComparer.Ordinal);
        Assert.All(covered, static id => Assert.False(string.IsNullOrWhiteSpace(id)));
        string[] expected =
        [
            "antigravity",
            "claude-code",
            "cline",
            "codex-cli",
            "commandcode",
            "copilot",
            "copilot-cli",
            "copilot-vscode",
            "cursor",
            "devin",
            "droid",
            "kilo-shared-runtime",
            "kimi-code",
            "opencode",
            "pi",
            "roo-code",
            "warp",
        ];
        Assert.All(expected, id => Assert.Contains(id, covered));
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
    public void AddDashDataServices_TranscriptReaderCachesUnderTheConfiguredStateDirectory()
    {
        string home = directory.Combine("home");
        string state = directory.Combine("transcript-state");
        string databaseDirectory = Path.Combine(home, ".local", "share", "devin", "cli");
        Directory.CreateDirectory(databaseDirectory);
        _ = SqliteFixtureDatabase.Create(
            Path.Combine(databaseDirectory, "sessions.db"),
            "CREATE TABLE fixture (id INTEGER PRIMARY KEY);",
            [new SqliteFixtureStatement("INSERT INTO fixture (id) VALUES (?1);", [1L])],
            SqliteFixtureJournal.WalUncheckpointed);
        DashOptions options = new() { DatabasePath = Path.Combine(directory.Path, "transcripts.db"), StateDirectory = state };

        string? originalHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            // The reader discovers from the user profile, which the host resolves from HOME; the exclusive
            // collection keeps this process-wide change away from every parallel class.
            Environment.SetEnvironmentVariable("HOME", home);
            using ServiceProvider provider = BuildProvider(options);
            ITranscriptReaderRepository reader = provider.GetRequiredService<ITranscriptReaderRepository>();
            SealDirectoryOrSkip(databaseDirectory);

            _ = reader.DiscoverLocalSources();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            UnsealDirectory(databaseDirectory);
        }

        string cacheRoot = Path.Combine(Path.GetFullPath(state), "sqlite-ro");
        Assert.True(Directory.Exists(cacheRoot), $"No cache copy was written under '{cacheRoot}'.");
        Assert.Single(Directory.GetFiles(cacheRoot, "*.db"));
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

    private static void SealDirectoryOrSkip(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A read-only directory needs a POSIX directory mode, which Windows does not have.");
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string probe = Path.Combine(path, ".write-probe");
        try
        {
            File.WriteAllBytes(probe, []);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        File.Delete(probe);
        Assert.Skip("The test user can write into a read-only directory (a superuser), so the cache copy cannot be forced.");
    }

    private static void UnsealDirectory(string path)
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(path))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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

/// <summary>
/// The exclusive collection for tests that change the process-wide <c>HOME</c> variable: it runs alone,
/// so no parallel class resolves the user profile mid-change, and every writer restores what it found.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class HomeEnvironmentStatics
{
    /// <summary>The name tests use to join the exclusive home-environment collection.</summary>
    public const string Name = "home-environment-statics";
}
