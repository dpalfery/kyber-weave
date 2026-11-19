using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KyberDash.Contracts.Services;
using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Verifies schema version stamping, read-only <c>sqlite_master</c> probing, and fail-closed
/// refusal of newer, invalid, and older stores over an in-memory <see cref="IRelationalStoreFileSystem"/>
/// seam without touching disk.
/// </summary>
public class SchemaVersionTests
{
    [Fact]
    public void SchemaConstantsAndFileSystemTypes_MatchContractAndRemainUnsealed()
    {
        Assert.True(SqliteRelationalStore.SchemaVersion > 24);
        Assert.Equal(25, SqliteRelationalStore.SchemaVersion);
        Assert.Equal(5, SqliteRelationalStore.DetectorVersion);
        Assert.Equal(32 * 1024 * 1024, SqliteRelationalStore.WalSizeLimitBytes);

        Type fsType = typeof(PhysicalRelationalStoreFileSystem);
        Assert.True(fsType.IsPublic);
        Assert.False(fsType.IsSealed);
        Assert.IsAssignableFrom<IRelationalStoreFileSystem>(PhysicalRelationalStoreFileSystem.Instance);

        Type rebuildInfoType = typeof(StoreRebuildInfo);
        Assert.True(rebuildInfoType.IsPublic);
        Assert.False(rebuildInfoType.IsSealed);

        StoreRebuildInfo info = new(
            "/tmp/canon.db",
            24,
            SqliteRelationalStore.SchemaVersion,
            SettingsResetToDefaults: true,
            UnrecoverableHistoryClasses: ["records", "sessions"],
            ReingestTriggered: true);
        Assert.Equal("/tmp/canon.db", info.DatabasePath);
        Assert.Equal(24, info.OldSchemaVersion);
        Assert.Equal(SqliteRelationalStore.SchemaVersion, info.NewSchemaVersion);
        Assert.True(info.SettingsResetToDefaults);
        Assert.Equal(["records", "sessions"], info.UnrecoverableHistoryClasses);
        Assert.True(info.ReingestTriggered);
    }

    [Fact]
    public void FormatSchemaVersionRefusal_FormatsExpectedOperatorMessage()
    {
        string message = SqliteRelationalStore.FormatSchemaVersionRefusal("/data/canon.db", 26);

        Assert.Equal(
            $"canon store at /data/canon.db was built with schema version 26, but this build understands version {SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}; upgrade KyberDash",
            message);
        Assert.Throws<ArgumentException>(() => SqliteRelationalStore.FormatSchemaVersionRefusal(" ", 26));
    }

    [Fact]
    public void EnsureInitialized_OnExistingCurrentStore_ProbesSqliteMasterAndOpensWithoutDeletingFiles()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = CreateExistingDatabaseSet(dbPath);
        bool rebuildCallbackInvoked = false;

        ScriptedSqliteRelationalStore store = new(
            dbPath,
            new SqliteCircuitBreaker(),
            initialize: false,
            fakeFs,
            onRebuiltFromOlderSchema: () => rebuildCallbackInvoked = true);
        store.EnqueueResponse("store_state");
        store.EnqueueResponse($"{SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}\t1");
        store.EnqueueResponse(string.Empty);
        store.EnqueueResponse(string.Empty);

        store.EnsureInitialized();

        Assert.False(Directory.Exists(rootDir));
        Assert.Empty(fakeFs.DeletedFiles);
        Assert.Empty(fakeFs.CreatedDirectories);
        Assert.True(fakeFs.FileExists(dbPath));
        Assert.True(fakeFs.FileExists(dbPath + "-wal"));
        Assert.True(fakeFs.FileExists(dbPath + "-shm"));
        Assert.False(rebuildCallbackInvoked);
        Assert.False(store.RebuiltFromOlderSchema);
        Assert.Null(store.RebuiltFromSchemaVersion);
        Assert.Null(store.LastRebuildInfo);
        Assert.False(store.HasPendingRebuildReingest);
        Assert.False(store.TryTakePendingRebuildReingest());

        Assert.Equal(4, store.ExecutedSql.Count);
        string probeSql = NormalizeLineEndings(store.ExecutedSql[0]);
        Assert.StartsWith(".headers off\n.mode tabs\n", probeSql, StringComparison.Ordinal);
        Assert.Contains("sqlite_master", probeSql, StringComparison.Ordinal);
        Assert.DoesNotContain("PRAGMA", probeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", probeSql, StringComparison.OrdinalIgnoreCase);

        string versionSql = NormalizeLineEndings(store.ExecutedSql[1]);
        Assert.Contains("SELECT schema_version, detector_version FROM store_state WHERE singleton_id = 1 LIMIT 1;", versionSql, StringComparison.Ordinal);

        string ddlSql = NormalizeLineEndings(store.ExecutedSql[3]);
        Assert.Contains(
            $"UPDATE store_state SET detector_version = {SqliteRelationalStore.DetectorVersion.ToString(CultureInfo.InvariantCulture)} WHERE singleton_id = 1 AND detector_version <> {SqliteRelationalStore.DetectorVersion.ToString(CultureInfo.InvariantCulture)};",
            ddlSql,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("store_state", "26\t5", 26)]
    [InlineData("store_state", "99\t5", 99)]
    [InlineData("metadata", "26", 26)]
    [InlineData("metadata", "30", 30)]
    public void EnsureInitialized_RefusesNewerRelationalOrLegacyStore_WithoutMutatingOrDeletingFiles(
        string discoveredTable,
        string versionQueryResponse,
        int newerVersion)
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = CreateExistingDatabaseSet(dbPath);
        bool rebuildCallbackInvoked = false;

        ScriptedSqliteRelationalStore store = new(
            dbPath,
            new SqliteCircuitBreaker(),
            initialize: false,
            fakeFs,
            onRebuiltFromOlderSchema: () => rebuildCallbackInvoked = true);
        store.EnqueueResponse(discoveredTable);
        store.EnqueueResponse(versionQueryResponse);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => store.EnsureInitialized());

        Assert.Equal(SqliteRelationalStore.FormatSchemaVersionRefusal(dbPath, newerVersion), ex.Message);
        AssertRefusedWithoutSideEffects(store, fakeFs, rootDir, dbPath, expectedQueryCount: 2, rebuildCallbackInvoked);
    }

    [Theory]
    [InlineData("store_state", "0\t5")]
    [InlineData("store_state", "-1\t5")]
    [InlineData("store_state", "not-an-integer\t5")]
    [InlineData("metadata", "0")]
    [InlineData("metadata", "-3")]
    [InlineData("metadata", "not-an-integer")]
    [InlineData("metadata", "")]
    public void EnsureInitialized_RefusesInvalidSchemaVersions_WithoutMutatingOrDeletingFiles(
        string discoveredTable,
        string versionQueryResponse)
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = CreateExistingDatabaseSet(dbPath);
        bool rebuildCallbackInvoked = false;

        ScriptedSqliteRelationalStore store = new(
            dbPath,
            new SqliteCircuitBreaker(),
            initialize: false,
            fakeFs,
            onRebuiltFromOlderSchema: () => rebuildCallbackInvoked = true);
        store.EnqueueResponse(discoveredTable);
        store.EnqueueResponse(versionQueryResponse);

        Assert.Throws<InvalidOperationException>(() => store.EnsureInitialized());
        AssertRefusedWithoutSideEffects(store, fakeFs, rootDir, dbPath, expectedQueryCount: 2, rebuildCallbackInvoked);
    }

    [Theory]
    [InlineData("store_state", "1\t5")]
    [InlineData("store_state", "24\t5")]
    [InlineData("metadata", "1")]
    [InlineData("metadata", "24")]
    public void EnsureInitialized_RefusesOlderRelationalOrLegacyStoreUntilRebuild_WithoutDeletingFiles(
        string discoveredTable,
        string versionQueryResponse)
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = CreateExistingDatabaseSet(dbPath);
        bool rebuildCallbackInvoked = false;

        ScriptedSqliteRelationalStore store = new(
            dbPath,
            new SqliteCircuitBreaker(),
            initialize: false,
            fakeFs,
            onRebuiltFromOlderSchema: () => rebuildCallbackInvoked = true);
        store.EnqueueResponse(discoveredTable);
        store.EnqueueResponse(versionQueryResponse);

        Assert.Throws<InvalidOperationException>(() => store.EnsureInitialized());
        AssertRefusedWithoutSideEffects(store, fakeFs, rootDir, dbPath, expectedQueryCount: 2, rebuildCallbackInvoked);
    }

    [Fact]
    public void EnsureInitialized_WhenDatabaseAbsent_CreatesDirectoryViaFileSystemSeamAndRunsThreeStepInitialization()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "nested", "canon.db");
        FakeRelationalStoreFileSystem fakeFs = new();

        ScriptedSqliteRelationalStore store = new(
            new DashOptions { DatabasePath = dbPath },
            new SqliteCircuitBreaker(),
            fakeFs);

        Assert.False(Directory.Exists(rootDir));
        Assert.NotEmpty(fakeFs.CreatedDirectories);
        Assert.All(fakeFs.CreatedDirectories, dir => Assert.Equal(Path.GetDirectoryName(dbPath)!, dir));
        Assert.Empty(fakeFs.DeletedFiles);
        Assert.Equal(3, store.ExecutedSql.Count);

        string pragmaSql = NormalizeLineEndings(store.ExecutedSql[0]);
        Assert.Contains($"PRAGMA journal_size_limit = {SqliteRelationalStore.WalSizeLimitBytes.ToString(CultureInfo.InvariantCulture)};", pragmaSql, StringComparison.Ordinal);
        string expectedStoreStateDdl = NormalizeLineEndings(SchemaDdlGenerator.GenerateTableDdl(RelationalSchemaCatalog.GetTable("store_state"))).Trim();
        Assert.Contains(expectedStoreStateDdl, pragmaSql, StringComparison.Ordinal);

        string ddlSql = NormalizeLineEndings(store.ExecutedSql[2]);
        Assert.Contains(
            $"INSERT OR IGNORE INTO store_state (singleton_id, schema_version, detector_version) VALUES (1, {SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}, {SqliteRelationalStore.DetectorVersion.ToString(CultureInfo.InvariantCulture)});",
            ddlSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteTransaction_WhenNotYetInitialized_InitializesOnceBeforeRunningTransaction()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = CreateExistingDatabaseSet(dbPath);

        ScriptedSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker(), initialize: false, fakeFs);
        store.EnqueueResponse("store_state");
        store.EnqueueResponse($"{SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}\t{SqliteRelationalStore.DetectorVersion.ToString(CultureInfo.InvariantCulture)}");
        store.EnqueueResponse(string.Empty);
        store.EnqueueResponse(string.Empty);
        store.EnqueueResponse(string.Empty);

        store.DeleteSource("src-1");

        Assert.Equal(5, store.ExecutedSql.Count);
        Assert.Contains("sqlite_master", store.ExecutedSql[0], StringComparison.Ordinal);
        Assert.Contains("DELETE FROM source WHERE source_id =", store.ExecutedSql[4], StringComparison.Ordinal);

        store.ClearRecordedSql();
        store.DeleteSource("src-2");
        Assert.Single(store.ExecutedSql);
        Assert.Contains("DELETE FROM source WHERE source_id =", store.ExecutedSql[0], StringComparison.Ordinal);
        Assert.False(Directory.Exists(rootDir));
    }

    private static FakeRelationalStoreFileSystem CreateExistingDatabaseSet(string dbPath)
    {
        FakeRelationalStoreFileSystem fakeFs = new();
        fakeFs.AddExistingFile(dbPath);
        fakeFs.AddExistingFile(dbPath + "-wal");
        fakeFs.AddExistingFile(dbPath + "-shm");
        return fakeFs;
    }

    private static void AssertRefusedWithoutSideEffects(
        ScriptedSqliteRelationalStore store,
        FakeRelationalStoreFileSystem fakeFs,
        string rootDir,
        string dbPath,
        int expectedQueryCount,
        bool rebuildCallbackInvoked)
    {
        Assert.False(Directory.Exists(rootDir));
        Assert.False(rebuildCallbackInvoked);
        Assert.Empty(fakeFs.CreatedDirectories);
        Assert.Empty(fakeFs.DeletedFiles);
        Assert.True(fakeFs.FileExists(dbPath));
        Assert.True(fakeFs.FileExists(dbPath + "-wal"));
        Assert.True(fakeFs.FileExists(dbPath + "-shm"));
        Assert.Equal(expectedQueryCount, store.ExecutedSql.Count);

        foreach (string sql in store.ExecutedSql)
        {
            string normalized = NormalizeLineEndings(sql);
            Assert.StartsWith(".headers off\n.mode tabs\n", normalized, StringComparison.Ordinal);
            Assert.DoesNotContain("PRAGMA", normalized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CREATE TABLE", normalized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BEGIN IMMEDIATE", normalized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("INSERT", normalized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE", normalized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", normalized, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NewNonExistentRoot() =>
        Path.Combine(Path.GetTempPath(), $"kyberdash-t51-nowrite-{Guid.NewGuid():N}");

    private static string NormalizeLineEndings(string sql) =>
        sql.Replace("\r\n", "\n", StringComparison.Ordinal);

    private class FakeRelationalStoreFileSystem : IRelationalStoreFileSystem
    {
        private readonly HashSet<string> existingFiles = new(StringComparer.Ordinal);
        private readonly List<string> createdDirectories = [];
        private readonly List<string> deletedFiles = [];

        public IReadOnlyList<string> CreatedDirectories => createdDirectories;

        public IReadOnlyList<string> DeletedFiles => deletedFiles;

        public void AddExistingFile(string path) => existingFiles.Add(Path.GetFullPath(path));

        public bool FileExists(string path) => existingFiles.Contains(Path.GetFullPath(path));

        public void CreateDirectory(string path) => createdDirectories.Add(Path.GetFullPath(path));

        public void DeleteFile(string path)
        {
            string fullPath = Path.GetFullPath(path);
            deletedFiles.Add(fullPath);
            existingFiles.Remove(fullPath);
        }
    }

    private class ScriptedSqliteRelationalStore : SqliteRelationalStore
    {
        private readonly List<string> executedSql = [];
        private readonly Queue<string> queuedResponses = new();

        public ScriptedSqliteRelationalStore(
            DashOptions options,
            ICircuitBreaker circuitBreaker,
            IRelationalStoreFileSystem? fileSystem = null,
            Action? onRebuiltFromOlderSchema = null,
            Action<string>? log = null)
            : base(options, circuitBreaker, fileSystem, onRebuiltFromOlderSchema, log)
        {
        }

        public ScriptedSqliteRelationalStore(
            string databasePath,
            ICircuitBreaker circuitBreaker,
            bool initialize,
            IRelationalStoreFileSystem? fileSystem = null,
            Action? onRebuiltFromOlderSchema = null,
            Action<string>? log = null)
            : base(databasePath, circuitBreaker, initialize, fileSystem, onRebuiltFromOlderSchema, log)
        {
        }

        public IReadOnlyList<string> ExecutedSql => executedSql;

        public void EnqueueResponse(string response) => queuedResponses.Enqueue(response);

        public void ClearRecordedSql() => executedSql.Clear();

        protected override string ExecuteSqliteCore(string sql)
        {
            if (!FileSystem.FileExists(DatabasePath))
            {
                EnsureDatabaseDirectory();
            }

            executedSql.Add(sql);
            return queuedResponses.Count > 0 ? queuedResponses.Dequeue() : string.Empty;
        }
    }
}
