using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using KyberDash.Core.Repositories;
using KyberDash.Domain.Schema;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Summary of an automatic destructive rebuild performed when opening an incompatible older
/// relational store schema.
/// </summary>
/// <param name="DatabasePath">Full path to the rebuilt SQLite database file.</param>
/// <param name="OldSchemaVersion">Schema version discovered in the pre-existing store.</param>
/// <param name="NewSchemaVersion">Current schema version stamped after rebuild.</param>
/// <param name="SettingsResetToDefaults">Whether operator settings were reset to defaults.</param>
/// <param name="UnrecoverableHistoryClasses">History classes that could not be preserved across the rebuild.</param>
/// <param name="ReingestTriggered">Whether source re-ingestion was triggered after rebuild.</param>
public record StoreRebuildInfo(
    string DatabasePath,
    int OldSchemaVersion,
    int NewSchemaVersion,
    bool SettingsResetToDefaults,
    IReadOnlyList<string> UnrecoverableHistoryClasses,
    bool ReingestTriggered);

public partial class SqliteRelationalStore
{
    private int pendingRebuildReingest;

    /// <summary>Whether this store instance rebuilt an incompatible older database during initialization.</summary>
    public bool RebuiltFromOlderSchema { get; private set; }

    /// <summary>Schema version discovered before rebuilding an older database, or <see langword="null"/> when none occurred.</summary>
    public int? RebuiltFromSchemaVersion { get; private set; }

    /// <summary>Details of the most recent older-schema rebuild, or <see langword="null"/> when none occurred.</summary>
    public StoreRebuildInfo? LastRebuildInfo { get; private set; }

    /// <summary>Whether a post-rebuild re-ingest notification is still pending consumption.</summary>
    public bool HasPendingRebuildReingest => Volatile.Read(ref pendingRebuildReingest) != 0;

    /// <summary>Atomically claims and clears the pending post-rebuild re-ingest flag.</summary>
    /// <returns><see langword="true"/> if a pending re-ingest flag was present and cleared; otherwise <see langword="false"/>.</returns>
    public bool TryTakePendingRebuildReingest() => Interlocked.Exchange(ref pendingRebuildReingest, 0) != 0;

    /// <summary>
    /// Formats the refusal message emitted when opening a canonical store written by a newer schema version.
    /// </summary>
    /// <param name="path">Path to the SQLite database file.</param>
    /// <param name="fromVersion">Schema version recorded inside the database file.</param>
    /// <param name="toVersion">Maximum schema version supported by this build.</param>
    /// <returns>Human-readable refusal diagnostic.</returns>
    public static string FormatSchemaVersionRefusal(
        string path,
        int fromVersion,
        int toVersion = SchemaVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"canon store at {path} was built with schema version {fromVersion}, but this build understands version {toVersion}; upgrade KyberDash");
    }

    private Action? Initialize()
    {
        if (GetType() == typeof(SqliteRelationalStore) && !fileSystem.FileExists(DatabasePath))
        {
            EnsureDatabaseDirectory();
        }

        if (fileSystem.FileExists(DatabasePath))
        {
            int? existingVersion = ProbeExistingDatabaseSchemaVersion();
            if (existingVersion is int version && version != SchemaVersion)
            {
                ThrowWhenInvalidOrNewerSchemaVersion(version);
                RebuildOlderStoreUnread(version);
            }
            else if (existingVersion is not null)
            {
                InvalidateCheckpointsWhenSpanClaimsAreNew();
            }

            ApplyWalPragmasAndStoreState();
            ApplyFullSchemaAndStampState();
            return ConsumeRebuildCallback();
        }

        ApplyWalPragmasAndStoreState();
        int? createdVersion = ReadStoredSchemaVersion();
        if (createdVersion is int currentVersion)
        {
            ValidateExistingSchemaVersion(currentVersion);
        }

        ApplyFullSchemaAndStampState();
        return ConsumeRebuildCallback();
    }

    /// <summary>
    /// A store written before <c>record_source_claim</c> existed holds shared spans with no claims, so the first
    /// re-import of their owning file would drop a span another file still holds. Before the schema adds the
    /// table, mark every folder checkpoint stale: the next folder import, whenever it runs, re-reads every file
    /// and records each file's claims. This imports nothing by itself. Runs only while the claim table is
    /// missing, and only when <c>source_checkpoint</c> exists: SQLite refuses an UPDATE on a missing table, and
    /// the schema step that follows recreates it.
    /// </summary>
    private void InvalidateCheckpointsWhenSpanClaimsAreNew()
    {
        IReadOnlyList<string> tableNames = ReadNameRows(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('source_checkpoint', 'record_source_claim');\n");

        HashSet<string> present = new(tableNames.Select(static name => name.Trim()), StringComparer.Ordinal);
        if (!present.Contains("source_checkpoint") || present.Contains("record_source_claim"))
        {
            return;
        }

        ExecuteSchemaBody("UPDATE source_checkpoint SET last_status = 'invalidated';\n");
    }

    /// <summary>
    /// Deletes an incompatible older store without reading any data rows, recreates the directory
    /// and schema skeleton state, and records the rebuild so the caller triggers full re-ingest.
    /// </summary>
    /// <param name="version">Schema version discovered in the pre-existing store (<c>1 &lt;= version &lt; <see cref="SchemaVersion"/></c>).</param>
    private void RebuildOlderStoreUnread(int version)
    {
        fileSystem.DeleteFile(DatabasePath);
        fileSystem.DeleteFile(DatabasePath + "-wal");
        fileSystem.DeleteFile(DatabasePath + "-shm");

        log.Invoke(
            $"Deleted incompatible relational store at '{DatabasePath}' with schema version {IntSql(version)} " +
            $"(expected schema version {IntSql(SchemaVersion)}); removed canon.db, -wal, and -shm unread with no backup. " +
            $"Recreated relational schema version {IntSql(SchemaVersion)}; operator settings (operator_setting_*) were reset to defaults. " +
            "Unrecoverable history classes: OTLP-only, unevidenced (zcode). Full source re-ingest triggered.");

        bool reingestTriggered = onRebuiltFromOlderSchema is not null;
        LastRebuildInfo = new StoreRebuildInfo(
            Path.GetFullPath(DatabasePath),
            version,
            SchemaVersion,
            SettingsResetToDefaults: true,
            UnrecoverableHistoryClasses: ["OTLP-only", "unevidenced (zcode)"],
            ReingestTriggered: reingestTriggered);
        RebuiltFromOlderSchema = true;
        RebuiltFromSchemaVersion = version;

        if (!reingestTriggered)
        {
            Interlocked.Exchange(ref pendingRebuildReingest, 1);
        }
    }

    /// <summary>
    /// Returns the post-rebuild re-ingest callback when a rebuild occurred, or <see langword="null"/>
    /// when no rebuild occurred or the pending re-ingest flag was armed instead.
    /// </summary>
    private Action? ConsumeRebuildCallback() => RebuiltFromOlderSchema ? onRebuiltFromOlderSchema : null;

    /// <summary>
    /// Throws when <paramref name="version"/> is invalid or newer than <see cref="SchemaVersion"/>.
    /// </summary>
    /// <param name="version">Schema version discovered in the pre-existing store.</param>
    private void ThrowWhenInvalidOrNewerSchemaVersion(int version)
    {
        if (version <= 0)
        {
            string invalidMessage = $"Relational store at '{DatabasePath}' has invalid schema_version {IntSql(version)}.";
            log.Invoke(invalidMessage);
            throw new InvalidOperationException(invalidMessage);
        }

        if (version > SchemaVersion)
        {
            string refusalMessage = FormatSchemaVersionRefusal(DatabasePath, version);
            log.Invoke(refusalMessage);
            throw new InvalidOperationException(refusalMessage);
        }
    }

    private int? ProbeExistingDatabaseSchemaVersion()
    {
        IReadOnlyList<string> tableNames = ReadNameRows(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('store_state', 'metadata');\n");

        bool hasStoreState = false;
        bool hasMetadata = false;
        foreach (string tableName in tableNames)
        {
            if (string.Equals(tableName.Trim(), "store_state", StringComparison.Ordinal))
            {
                hasStoreState = true;
            }
            else if (string.Equals(tableName.Trim(), "metadata", StringComparison.Ordinal))
            {
                hasMetadata = true;
            }
        }

        if (hasStoreState)
        {
            return ReadStoredSchemaVersion();
        }

        if (hasMetadata)
        {
            return ReadLegacyMetadataSchemaVersion();
        }

        return null;
    }

    private int? ReadStoredSchemaVersion()
    {
        IReadOnlyList<string> rows = ReadSchemaVersionRows(
            "SELECT schema_version, detector_version FROM store_state WHERE singleton_id = 1 LIMIT 1;\n");

        foreach (string line in rows)
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length == 0 || !int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) || version <= 0)
            {
                string message = $"Relational store at '{DatabasePath}' has invalid schema_version row '{line}'.";
                log.Invoke(message);
                throw new InvalidOperationException(message);
            }

            return version;
        }

        return null;
    }

    private int ReadLegacyMetadataSchemaVersion()
    {
        IReadOnlyList<string> rows = ReadValueRows(
            "SELECT value FROM metadata WHERE key = 'schema_version' LIMIT 1;\n");

        foreach (string line in rows)
        {
            if (!int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) || version <= 0)
            {
                string message = $"Relational store at '{DatabasePath}' has invalid legacy metadata schema_version '{line}'.";
                log.Invoke(message);
                throw new InvalidOperationException(message);
            }

            return version;
        }

        string missingMessage = $"Relational store at '{DatabasePath}' has a legacy metadata table without a schema_version row.";
        log.Invoke(missingMessage);
        throw new InvalidOperationException(missingMessage);
    }

    private void ValidateExistingSchemaVersion(int version)
    {
        ThrowWhenInvalidOrNewerSchemaVersion(version);

        if (version < SchemaVersion)
        {
            string olderMessage =
                $"Relational store at '{DatabasePath}' is at schema version {IntSql(version)} and requires migration to {IntSql(SchemaVersion)}.";
            log.Invoke(olderMessage);
            throw new InvalidOperationException(olderMessage);
        }
    }

    private void ApplyWalPragmasAndStoreState()
    {
        string storeStateDdl = SchemaDdlGenerator.GenerateTableDdl(RelationalSchemaCatalog.GetTable("store_state"));
        string journalMode = "PRAGMA journal_mode = WAL;\n";
        string synchronous = "PRAGMA synchronous = NORMAL;\n";
        string sizeLimit = $"PRAGMA journal_size_limit = {IntSql(WalSizeLimitBytes)};\n";

        if (GetType() != typeof(SqliteRelationalStore))
        {
            CircuitBreaker.Execute(() => ExecuteSqliteCore(journalMode + synchronous + sizeLimit + storeStateDdl));
            return;
        }

        // Journal mode cannot change inside a transaction, so the pragmas run as one script and the
        // table DDL that follows runs through the transactional statement path.
        CircuitBreaker.Execute(() => ExecuteSqliteCore(journalMode + synchronous + sizeLimit));
        ExecuteStatements([SqlStatement.Of(storeStateDdl)]);
    }

    private void ApplyFullSchemaAndStampState()
    {
        string schemaDdl = SchemaDdlGenerator.GenerateSchemaDdl();
        string stampSql =
            $"INSERT OR IGNORE INTO store_state (singleton_id, schema_version, detector_version) VALUES (1, $schemaVersion, $detectorVersion);\n";

        if (GetType() != typeof(SqliteRelationalStore))
        {
            StringBuilder script = new();
            script.Append("PRAGMA foreign_keys = ON;\n");
            script.Append("BEGIN IMMEDIATE;\n");
            script.Append(schemaDdl);
            script.Append(
                CultureInfo.InvariantCulture,
                $"INSERT OR IGNORE INTO store_state (singleton_id, schema_version, detector_version) VALUES (1, {IntSql(SchemaVersion)}, {IntSql(DetectorVersion)});\n");
            script.Append("COMMIT;\n");
            CircuitBreaker.Execute(() => ExecuteSqliteCore(script.ToString()));
            return;
        }

        SqlStatement schemaStatement = SqlStatement.Of(schemaDdl);
        SqlStatement stampStatement = new(
            stampSql,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["$schemaVersion"] = SchemaVersion,
                ["$detectorVersion"] = DetectorVersion,
            });
        ExecuteStatements([schemaStatement, stampStatement]);
    }

    private IReadOnlyList<string> ReadNameRows(string sql)
    {
        if (GetType() == typeof(SqliteRelationalStore))
        {
            return Query<string>(
                sql,
                EmptyParameters(),
                static reader => reader.GetString(reader.GetOrdinal("name")));
        }

        return ReadScriptedRows(sql);
    }

    private IReadOnlyList<string> ReadSchemaVersionRows(string sql)
    {
        if (GetType() == typeof(SqliteRelationalStore))
        {
            return Query<string>(
                sql,
                EmptyParameters(),
                static reader =>
                {
                    object version = reader.GetValue(reader.GetOrdinal("schema_version"));
                    object detectorVersion = reader.GetValue(reader.GetOrdinal("detector_version"));
                    return Convert.ToString(version, CultureInfo.InvariantCulture) +
                        "\t" +
                        Convert.ToString(detectorVersion, CultureInfo.InvariantCulture);
                });
        }

        return ReadScriptedRows(sql);
    }

    private IReadOnlyList<string> ReadValueRows(string sql)
    {
        if (GetType() == typeof(SqliteRelationalStore))
        {
            return Query<string>(
                sql,
                EmptyParameters(),
                static reader => Convert.ToString(
                    reader.GetValue(reader.GetOrdinal("value")),
                    CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return ReadScriptedRows(sql);
    }

    private IReadOnlyList<string> ReadScriptedRows(string sql)
    {
        string output = CircuitBreaker.Execute(() => ExecuteSqliteCore(".headers off\n.mode tabs\n" + sql));
        return output
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private void ExecuteSchemaBody(string bodySql)
    {
        if (GetType() == typeof(SqliteRelationalStore))
        {
            ExecuteStatements([SqlStatement.Of(bodySql)]);
            return;
        }

        CircuitBreaker.Execute(() => ExecuteSqliteCore("BEGIN IMMEDIATE;\n" + bodySql + "COMMIT;\n"));
    }

    private static Dictionary<string, object?> EmptyParameters() => new(StringComparer.Ordinal);
}
