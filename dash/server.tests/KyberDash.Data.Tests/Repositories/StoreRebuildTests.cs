using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Verifies destructive rebuild of incompatible older relational stores: unread deletion of
/// <c>canon.db</c>, <c>-wal</c>, and <c>-shm</c> without backup, fresh schema creation stamped
/// at <c>v26</c>, rebuild diagnostics logging, and full source re-ingest triggering, all over
/// an in-memory <see cref="IRelationalStoreFileSystem"/> seam without opening real SQLite files.
/// </summary>
public class StoreRebuildTests
{
    [Theory]
    [InlineData("store_state", "25\t5", 25)]
    [InlineData("store_state", "24\t5", 24)]
    [InlineData("store_state", "1\t5", 1)]
    [InlineData("metadata", "25", 25)]
    [InlineData("metadata", "24", 24)]
    [InlineData("metadata", "1", 1)]
    public void EnsureInitialized_RebuildsOlderStoreUnread_DeletesFilesStampsV26AndTriggersCallback(
        string discoveredTable,
        string versionQueryResponse,
        int expectedOldVersion)
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = CreateExistingDatabaseSet(dbPath);
        List<string> logs = [];
        bool rebuildCallbackInvoked = false;

        ScriptedSqliteRelationalStore store = new(
            dbPath,
            new SqliteCircuitBreaker(),
            initialize: false,
            fakeFs,
            onRebuiltFromOlderSchema: () => rebuildCallbackInvoked = true,
            log: logs.Add);
        store.EnqueueResponse(discoveredTable);
        store.EnqueueResponse(versionQueryResponse);
        store.EnqueueResponse(string.Empty);
        store.EnqueueResponse(string.Empty);

        store.EnsureInitialized();

        Assert.False(Directory.Exists(rootDir));
        Assert.False(File.Exists(dbPath));
        Assert.Equal(
            [Path.GetFullPath(dbPath), Path.GetFullPath(dbPath + "-wal"), Path.GetFullPath(dbPath + "-shm")],
            fakeFs.DeletedFiles);
        Assert.False(fakeFs.FileExists(dbPath));
        Assert.False(fakeFs.FileExists(dbPath + "-wal"));
        Assert.False(fakeFs.FileExists(dbPath + "-shm"));

        Assert.True(store.RebuiltFromOlderSchema);
        Assert.Equal(expectedOldVersion, store.RebuiltFromSchemaVersion);
        Assert.NotNull(store.LastRebuildInfo);
        Assert.Equal(Path.GetFullPath(dbPath), store.LastRebuildInfo.DatabasePath);
        Assert.Equal(expectedOldVersion, store.LastRebuildInfo.OldSchemaVersion);
        Assert.Equal(SqliteRelationalStore.SchemaVersion, store.LastRebuildInfo.NewSchemaVersion);
        Assert.True(store.LastRebuildInfo.SettingsResetToDefaults);
        Assert.Equal(["OTLP-only", "unevidenced (zcode)"], store.LastRebuildInfo.UnrecoverableHistoryClasses);
        Assert.True(store.LastRebuildInfo.ReingestTriggered);

        Assert.True(rebuildCallbackInvoked);
        Assert.False(store.HasPendingRebuildReingest);
        Assert.False(store.TryTakePendingRebuildReingest());

        Assert.Single(logs);
        Assert.Contains(Path.GetFullPath(dbPath), logs[0], StringComparison.Ordinal);
        Assert.Contains($"schema version {expectedOldVersion.ToString(CultureInfo.InvariantCulture)}", logs[0], StringComparison.Ordinal);
        Assert.Contains($"schema version {SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}", logs[0], StringComparison.Ordinal);
        Assert.Contains("operator_setting", logs[0], StringComparison.Ordinal);
        Assert.Contains("reset to defaults", logs[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OTLP-only", logs[0], StringComparison.Ordinal);
        Assert.Contains("unevidenced (zcode)", logs[0], StringComparison.Ordinal);
        Assert.Contains("re-ingest", logs[0], StringComparison.OrdinalIgnoreCase);

        Assert.Equal(4, store.ExecutedSql.Count);
        AssertZeroDataRowReads(store);
        string probeSql = NormalizeLineEndings(store.ExecutedSql[0]);
        Assert.Contains("sqlite_master", probeSql, StringComparison.Ordinal);
        string pragmaSql = NormalizeLineEndings(store.ExecutedSql[2]);
        Assert.Contains("PRAGMA journal_mode = WAL;", pragmaSql, StringComparison.Ordinal);
        string ddlSql = NormalizeLineEndings(store.ExecutedSql[3]);
        Assert.Contains(NormalizeLineEndings(SchemaDdlGenerator.GenerateSchemaDdl()).Trim(), ddlSql, StringComparison.Ordinal);
        Assert.Contains(
            $"INSERT OR IGNORE INTO store_state (singleton_id, schema_version, detector_version) VALUES (1, {SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}, {SqliteRelationalStore.DetectorVersion.ToString(CultureInfo.InvariantCulture)});",
            ddlSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureInitialized_RebuildCallbackRunsOutsideWriteGateAfterInitialization()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = CreateExistingDatabaseSet(dbPath);
        List<string> logs = [];
        ScriptedSqliteRelationalStore? store = null;
        bool callbackRan = false;

        store = new ScriptedSqliteRelationalStore(
            dbPath,
            new SqliteCircuitBreaker(),
            initialize: false,
            fakeFs,
            onRebuiltFromOlderSchema: () =>
            {
                callbackRan = true;
                store!.EnsureInitialized();
            },
            log: logs.Add);
        store.EnqueueResponse("store_state");
        store.EnqueueResponse("24\t5");
        store.EnqueueResponse(string.Empty);
        store.EnqueueResponse(string.Empty);

        store.EnsureInitialized();

        Assert.True(callbackRan);
        Assert.True(store.RebuiltFromOlderSchema);
        Assert.Equal(4, store.ExecutedSql.Count);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void EnsureInitialized_RebuildWithoutCallback_ArmsPendingReingestFlag()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = CreateExistingDatabaseSet(dbPath);
        List<string> logs = [];

        ScriptedSqliteRelationalStore store = new(
            dbPath,
            new SqliteCircuitBreaker(),
            initialize: false,
            fakeFs,
            log: logs.Add);
        store.EnqueueResponse("metadata");
        store.EnqueueResponse("24");
        store.EnqueueResponse(string.Empty);
        store.EnqueueResponse(string.Empty);

        store.EnsureInitialized();

        Assert.True(store.RebuiltFromOlderSchema);
        Assert.Equal(24, store.RebuiltFromSchemaVersion);
        Assert.NotNull(store.LastRebuildInfo);
        Assert.False(store.LastRebuildInfo.ReingestTriggered);
        Assert.True(store.HasPendingRebuildReingest);
        Assert.True(store.TryTakePendingRebuildReingest());
        Assert.False(store.HasPendingRebuildReingest);
        Assert.False(store.TryTakePendingRebuildReingest());
        Assert.Single(logs);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void EnsureInitialized_RebuildToleratesAbsentWalAndShm()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = new();
        fakeFs.AddExistingFile(dbPath);
        bool rebuildCallbackInvoked = false;

        ScriptedSqliteRelationalStore store = new(
            dbPath,
            new SqliteCircuitBreaker(),
            initialize: false,
            fakeFs,
            onRebuiltFromOlderSchema: () => rebuildCallbackInvoked = true);
        store.EnqueueResponse("store_state");
        store.EnqueueResponse("24\t5");
        store.EnqueueResponse(string.Empty);
        store.EnqueueResponse(string.Empty);

        store.EnsureInitialized();

        Assert.True(rebuildCallbackInvoked);
        Assert.True(store.RebuiltFromOlderSchema);
        Assert.Equal(
            [Path.GetFullPath(dbPath), Path.GetFullPath(dbPath + "-wal"), Path.GetFullPath(dbPath + "-shm")],
            fakeFs.DeletedFiles);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void EnsureInitialized_CurrentV26Store_OpensWithoutDeletingOrReingesting()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = CreateExistingDatabaseSet(dbPath);
        List<string> logs = [];
        bool rebuildCallbackInvoked = false;

        ScriptedSqliteRelationalStore store = new(
            dbPath,
            new SqliteCircuitBreaker(),
            initialize: false,
            fakeFs,
            onRebuiltFromOlderSchema: () => rebuildCallbackInvoked = true,
            log: logs.Add);
        store.EnqueueResponse("store_state");
        store.EnqueueResponse($"{SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}\t{SqliteRelationalStore.DetectorVersion.ToString(CultureInfo.InvariantCulture)}");
        store.EnqueueResponse(string.Empty);
        store.EnqueueResponse(string.Empty);

        store.EnsureInitialized();

        Assert.Empty(fakeFs.DeletedFiles);
        Assert.False(rebuildCallbackInvoked);
        Assert.False(store.RebuiltFromOlderSchema);
        Assert.Null(store.RebuiltFromSchemaVersion);
        Assert.Null(store.LastRebuildInfo);
        Assert.False(store.HasPendingRebuildReingest);
        Assert.Empty(logs);
        Assert.True(fakeFs.FileExists(dbPath));
        Assert.False(Directory.Exists(rootDir));
    }

    [Theory]
    [InlineData("store_state", "0\t5")]
    [InlineData("metadata", "0")]
    public void EnsureInitialized_InvalidSchemaVersionStillThrows_WithoutDeletingFiles(
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

        Assert.Empty(fakeFs.DeletedFiles);
        Assert.False(rebuildCallbackInvoked);
        Assert.False(store.RebuiltFromOlderSchema);
        Assert.Null(store.LastRebuildInfo);
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void EnsureInitialized_RebuildLogsToConsoleErrorByDefaultPerD15()
    {
        string rootDir = NewNonExistentRoot();
        string dbPath = Path.Combine(rootDir, "canon.db");
        FakeRelationalStoreFileSystem fakeFs = CreateExistingDatabaseSet(dbPath);

        // D15: the store's diagnostic log defaults to Console.Error, so a destructive rebuild is
        // never silent even when no host or test supplies a logger. The capture is installed
        // before the store is constructed because the default binds the Console.Error instance it
        // finds at construction time — a swap made afterwards cannot intercept its writes.
        using StringWriter captured = new();
        TextWriter originalError = Console.Error;
        Console.SetError(captured);
        try
        {
            ScriptedSqliteRelationalStore store = new(
                dbPath,
                new SqliteCircuitBreaker(),
                initialize: false,
                fakeFs);
            store.EnqueueResponse("store_state");
            store.EnqueueResponse("24\t5");
            store.EnqueueResponse(string.Empty);
            store.EnqueueResponse(string.Empty);

            store.EnsureInitialized();

            Assert.True(store.RebuiltFromOlderSchema);
        }
        finally
        {
            Console.SetError(originalError);
        }

        string logged = captured.ToString();
        Assert.Contains("Deleted incompatible relational store", logged, StringComparison.Ordinal);
        Assert.Contains("schema version 24", logged, StringComparison.Ordinal);
        Assert.Contains(
            $"schema version {SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}",
            logged,
            StringComparison.Ordinal);
        Assert.Contains("re-ingest", logged, StringComparison.OrdinalIgnoreCase);
    }

    private static FakeRelationalStoreFileSystem CreateExistingDatabaseSet(string dbPath)
    {
        FakeRelationalStoreFileSystem fakeFs = new();
        fakeFs.AddExistingFile(dbPath);
        fakeFs.AddExistingFile(dbPath + "-wal");
        fakeFs.AddExistingFile(dbPath + "-shm");
        return fakeFs;
    }

    private static void AssertZeroDataRowReads(ScriptedSqliteRelationalStore store)
    {
        foreach (string sql in store.ExecutedSql)
        {
            if (!sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.DoesNotContain("FROM records", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FROM session", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FROM settings", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("operator_setting", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NewNonExistentRoot() =>
        Path.Combine(Path.GetTempPath(), $"kyberdash-t52-rebuild-{Guid.NewGuid():N}");

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
