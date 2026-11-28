using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KyberDash.Data.Tests;

/// <summary>Fail-closed validation and persistence for <see cref="SettingsRepository"/>.</summary>
public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly InMemoryRelationalStore store = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Constructor_RejectsNullStore()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsRepository(null!));
    }

    [Fact]
    public void ReadSettings_FallsBackToDefaultsWhenNothingIsStored()
    {
        SettingsRepository repository = new(store);

        SettingsView settings = repository.ReadSettings();

        Assert.Equal(
            new SettingsView(false, false, SettingsRepository.DefaultRefreshCadenceMinutes, false, SettingsRepository.DefaultDashboardPort),
            settings);
    }

    [Theory]
    [InlineData("yes please\t0\t5\t0\t4747")]
    [InlineData("0\t0\t3.5\t0\t4747")]
    [InlineData("0\t0\t5\tON\t4747")]
    [InlineData("0\t0\t5\t0\tnotanint")]
    public void ReadSettings_FailsClosedOnCorruptCellsFromSqliteRelationalStore(string scriptedRow)
    {
        ScriptedSqliteRelationalStore scripted = new(
            Path.Combine(directory.Path, "corrupt.db"),
            new SqliteCircuitBreaker());
        scripted.EnqueueSettingsRow(scriptedRow);
        SettingsRepository repository = new(scripted);

        Assert.Throws<StoredDataException>(() => repository.ReadSettings());
    }

    [Fact]
    public void ReadSettings_PropagatesSqliteAndCircuitBreakerFaults()
    {
        ScriptedSqliteRelationalStore executionFailing = new(
            Path.Combine(directory.Path, "sqlite-fail.db"),
            new SqliteCircuitBreaker());
        executionFailing.EnqueueException(new SqliteExecutionException("sqlite offline"));
        Assert.Throws<SqliteExecutionException>(() => new SettingsRepository(executionFailing).ReadSettings());

        ScriptedSqliteRelationalStore breakerFailing = new(
            Path.Combine(directory.Path, "breaker-fail.db"),
            new SqliteCircuitBreaker());
        breakerFailing.EnqueueException(new CircuitBreakerOpenException("breaker open"));
        Assert.Throws<CircuitBreakerOpenException>(() => new SettingsRepository(breakerFailing).ReadSettings());
    }

    [Theory]
    [InlineData("0\t1\t0\t1\t65536", false, true, true)]
    [InlineData("1\t0\t1441\t0\t-1", true, false, false)]
    public void ReadSettings_ClampsOutOfRangeSqliteIntegersToDefaults(
        string scriptedRow,
        bool expectedFolderImport,
        bool expectedJobsPaused,
        bool expectedReceiverHosted)
    {
        ScriptedSqliteRelationalStore scripted = new(
            Path.Combine(directory.Path, "out-of-range.db"),
            new SqliteCircuitBreaker());
        scripted.EnqueueSettingsRow(scriptedRow);
        SettingsRepository repository = new(scripted);

        SettingsView settings = repository.ReadSettings();

        Assert.Equal(expectedFolderImport, settings.FolderImportScheduled);
        Assert.Equal(expectedJobsPaused, settings.JobsPaused);
        Assert.Equal(SettingsRepository.DefaultRefreshCadenceMinutes, settings.RefreshCadenceMinutes);
        Assert.Equal(expectedReceiverHosted, settings.ReceiverHosted);
        Assert.Equal(SettingsRepository.DefaultDashboardPort, settings.DashboardPort);
    }

    [Fact]
    public void ReadSettings_ClampsOutOfRangeSettingsViewIntsFromOperationalStoreStub()
    {
        StubOperationalStore stub = new(new SettingsView(true, false, 0, true, 65536));
        SettingsRepository repository = new(stub);

        SettingsView settings = repository.ReadSettings();

        Assert.Equal(
            new SettingsView(true, false, SettingsRepository.DefaultRefreshCadenceMinutes, true, SettingsRepository.DefaultDashboardPort),
            settings);
    }

    [Fact]
    public void WriteBooleanSetting_PersistsAndPreservesUnmodifiedSettings()
    {
        store.SaveSettings(new SettingsView(false, false, 15, false, 8080));
        SettingsRepository repository = new(store);

        repository.WriteBooleanSetting(SettingsRepository.FolderImportScheduledKey, true);
        repository.WriteBooleanSetting(SettingsRepository.JobsPausedKey, true);
        repository.WriteBooleanSetting(SettingsRepository.ReceiverHostedKey, true);

        SettingsView stored = store.ReadSettings();
        Assert.Equal(new SettingsView(true, true, 15, true, 8080), stored);
        Assert.Equal(stored, repository.ReadSettings());
    }

    [Fact]
    public void WriteBooleanSetting_RejectsUnknownKeysBeforeReadingOrWritingStore()
    {
        StubOperationalStore stub = new(new SettingsView(false, false, 5, false, 4747));
        SettingsRepository repository = new(stub);

        Assert.Throws<ArgumentException>(() => repository.WriteBooleanSetting("settings.unknown", true));
        Assert.Throws<ArgumentNullException>(() => repository.WriteBooleanSetting(null!, true));
        Assert.Throws<ArgumentException>(() => repository.WriteBooleanSetting("   ", true));
        Assert.Equal(0, stub.ReadSettingsCalls);
        Assert.Equal(0, stub.SaveSettingsCalls);
    }

    [Fact]
    public void WriteIntSetting_PersistsInRangeValuesAndPreservesUnmodifiedSettings()
    {
        store.SaveSettings(new SettingsView(true, true, 5, false, 4747));
        SettingsRepository repository = new(store);

        repository.WriteIntSetting(SettingsRepository.RefreshCadenceMinutesKey, SettingsRepository.CadenceMaxMinutes);
        repository.WriteIntSetting(SettingsRepository.DashboardPortKey, SettingsRepository.PortMax);

        SettingsView settings = repository.ReadSettings();
        Assert.Equal(new SettingsView(true, true, SettingsRepository.CadenceMaxMinutes, false, SettingsRepository.PortMax), settings);
        Assert.Equal(settings, store.ReadSettings());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    public void WriteIntSetting_RejectsOutOfRangeCadenceBeforeReadingOrWritingStore(int value)
    {
        StubOperationalStore stub = new(new SettingsView(false, false, 5, false, 4747));
        SettingsRepository stubRepository = new(stub);
        SettingsRepository memoryRepository = new(store);

        Assert.Throws<ArgumentOutOfRangeException>(() => stubRepository.WriteIntSetting(SettingsRepository.RefreshCadenceMinutesKey, value));
        Assert.Equal(0, stub.ReadSettingsCalls);
        Assert.Equal(0, stub.SaveSettingsCalls);

        Assert.Throws<ArgumentOutOfRangeException>(() => memoryRepository.WriteIntSetting(SettingsRepository.RefreshCadenceMinutesKey, value));
        Assert.Equal(0, store.CountRows("operator_setting_refresh_cadence"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void WriteIntSetting_RejectsOutOfRangePortBeforeReadingOrWritingStore(int value)
    {
        StubOperationalStore stub = new(new SettingsView(false, false, 5, false, 4747));
        SettingsRepository stubRepository = new(stub);
        SettingsRepository memoryRepository = new(store);

        Assert.Throws<ArgumentOutOfRangeException>(() => stubRepository.WriteIntSetting(SettingsRepository.DashboardPortKey, value));
        Assert.Equal(0, stub.ReadSettingsCalls);
        Assert.Equal(0, stub.SaveSettingsCalls);

        Assert.Throws<ArgumentOutOfRangeException>(() => memoryRepository.WriteIntSetting(SettingsRepository.DashboardPortKey, value));
        Assert.Equal(0, store.CountRows("operator_setting_dashboard"));
    }

    [Fact]
    public void WriteIntSetting_RejectsBooleanKeysBeforeReadingOrWritingStore()
    {
        StubOperationalStore stub = new(new SettingsView(false, false, 5, false, 4747));
        SettingsRepository repository = new(stub);

        Assert.Throws<ArgumentException>(() => repository.WriteIntSetting(SettingsRepository.JobsPausedKey, 1));
        Assert.Throws<ArgumentNullException>(() => repository.WriteIntSetting(null!, 1));
        Assert.Equal(0, stub.ReadSettingsCalls);
        Assert.Equal(0, stub.SaveSettingsCalls);
    }

    [Fact]
    public void FactoryContentConsent_DefaultsToDisabledAndRoundTrips()
    {
        SettingsRepository repository = new(store);

        Assert.False(repository.ReadFactoryContentConsent(directory.Path));

        repository.WriteFactoryContentConsent(directory.Path, true);
        Assert.True(repository.ReadFactoryContentConsent(directory.Path));

        repository.WriteFactoryContentConsent(directory.Path, false);
        Assert.False(repository.ReadFactoryContentConsent(directory.Path));
        Assert.True(File.Exists(directory.Combine(SettingsRepository.FactoryPluginFile)));
    }

    [Fact]
    public void FactoryContentConsent_FailsClosedOnMalformedFile()
    {
        SettingsRepository repository = new(store);
        File.WriteAllText(directory.Combine(SettingsRepository.FactoryPluginFile), "{ not json");

        Assert.False(repository.ReadFactoryContentConsent(directory.Path));
    }

    [Fact]
    public void FactoryContentConsent_FailsClosedWhenFlagIsNotBooleanTrue()
    {
        SettingsRepository repository = new(store);
        File.WriteAllText(directory.Combine(SettingsRepository.FactoryPluginFile), "{\"contentCapture\":\"true\"}");

        Assert.False(repository.ReadFactoryContentConsent(directory.Path));
    }

    [Fact]
    public void FactoryContentConsent_CreatesMissingStateDirectory()
    {
        SettingsRepository repository = new(store);
        string nested = directory.Combine("nested/state");

        repository.WriteFactoryContentConsent(nested, true);

        Assert.True(repository.ReadFactoryContentConsent(nested));
    }

    [Fact]
    public void AppendAntigravityStatusLineEvent_WritesOneJsonLinePerCall()
    {
        SettingsRepository repository = new(store);

        repository.AppendAntigravityStatusLineEvent(directory.Path, JsonSerializer.SerializeToElement(new { turn = 1 }));
        repository.AppendAntigravityStatusLineEvent(directory.Path, JsonSerializer.SerializeToElement(new { turn = 2 }));

        string path = directory.Combine(SettingsRepository.AntigravityStatusLineRelativePath);
        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, JsonDocument.Parse(lines[0]).RootElement.GetProperty("turn").GetInt32());
        Assert.Equal(2, JsonDocument.Parse(lines[1]).RootElement.GetProperty("turn").GetInt32());
    }

    [Fact]
    public void AppendAntigravityStatusLineEvent_WritesEmptyObjectForUndefinedPayload()
    {
        SettingsRepository repository = new(store);

        repository.AppendAntigravityStatusLineEvent(directory.Path, default);

        Assert.Equal("{}", Assert.Single(File.ReadAllLines(directory.Combine(SettingsRepository.AntigravityStatusLineRelativePath))));
    }

    private class ScriptedSqliteRelationalStore : SqliteRelationalStore
    {
        private readonly Queue<Exception> handlers = new();
        private readonly Queue<string> settingsRows = new();

        public ScriptedSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
            : base(databasePath, circuitBreaker, initialize: false)
        {
        }

        public void EnqueueException(Exception exception) => handlers.Enqueue(exception);

        public void EnqueueSettingsRow(string row) => settingsRows.Enqueue(row);

        protected override IReadOnlyList<T> QueryRows<T>(
            string sql,
            IReadOnlyDictionary<string, object?> parameters,
            Func<SqliteDataReader, T> map)
        {
            if (handlers.Count > 0)
            {
                throw handlers.Dequeue();
            }

            if (settingsRows.Count == 0)
            {
                return [];
            }

            string[] cells = settingsRows.Dequeue().Split('\t');
            using SqliteConnection connection = new("Data Source=:memory:");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT $a AS scheduled, $b AS paused, $c AS minutes, $d AS hosted, $e AS port;";
            command.Parameters.AddWithValue("$a", cells[0]);
            command.Parameters.AddWithValue("$b", cells[1]);
            command.Parameters.AddWithValue("$c", cells[2]);
            command.Parameters.AddWithValue("$d", cells[3]);
            command.Parameters.AddWithValue("$e", cells[4]);
            using SqliteDataReader reader = command.ExecuteReader();
            List<T> rows = [];
            while (reader.Read())
            {
                rows.Add(map(reader));
            }

            return rows;
        }
    }

    private class StubOperationalStore : IOperationalStore
    {
        private SettingsView current;

        public StubOperationalStore(SettingsView initial)
        {
            current = initial;
        }

        public int ReadSettingsCalls { get; private set; }

        public int SaveSettingsCalls { get; private set; }

        public SettingsView ReadSettings()
        {
            ReadSettingsCalls++;
            return current;
        }

        public void SaveSettings(SettingsView settings)
        {
            SaveSettingsCalls++;
            current = settings;
        }

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

        public StoredModelCatalogSnapshot GetModelContextWindowCatalog() => throw new NotSupportedException();

        public void ReplaceModelContextWindowCatalog(StoredModelCatalogSnapshot snapshot) => throw new NotSupportedException();

        public void PutMetricPoints(IEnumerable<StoredMetricPoint> points) => throw new NotSupportedException();

        public IReadOnlyList<StoredMetricPoint> ListMetricPoints(string? name = null, int? limit = null) => throw new NotSupportedException();

        public void RecordIngest(string source, int count, string timestampUtc) => throw new NotSupportedException();

        public IReadOnlyList<IngestLogEntry> ListIngestLog(string? source = null) => throw new NotSupportedException();

        public void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows) => throw new NotSupportedException();

        public IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey) => throw new NotSupportedException();
    }
}
