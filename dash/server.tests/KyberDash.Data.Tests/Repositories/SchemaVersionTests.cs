using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KyberDash.Contracts.Services;
using KyberDash.Core.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;
using KyberDash.Domain.Schema;
using Microsoft.Data.Sqlite;
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
        Assert.Equal(30, SqliteRelationalStore.SchemaVersion);
        Assert.Equal(8, SqliteRelationalStore.DetectorVersion);
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
        string message = SqliteRelationalStore.FormatSchemaVersionRefusal("/data/canon.db", 31);

        Assert.Equal(
            $"canon store at /data/canon.db was built with schema version 31, but this build understands version {SqliteRelationalStore.SchemaVersion.ToString(CultureInfo.InvariantCulture)}; upgrade KyberDash",
            message);
        Assert.Throws<ArgumentException>(() => SqliteRelationalStore.FormatSchemaVersionRefusal(" ", 31));
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
        store.EnqueueResponse("source_checkpoint");
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

        Assert.Equal(7, store.ExecutedSql.Count);
        string probeSql = NormalizeLineEndings(store.ExecutedSql[0]);
        Assert.Contains("sqlite_master", probeSql, StringComparison.Ordinal);
        Assert.DoesNotContain("PRAGMA", probeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", probeSql, StringComparison.OrdinalIgnoreCase);

        string versionSql = NormalizeLineEndings(store.ExecutedSql[1]);
        Assert.Contains("SELECT schema_version, detector_version FROM store_state WHERE singleton_id = 1 LIMIT 1;", versionSql, StringComparison.Ordinal);

        // Before the schema adds record_source_claim, an existing store's folder checkpoints are marked stale
        // so the next folder import records every file's span claims; it is a no-op once the table exists.
        string claimProbeSql = NormalizeLineEndings(store.ExecutedSql[2]);
        Assert.Contains("'source_checkpoint', 'record_source_claim'", claimProbeSql, StringComparison.Ordinal);
        string claimSql = NormalizeLineEndings(store.ExecutedSql[3]);
        Assert.Contains("UPDATE source_checkpoint SET last_status = 'invalidated';", claimSql, StringComparison.Ordinal);

        string ddlSql = NormalizeLineEndings(store.ExecutedSql[6]);
        Assert.DoesNotContain("UPDATE store_state", ddlSql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("store_state", "31\t5", 31)]
    [InlineData("store_state", "99\t5", 99)]
    [InlineData("metadata", "31", 31)]
    [InlineData("metadata", "32", 32)]
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
    [InlineData("store_state", "1\t5", 1)]
    [InlineData("store_state", "24\t5", 24)]
    [InlineData("metadata", "1", 1)]
    [InlineData("metadata", "24", 24)]
    public void EnsureInitialized_RebuildsOlderRelationalOrLegacyStoreUnread_WithoutBackup(
        string discoveredTable,
        string versionQueryResponse,
        int expectedOldVersion)
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

        store.EnsureInitialized();

        Assert.False(Directory.Exists(rootDir));
        Assert.Equal(
            [Path.GetFullPath(dbPath), Path.GetFullPath(dbPath + "-wal"), Path.GetFullPath(dbPath + "-shm")],
            fakeFs.DeletedFiles);
        Assert.True(rebuildCallbackInvoked);
        Assert.True(store.RebuiltFromOlderSchema);
        Assert.Equal(expectedOldVersion, store.RebuiltFromSchemaVersion);
        Assert.NotNull(store.LastRebuildInfo);
        Assert.Equal(expectedOldVersion, store.LastRebuildInfo.OldSchemaVersion);
        Assert.Equal(SqliteRelationalStore.SchemaVersion, store.LastRebuildInfo.NewSchemaVersion);
        Assert.Equal(5, store.ExecutedSql.Count);
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
        Assert.Equal(4, store.ExecutedSql.Count);

        string pragmaSql = NormalizeLineEndings(store.ExecutedSql[0]);
        Assert.Contains("PRAGMA journal_mode = WAL;", pragmaSql, StringComparison.Ordinal);
        // The per-connection settings run on every connection the store opens, not once here.
        string perConnection = string.Join("\n", store.PragmasPerConnection.Select(static pragma => pragma.Sql));
        Assert.Contains($"PRAGMA journal_size_limit = {SqliteRelationalStore.WalSizeLimitBytes.ToString(CultureInfo.InvariantCulture)};", perConnection, StringComparison.Ordinal);
        string expectedStoreStateDdl = NormalizeLineEndings(SchemaDdlGenerator.GenerateTableDdl(RelationalSchemaCatalog.GetTable("store_state"))).Trim();
        Assert.Contains(expectedStoreStateDdl, NormalizeLineEndings(store.ExecutedSql[1]), StringComparison.Ordinal);

        string ddlSql = NormalizeLineEndings(store.ExecutedSql[3]);
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

        Assert.Equal(7, store.ExecutedSql.Count);
        Assert.Contains("sqlite_master", store.ExecutedSql[0], StringComparison.Ordinal);
        Assert.Contains("DELETE FROM source WHERE source_id =", store.ExecutedSql[6], StringComparison.Ordinal);

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
            Assert.DoesNotContain(".headers off", normalized, StringComparison.Ordinal);
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
        public IReadOnlyList<SqlStatement> PragmasPerConnection => ConnectionPragmas;

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

        protected override IReadOnlyList<T> QueryRows<T>(
            string sql,
            IReadOnlyDictionary<string, object?> parameters,
            Func<SqliteDataReader, T> map)
        {
            executedSql.Add(sql);
            if (sql.Contains("PRAGMA", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            if (queuedResponses.Count > 0)
            {
                string response = queuedResponses.Dequeue();
                if (typeof(T) == typeof(string))
                {
                    return response.Length == 0
                        ? []
                        : (IReadOnlyList<T>)(object)new List<string> { response };
                }
            }

            return [];
        }

        protected override void ExecuteStatements(IReadOnlyList<SqlStatement> statements)
        {
            if (!FileSystem.FileExists(DatabasePath))
            {
                EnsureDatabaseDirectory();
            }

            StringBuilder script = new();
            script.Append("BEGIN IMMEDIATE;\n");
            foreach (SqlStatement statement in statements)
            {
                script.Append(RenderStatement(statement));
            }

            script.Append("COMMIT;\n");
            executedSql.Add(script.ToString());
        }

        protected override void ExecuteStatementsWithoutTransaction(IReadOnlyList<SqlStatement> statements)
        {
            StringBuilder script = new();
            foreach (SqlStatement statement in statements)
            {
                script.Append(RenderStatement(statement));
            }

            executedSql.Add(script.ToString());
        }

        private static string RenderStatement(SqlStatement statement)
        {
            string sql = statement.Sql;
            string[] names = [.. statement.Parameters.Keys];
            Array.Sort(names, static (left, right) => right.Length.CompareTo(left.Length));
            foreach (string name in names)
            {
                sql = sql.Replace(name, RenderParameterLiteral(statement.Parameters[name]), StringComparison.Ordinal);
            }

            return sql;
        }

        private static string RenderParameterLiteral(object? value) => value switch
        {
            null => "NULL",
            string text => $"X'{Convert.ToHexString(Encoding.UTF8.GetBytes(text))}' || ''",
            byte[] bytes => $"X'{Convert.ToHexString(bytes)}'",
            double real => real.ToString("G17", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "NULL",
        };
    }
}
