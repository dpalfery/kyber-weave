using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
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
        if (fileSystem.FileExists(DatabasePath))
        {
            int? existingVersion = ProbeExistingDatabaseSchemaVersion();
            if (existingVersion is int version && version != SchemaVersion)
            {
                ThrowWhenInvalidOrNewerSchemaVersion(version);
                RebuildOlderStoreUnread(version);
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
        string probeOutput = ExecuteSqlite(
            ".headers off\n" +
            ".mode tabs\n" +
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('store_state', 'metadata');");

        bool hasStoreState = false;
        bool hasMetadata = false;
        foreach (string line in TabLines(probeOutput))
        {
            string tableName = line.Trim();
            if (string.Equals(tableName, "store_state", StringComparison.Ordinal))
            {
                hasStoreState = true;
            }
            else if (string.Equals(tableName, "metadata", StringComparison.Ordinal))
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
        string output = ExecuteSqlite(
            ".headers off\n" +
            ".mode tabs\n" +
            "SELECT schema_version, detector_version FROM store_state WHERE singleton_id = 1 LIMIT 1;");

        foreach (string line in TabLines(output))
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
        string output = ExecuteSqlite(
            ".headers off\n" +
            ".mode tabs\n" +
            "SELECT value FROM metadata WHERE key = 'schema_version' LIMIT 1;");

        foreach (string line in TabLines(output))
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
        ExecuteSqlite(
            "PRAGMA journal_mode = WAL;\n" +
            "PRAGMA synchronous = NORMAL;\n" +
            $"PRAGMA journal_size_limit = {IntSql(WalSizeLimitBytes)};\n" +
            storeStateDdl);
    }

    private void ApplyFullSchemaAndStampState()
    {
        StringBuilder script = new();
        script.Append("PRAGMA foreign_keys = ON;\n");
        script.Append("BEGIN IMMEDIATE;\n");
        script.Append(SchemaDdlGenerator.GenerateSchemaDdl());
        string schemaVersionSql = IntSql(SchemaVersion);
        string detectorVersionSql = IntSql(DetectorVersion);
        script.Append(
            CultureInfo.InvariantCulture,
            $"INSERT OR IGNORE INTO store_state (singleton_id, schema_version, detector_version) VALUES (1, {schemaVersionSql}, {detectorVersionSql});\n");
        script.Append(
            CultureInfo.InvariantCulture,
            $"UPDATE store_state SET detector_version = {detectorVersionSql} WHERE singleton_id = 1 AND detector_version <> {detectorVersionSql};\n");
        script.Append("COMMIT;\n");
        ExecuteSqlite(script.ToString());
    }
}
