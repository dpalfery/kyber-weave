using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Microsoft.Data.Sqlite;

namespace KyberDash.Data.Repositories;

public partial class SqliteRelationalStore : IOperationalStore
{
    private static readonly string[] MetricPointChildTables =
    [
        "metric_point_unit",
        "metric_point_temporality",
        "metric_point_time",
        "metric_point_start_time",
        "metric_point_timestamp",
        "metric_point_value",
        "metric_point_count",
        "metric_point_sum",
        "metric_point_source",
    ];

    private static readonly Func<string, string, string?, string> LegacyGuardedTextValidator =
        GuardedTextExpr;

    /// <inheritdoc />
    public SettingsView ReadSettings()
    {
        string sql =
            "SELECT " +
            "COALESCE((SELECT scheduled FROM operator_setting_folder_import WHERE singleton_id = 1), 0) AS scheduled, " +
            "COALESCE((SELECT paused FROM operator_setting_jobs WHERE singleton_id = 1), 0) AS paused, " +
            "COALESCE((SELECT minutes FROM operator_setting_refresh_cadence WHERE singleton_id = 1), 5) AS minutes, " +
            "COALESCE((SELECT hosted FROM operator_setting_receiver WHERE singleton_id = 1), 0) AS hosted, " +
            "COALESCE((SELECT port FROM operator_setting_dashboard WHERE singleton_id = 1), 4747) AS port;\n";

        IReadOnlyList<SettingsView> rows = QueryRows<SettingsView>(
            sql,
            NewParameters(),
            reader => new SettingsView(
                FolderImportScheduled: ParseBoolFlag(CellText(reader, "scheduled"), "operator_setting_folder_import.scheduled"),
                JobsPaused: ParseBoolFlag(CellText(reader, "paused"), "operator_setting_jobs.paused"),
                RefreshCadenceMinutes: ParseInt32Required(CellText(reader, "minutes"), "operator_setting_refresh_cadence.minutes"),
                ReceiverHosted: ParseBoolFlag(CellText(reader, "hosted"), "operator_setting_receiver.hosted"),
                DashboardPort: ParseInt32Required(CellText(reader, "port"), "operator_setting_dashboard.port")));

        if (rows.Count != 1)
        {
            throw new StoredDataException($"Expected single scalar row from operator_setting query, got {rows.Count}.");
        }

        return rows[0];
    }

    /// <inheritdoc />
    public void SaveSettings(SettingsView settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RequireRangeInt(settings.RefreshCadenceMinutes, 1, 1440, "operator_setting_refresh_cadence", "minutes");
        RequireRangeInt(settings.DashboardPort, 1, 65535, "operator_setting_dashboard", "port");

        SqlBatch batch = new();
        batch.Append(
            $"INSERT OR REPLACE INTO operator_setting_folder_import (singleton_id, scheduled) VALUES (1, {batch.Add(settings.FolderImportScheduled ? 1 : 0)});\n");
        batch.Append(
            $"INSERT OR REPLACE INTO operator_setting_jobs (singleton_id, paused) VALUES (1, {batch.Add(settings.JobsPaused ? 1 : 0)});\n");
        batch.Append(
            $"INSERT OR REPLACE INTO operator_setting_refresh_cadence (singleton_id, minutes) VALUES (1, {batch.Add(settings.RefreshCadenceMinutes)});\n");
        batch.Append(
            $"INSERT OR REPLACE INTO operator_setting_receiver (singleton_id, hosted) VALUES (1, {batch.Add(settings.ReceiverHosted ? 1 : 0)});\n");
        batch.Append(
            $"INSERT OR REPLACE INTO operator_setting_dashboard (singleton_id, port) VALUES (1, {batch.Add(settings.DashboardPort)});\n");

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public StoreStateView? GetStoreState()
    {
        string sql =
            "SELECT schema_version, detector_version FROM store_state WHERE singleton_id = 1 LIMIT 1;\n";

        IReadOnlyList<StoreStateView> rows = QueryRows<StoreStateView>(
            sql,
            NewParameters(),
            reader => new StoreStateView(
                ParseInt32Required(CellText(reader, "schema_version"), "store_state.schema_version"),
                ParseInt32Required(CellText(reader, "detector_version"), "store_state.detector_version")));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public void SetStoreState(StoreStateView state)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequirePositiveInt(state.SchemaVersion, "store_state", "schema_version");
        RequirePositiveInt(state.DetectorVersion, "store_state", "detector_version");

        SqlBatch batch = new();
        batch.Append(
            $"INSERT OR REPLACE INTO store_state (singleton_id, schema_version, detector_version) VALUES (1, {batch.Add(state.SchemaVersion)}, {batch.Add(state.DetectorVersion)});\n");
        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public long GetWriteMarker()
    {
        string sql = "SELECT records_write_seq FROM store_write_marker WHERE singleton_id = 1 LIMIT 1;\n";

        IReadOnlyList<long> rows = Query<long>(sql, NewParameters(), reader => reader.GetInt64(reader.GetOrdinal("records_write_seq")));
        return rows.Count == 0 ? 0L : rows[0];
    }

    /// <inheritdoc />
    public void SetWriteMarker(long recordsWriteSeq)
    {
        RequireNonNegative(recordsWriteSeq, "store_write_marker", "records_write_seq");
        SqlBatch batch = new();
        batch.Append(
            $"INSERT OR REPLACE INTO store_write_marker (singleton_id, records_write_seq) VALUES (1, {batch.Add(recordsWriteSeq)});\n");
        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public string? GetLastCleanAtUtc() =>
        ReadSingletonText("store_clean_stamp", "last_clean_at_utc");

    /// <inheritdoc />
    public void SetLastCleanAtUtc(string lastCleanAtUtc)
    {
        ArgumentNullException.ThrowIfNull(lastCleanAtUtc);
        string value = GuardedNonEmptyText("store_clean_stamp", "last_clean_at_utc", lastCleanAtUtc);
        SqlBatch batch = new();
        batch.Append($"INSERT OR REPLACE INTO store_clean_stamp (singleton_id, last_clean_at_utc) VALUES (1, {batch.Add(value)});\n");
        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public int? GetContentRetentionDays()
    {
        string sql = "SELECT retention_days FROM content_retention WHERE singleton_id = 1 LIMIT 1;\n";

        IReadOnlyList<int> rows = Query<int>(sql, NewParameters(), reader => reader.GetInt32(reader.GetOrdinal("retention_days")));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public void SetContentRetentionDays(int retentionDays)
    {
        RequirePositiveInt(retentionDays, "content_retention", "retention_days");
        SqlBatch batch = new();
        batch.Append(
            $"INSERT OR REPLACE INTO content_retention (singleton_id, retention_days) VALUES (1, {batch.Add(retentionDays)});\n");
        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public string? GetContentPurgeMarkUtc() =>
        ReadSingletonText("content_purge_mark", "purged_through_utc");

    /// <inheritdoc />
    public void SetContentPurgeMarkUtc(string purgedThroughUtc)
    {
        ArgumentNullException.ThrowIfNull(purgedThroughUtc);
        string value = GuardedNonEmptyText("content_purge_mark", "purged_through_utc", purgedThroughUtc);
        SqlBatch batch = new();
        batch.Append($"INSERT OR REPLACE INTO content_purge_mark (singleton_id, purged_through_utc) VALUES (1, {batch.Add(value)});\n");
        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public ProjectionStateView? GetProjectionState()
    {
        string sql = "SELECT projection_schema_version, records_marker FROM projection_state WHERE singleton_id = 1 LIMIT 1;\n";

        IReadOnlyList<ProjectionStateView> rows = Query<ProjectionStateView>(
            sql,
            NewParameters(),
            reader => new ProjectionStateView(
                reader.GetInt32(reader.GetOrdinal("projection_schema_version")),
                reader.GetInt64(reader.GetOrdinal("records_marker"))));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public void SetProjectionState(ProjectionStateView state)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequireNonNegative(state.RecordsMarker, "projection_state", "records_marker");

        SqlBatch batch = new();
        batch.Append(
            $"INSERT OR REPLACE INTO projection_state (singleton_id, projection_schema_version, records_marker) " +
            $"VALUES (1, {batch.Add(state.ProjectionSchemaVersion)}, {batch.Add(state.RecordsMarker)});\n");
        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public DirtyProjectionState? GetProjectionDirtyState()
    {
        string sql =
            "SELECT 'RNG' AS tag, 0 AS sort_ord, from_seq AS value_a, to_seq AS value_b FROM projection_dirty_range WHERE singleton_id = 1 " +
            "UNION ALL " +
            "SELECT 'SES', rowid, session_id, '' FROM projection_dirty_session " +
            "ORDER BY 1 ASC, 2 ASC;\n";

        IReadOnlyList<DirtyProjectionRow> rows = Query<DirtyProjectionRow>(
            sql,
            NewParameters(),
            reader =>
            {
                string tag = reader.GetString(reader.GetOrdinal("tag"));
                return tag switch
                {
                    "RNG" => new DirtyProjectionRow(
                        tag,
                        reader.GetInt64(reader.GetOrdinal("value_a")),
                        reader.GetInt64(reader.GetOrdinal("value_b")),
                        string.Empty),
                    "SES" => new DirtyProjectionRow(tag, 0L, 0L, reader.GetString(reader.GetOrdinal("value_a"))),
                    _ => throw new StoredDataException($"Unexpected tag '{tag}' in projection_dirty query."),
                };
            });

        long? fromSeq = null;
        long? toSeq = null;
        List<string> keys = [];

        foreach (DirtyProjectionRow row in rows)
        {
            if (string.Equals(row.Tag, "RNG", StringComparison.Ordinal))
            {
                fromSeq = row.FromSeq;
                toSeq = row.ToSeq;
            }
            else
            {
                keys.Add(row.SessionId);
            }
        }

        if (!fromSeq.HasValue || !toSeq.HasValue)
        {
            return null;
        }

        bool isWildcard = keys.Count == 0;
        return new DirtyProjectionState(
            fromSeq.Value,
            toSeq.Value,
            isWildcard ? null : keys,
            isWildcard);
    }

    /// <inheritdoc />
    public void SetProjectionDirtyState(DirtyProjectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequireNonNegative(state.FromSeq, "projection_dirty_range", "from_seq");
        RequireNonNegative(state.ToSeq, "projection_dirty_range", "to_seq");

        SqlBatch batch = new();
        batch.Append(
            $"INSERT OR REPLACE INTO projection_dirty_range (singleton_id, from_seq, to_seq) VALUES (1, {batch.Add(state.FromSeq)}, {batch.Add(state.ToSeq)});\n");
        batch.Append("DELETE FROM projection_dirty_session;\n");

        if (!state.IsWildcard && state.Keys is not null)
        {
            HashSet<string> seenKeys = new(StringComparer.Ordinal);
            foreach (string key in state.Keys)
            {
                string keyValue = GuardedNonEmptyText("projection_dirty_session", "session_id", key);
                if (!seenKeys.Add(key))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Duplicate session_id '{key}' in projection_dirty_session.");
                }

                batch.Append($"INSERT INTO projection_dirty_session (session_id) VALUES ({batch.Add(keyValue)});\n");
            }
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public void ClearProjectionDirtyState()
    {
        ExecuteTransaction("DELETE FROM projection_dirty_range;\nDELETE FROM projection_dirty_session;\n");
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionKeyAlias> ListSessionKeyAliases(string? sessionId = null)
    {
        StringBuilder sql = new("SELECT alias_key, session_id FROM session_key_alias");
        Dictionary<string, object?> parameters = NewParameters();
        if (sessionId is not null)
        {
            sql.Append(" WHERE session_id = ").Append(AddParameter(parameters, sessionId));
        }

        sql.Append(" ORDER BY rowid ASC;\n");

        return Query<SessionKeyAlias>(
            sql.ToString(),
            parameters,
            reader => new SessionKeyAlias(
                reader.GetString(reader.GetOrdinal("alias_key")),
                reader.GetString(reader.GetOrdinal("session_id"))));
    }

    /// <inheritdoc />
    public void ReplaceSessionKeyAliases(IEnumerable<SessionKeyAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        SqlBatch batch = new();
        batch.Append("DELETE FROM session_key_alias;\n");

        HashSet<string> seenAliases = new(StringComparer.Ordinal);
        foreach (SessionKeyAlias alias in aliases)
        {
            ArgumentNullException.ThrowIfNull(alias);
            string aliasValue = GuardedNonEmptyText("session_key_alias", "alias_key", alias.AliasKey);
            string sessionValue = GuardedNonEmptyText("session_key_alias", "session_id", alias.SessionId);
            if (!seenAliases.Add(alias.AliasKey))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate alias_key '{alias.AliasKey}' in session_key_alias.");
            }

            batch.Append(
                $"INSERT INTO session_key_alias (alias_key, session_id) VALUES ({batch.Add(aliasValue)}, {batch.Add(sessionValue)});\n");
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public void UpsertRefreshRun(RefreshRunRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        string idValue = GuardedNonEmptyText("refresh_run", "refresh_run_id", row.Id);
        string startedValue = GuardedNonEmptyText("refresh_run", "started_at_utc", row.StartedAt);
        string statusValue = GuardedNonEmptyText("refresh_run", "status", row.Status);
        string triggerValue = GuardedNonEmptyText("refresh_run", "trigger", row.Trigger);

        if (row.HistoryWeeks.HasValue)
        {
            RequirePositiveInt(row.HistoryWeeks.Value, "refresh_run_history_window", "history_weeks");
        }

        SqlBatch batch = new();
        batch.Append($"DELETE FROM refresh_run WHERE refresh_run_id = {batch.Add(idValue)};\n");
        batch.Append(
            $"INSERT INTO refresh_run (refresh_run_id, started_at_utc, status, pid, trigger) " +
            $"VALUES ({batch.Add(idValue)}, {batch.Add(startedValue)}, {batch.Add(statusValue)}, {batch.Add(row.Pid)}, {batch.Add(triggerValue)});\n");

        AppendOptionalNonEmptyTextChild(batch, "refresh_run_completion", "refresh_run_id", idValue, "completed_at_utc", row.CompletedAt);
        AppendOptionalNonEmptyTextChild(batch, "refresh_run_summary", "refresh_run_id", idValue, "summary", row.Summary);
        if (row.HistoryWeeks.HasValue)
        {
            batch.Append(
                $"INSERT INTO refresh_run_history_window (refresh_run_id, history_weeks) VALUES ({batch.Add(idValue)}, {batch.Add(row.HistoryWeeks.Value)});\n");
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public RefreshRunRow? GetRefreshRun(string refreshRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshRunId);
        Dictionary<string, object?> parameters = NewParameters();
        string filter = $"rr.refresh_run_id = {AddParameter(parameters, refreshRunId)}";
        IReadOnlyList<RefreshRunRow> rows = QueryRefreshRuns(filter, parameters, "ORDER BY rr.rowid ASC");
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        Dictionary<string, object?> parameters = NewParameters();
        string orderAndLimit = "ORDER BY rr.started_at_utc DESC, rr.rowid ASC LIMIT " + AddParameter(parameters, limit);
        return QueryRefreshRuns(filterPredicate: null, parameters, orderAndLimit);
    }

    /// <inheritdoc />
    public SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        Dictionary<string, object?> parameters = NewParameters();
        string filter =
            $"sc.harness_id = {AddParameter(parameters, harnessId)} AND sc.source_key = {AddParameter(parameters, sourceKey)}";
        IReadOnlyList<SourceCheckpoint> rows = QuerySourceCheckpoints(filter, parameters);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public void UpsertSourceCheckpoint(SourceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        string harnessValue = GuardedNonEmptyText("source_checkpoint", "harness_id", checkpoint.HarnessId);
        string sourceKeyValue = GuardedNonEmptyText("source_checkpoint", "source_key", checkpoint.SourceKey);
        string providerValue = GuardedNonEmptyText("source_checkpoint", "provider_id", checkpoint.ProviderId);
        string parserValue = GuardedNonEmptyText("source_checkpoint", "parser_id", checkpoint.ParserId);
        string contractValue = GuardedNonEmptyText("source_checkpoint", "parser_contract_version", checkpoint.ParserContractVersion);
        string formatValue = GuardedNonEmptyText("source_checkpoint", "format", checkpoint.Format);
        string rootLabelValue = GuardedTextValue("source_checkpoint", "source_root_label", checkpoint.SourceRootLabel);
        string revisionValue = GuardedTextValue("source_checkpoint", "revision_token", checkpoint.RevisionToken);
        string coveredFromValue = GuardedNonEmptyText("source_checkpoint", "covered_from_utc", checkpoint.CoveredFromUtc);
        string coveredThroughValue = GuardedNonEmptyText("source_checkpoint", "covered_through_utc", checkpoint.CoveredThroughUtc);
        string lastAttemptValue = GuardedNonEmptyText("source_checkpoint", "last_attempt_utc", checkpoint.LastAttemptUtc);
        string lastStatusValue = GuardedNonEmptyText("source_checkpoint", "last_status", checkpoint.LastStatus);

        RequireNonNegative(checkpoint.UnitCount, "source_checkpoint", "unit_count");
        RequireNonNegative(checkpoint.RecordCount, "source_checkpoint", "record_count");

        string? successValue = checkpoint.LastSuccessUtc is null
            ? null
            : GuardedNonEmptyText("source_checkpoint_success", "last_success_utc", checkpoint.LastSuccessUtc);
        string? errorValue = checkpoint.LastErrorCode is null
            ? null
            : GuardedNonEmptyText("source_checkpoint_error", "last_error_code", checkpoint.LastErrorCode);

        SqlBatch batch = new();
        batch.Append(
            $"DELETE FROM source_checkpoint WHERE harness_id = {batch.Add(harnessValue)} AND source_key = {batch.Add(sourceKeyValue)};\n");
        batch.Append(
            "INSERT INTO source_checkpoint (harness_id, source_key, provider_id, parser_id, parser_contract_version, format, " +
            "source_root_label, revision_token, covered_from_utc, covered_through_utc, last_attempt_utc, last_status, unit_count, record_count) " +
            $"VALUES ({batch.Add(harnessValue)}, {batch.Add(sourceKeyValue)}, {batch.Add(providerValue)}, {batch.Add(parserValue)}, " +
            $"{batch.Add(contractValue)}, {batch.Add(formatValue)}, {batch.Add(rootLabelValue)}, {batch.Add(revisionValue)}, " +
            $"{batch.Add(coveredFromValue)}, {batch.Add(coveredThroughValue)}, {batch.Add(lastAttemptValue)}, {batch.Add(lastStatusValue)}, " +
            $"{batch.Add(checkpoint.UnitCount)}, {batch.Add(checkpoint.RecordCount)});\n");

        if (successValue is not null)
        {
            batch.Append(
                $"INSERT INTO source_checkpoint_success (harness_id, source_key, last_success_utc) VALUES ({batch.Add(harnessValue)}, {batch.Add(sourceKeyValue)}, {batch.Add(successValue)});\n");
        }

        if (errorValue is not null)
        {
            batch.Append(
                $"INSERT INTO source_checkpoint_error (harness_id, source_key, last_error_code) VALUES ({batch.Add(harnessValue)}, {batch.Add(sourceKeyValue)}, {batch.Add(errorValue)});\n");
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null)
    {
        Dictionary<string, object?> parameters = NewParameters();
        string? filter = harnessId is null ? null : $"sc.harness_id = {AddParameter(parameters, harnessId)}";
        return QuerySourceCheckpoints(filter, parameters);
    }

    /// <inheritdoc />
    public StoredIngestProblem? GetIngestProblem(long problemId)
    {
        Dictionary<string, object?> parameters = NewParameters();
        string filter = $"ip.problem_id = {AddParameter(parameters, problemId)}";
        IReadOnlyList<StoredIngestProblem> rows = QueryIngestProblems(filter, parameters, limit: null, offset: null);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredIngestProblem> ListIngestProblems(
        string? harnessId = null,
        string? spanId = null,
        int? limit = null,
        int? offset = null)
    {
        if (limit.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit.Value);
        }

        if (offset.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset.Value);
        }

        Dictionary<string, object?> parameters = NewParameters();
        List<string> predicates = [];
        if (harnessId is not null)
        {
            predicates.Add($"iph.harness_id = {AddParameter(parameters, harnessId)}");
        }

        if (spanId is not null)
        {
            predicates.Add($"ips.span_id = {AddParameter(parameters, spanId)}");
        }

        string? where = predicates.Count > 0 ? string.Join(" AND ", predicates) : null;
        return QueryIngestProblems(where, parameters, limit, offset);
    }

    /// <inheritdoc />
    public int CountIngestProblems(string? harnessId = null)
    {
        Dictionary<string, object?> parameters = NewParameters();
        string sql = harnessId is null
            ? "SELECT COUNT(*) AS problem_count FROM ingest_problem;\n"
            : $"SELECT COUNT(*) AS problem_count FROM ingest_problem_harness WHERE harness_id = {AddParameter(parameters, harnessId)};\n";

        return Query<int>(sql, parameters, reader => reader.GetInt32(reader.GetOrdinal("problem_count")))[0];
    }

    /// <inheritdoc />
    public void PutQuarantinedSpan(StoredQuarantinedSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(span.ObservedNamespaces);

        string spanIdValue = GuardedNonEmptyText("quarantined_span", "span_id", span.SpanId);
        string reasonValue = GuardedNonEmptyText("quarantined_span", "reason", span.Reason);

        List<string> namespaces = new(span.ObservedNamespaces.Count);
        HashSet<string> seenNamespaces = new(StringComparer.Ordinal);
        foreach (string ns in span.ObservedNamespaces)
        {
            string namespaceValue = GuardedNonEmptyText("quarantined_span_namespace", "namespace_name", ns);
            if (!seenNamespaces.Add(ns))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate namespace_name '{ns}' for quarantined_span '{span.SpanId}'.");
            }

            namespaces.Add(namespaceValue);
        }

        SqlBatch batch = new();
        batch.Append($"DELETE FROM quarantined_span WHERE span_id = {batch.Add(spanIdValue)};\n");
        batch.Append($"INSERT INTO quarantined_span (span_id, reason) VALUES ({batch.Add(spanIdValue)}, {batch.Add(reasonValue)});\n");

        foreach (string namespaceValue in namespaces)
        {
            batch.Append(
                $"INSERT INTO quarantined_span_namespace (span_id, namespace_name) VALUES ({batch.Add(spanIdValue)}, {batch.Add(namespaceValue)});\n");
        }

        AppendOptionalNonEmptyTextChild(batch, "quarantined_span_source", "span_id", spanIdValue, "source", span.Source);
        AppendOptionalNonEmptyTextChild(batch, "quarantined_span_name", "span_id", spanIdValue, "name", span.Name);
        AppendOptionalNonEmptyTextChild(batch, "quarantined_span_timestamp", "span_id", spanIdValue, "timestamp_utc", span.TimestampUtc);
        AppendOptionalNonEmptyTextChild(batch, "quarantined_span_harness", "span_id", spanIdValue, "harness_id", span.HarnessId);

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public StoredQuarantinedSpan? GetQuarantinedSpan(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);
        Dictionary<string, object?> parameters = NewParameters();
        string filter = $"qs.span_id = {AddParameter(parameters, spanId)}";
        IReadOnlyList<StoredQuarantinedSpan> rows = QueryQuarantinedSpans(filter, parameters, limit: null, offset: null);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredQuarantinedSpan> ListQuarantinedSpans(
        string? harnessId = null,
        int? limit = null,
        int? offset = null)
    {
        if (limit.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit.Value);
        }

        if (offset.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset.Value);
        }

        Dictionary<string, object?> parameters = NewParameters();
        string? filter = harnessId is null ? null : $"qh.harness_id = {AddParameter(parameters, harnessId)}";
        return QueryQuarantinedSpans(filter, parameters, limit, offset);
    }

    /// <inheritdoc />
    public int CountQuarantinedSpans(string? harnessId = null)
    {
        Dictionary<string, object?> parameters = NewParameters();
        string sql = harnessId is null
            ? "SELECT COUNT(*) AS span_count FROM quarantined_span;\n"
            : $"SELECT COUNT(*) AS span_count FROM quarantined_span_harness WHERE harness_id = {AddParameter(parameters, harnessId)};\n";

        return Query<int>(sql, parameters, reader => reader.GetInt32(reader.GetOrdinal("span_count")))[0];
    }

    /// <inheritdoc />
    public long? GetCachedTokenCount(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        Dictionary<string, object?> parameters = NewParameters();
        string sql = $"SELECT token_count FROM token_cache WHERE hash = {AddParameter(parameters, hash)} LIMIT 1;\n";

        IReadOnlyList<long> rows = Query<long>(sql, parameters, reader => reader.GetInt64(reader.GetOrdinal("token_count")));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public void PutCachedTokenCounts(IEnumerable<StoredTokenCacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        SqlBatch batch = new();
        foreach (StoredTokenCacheEntry entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            string hashValue = GuardedNonEmptyText("token_cache", "hash", entry.Hash);
            RequireNonNegative(entry.TokenCount, "token_cache", "token_count");
            string? modelValue = entry.ModelId is null
                ? null
                : GuardedNonEmptyText("token_cache_model", "model_id", entry.ModelId);

            batch.Append($"DELETE FROM token_cache WHERE hash = {batch.Add(hashValue)};\n");
            batch.Append(
                $"INSERT INTO token_cache (hash, token_count) VALUES ({batch.Add(hashValue)}, {batch.Add(entry.TokenCount)});\n");
            if (modelValue is not null)
            {
                batch.Append(
                    $"INSERT INTO token_cache_model (hash, model_id) VALUES ({batch.Add(hashValue)}, {batch.Add(modelValue)});\n");
            }
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public StoredModelCatalogSnapshot GetModelContextWindowCatalog()
    {
        string status = "empty";
        IReadOnlyList<string> statusRows = Query<string>(
            "SELECT status FROM context_window_catalog_state WHERE singleton_id = 1;\n",
            NewParameters(),
            reader => reader.GetString(reader.GetOrdinal("status")));
        if (statusRows.Count > 0)
        {
            status = statusRows[0];
        }

        string vendorSql =
            "SELECT v.vendor, v.status, vr.last_refresh_at_utc, ve.last_error " +
            "FROM model_context_window_catalog_vendor v " +
            "LEFT JOIN model_context_window_catalog_vendor_refresh vr ON vr.vendor = v.vendor " +
            "LEFT JOIN model_context_window_catalog_vendor_error ve ON ve.vendor = v.vendor " +
            "ORDER BY v.rowid ASC;\n";
        IReadOnlyList<StoredModelCatalogVendorStatus> vendors = Query<StoredModelCatalogVendorStatus>(
            vendorSql,
            NewParameters(),
            reader => new StoredModelCatalogVendorStatus(
                reader.GetString(reader.GetOrdinal("vendor")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.IsDBNull(reader.GetOrdinal("last_refresh_at_utc")) ? null : reader.GetString(reader.GetOrdinal("last_refresh_at_utc")),
                reader.IsDBNull(reader.GetOrdinal("last_error")) ? null : reader.GetString(reader.GetOrdinal("last_error"))));

        string entrySql =
            "SELECT e.lookup_id, e.canonical_model_id, e.context_window, e.vendor, e.documentation_url, " +
            "e.retrieved_at_utc, r.source_revision " +
            "FROM model_context_window_catalog e " +
            "LEFT JOIN model_context_window_source_revision r ON r.lookup_id = e.lookup_id " +
            "ORDER BY e.rowid ASC;\n";
        IReadOnlyList<StoredModelContextWindowEntry> entries = Query<StoredModelContextWindowEntry>(
            entrySql,
            NewParameters(),
            reader => new StoredModelContextWindowEntry(
                reader.GetString(reader.GetOrdinal("lookup_id")),
                reader.GetString(reader.GetOrdinal("canonical_model_id")),
                reader.GetInt32(reader.GetOrdinal("context_window")),
                reader.GetString(reader.GetOrdinal("vendor")),
                new Uri(reader.GetString(reader.GetOrdinal("documentation_url")), UriKind.RelativeOrAbsolute),
                reader.GetString(reader.GetOrdinal("retrieved_at_utc")),
                reader.IsDBNull(reader.GetOrdinal("source_revision")) ? null : reader.GetString(reader.GetOrdinal("source_revision"))));

        return new StoredModelCatalogSnapshot(status, vendors, entries);
    }

    /// <inheritdoc />
    public void ReplaceModelContextWindowCatalog(StoredModelCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Vendors);
        ArgumentNullException.ThrowIfNull(snapshot.Entries);

        string statusValue = GuardedNonEmptyText("context_window_catalog_state", "status", snapshot.Status);

        SqlBatch batch = new();
        batch.Append(
            $"INSERT OR REPLACE INTO context_window_catalog_state (singleton_id, status) VALUES (1, {batch.Add(statusValue)});\n");
        batch.Append("DELETE FROM model_context_window_catalog_vendor;\n");

        HashSet<string> seenVendors = new(StringComparer.Ordinal);
        foreach (StoredModelCatalogVendorStatus vendor in snapshot.Vendors)
        {
            ArgumentNullException.ThrowIfNull(vendor);
            string vendorValue = GuardedNonEmptyText("model_context_window_catalog_vendor", "vendor", vendor.Vendor);
            string vendorStatusValue = GuardedNonEmptyText("model_context_window_catalog_vendor", "status", vendor.Status);
            if (!seenVendors.Add(vendor.Vendor))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate vendor '{vendor.Vendor}' in model_context_window_catalog_vendor.");
            }

            batch.Append(
                $"INSERT INTO model_context_window_catalog_vendor (vendor, status) VALUES ({batch.Add(vendorValue)}, {batch.Add(vendorStatusValue)});\n");
            AppendOptionalNonEmptyTextChild(
                batch,
                "model_context_window_catalog_vendor_refresh",
                "vendor",
                vendorValue,
                "last_refresh_at_utc",
                vendor.LastRefreshAtUtc);
            AppendOptionalNonEmptyTextChild(
                batch,
                "model_context_window_catalog_vendor_error",
                "vendor",
                vendorValue,
                "last_error",
                vendor.LastError);
        }

        batch.Append("DELETE FROM model_context_window_catalog;\n");

        HashSet<string> seenLookupIds = new(StringComparer.Ordinal);
        foreach (StoredModelContextWindowEntry entry in snapshot.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            string lookupIdValue = GuardedNonEmptyText("model_context_window_catalog", "lookup_id", entry.LookupId);
            string canonicalValue = GuardedNonEmptyText("model_context_window_catalog", "canonical_model_id", entry.CanonicalModelId);
            RequirePositiveInt(entry.ContextWindow, "model_context_window_catalog", "context_window");
            string entryVendorValue = GuardedNonEmptyText("model_context_window_catalog", "vendor", entry.Vendor);
            string docUrlValue = GuardedNonEmptyText(
                "model_context_window_catalog",
                "documentation_url",
                entry.DocumentationUrl?.ToString());
            string retrievedValue = GuardedNonEmptyText("model_context_window_catalog", "retrieved_at_utc", entry.RetrievedAtUtc);

            if (!seenLookupIds.Add(entry.LookupId))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate lookup_id '{entry.LookupId}' in model_context_window_catalog.");
            }

            batch.Append(
                "INSERT INTO model_context_window_catalog (lookup_id, canonical_model_id, context_window, vendor, documentation_url, retrieved_at_utc) " +
                $"VALUES ({batch.Add(lookupIdValue)}, {batch.Add(canonicalValue)}, {batch.Add(entry.ContextWindow)}, " +
                $"{batch.Add(entryVendorValue)}, {batch.Add(docUrlValue)}, {batch.Add(retrievedValue)});\n");
            AppendOptionalNonEmptyTextChild(
                batch,
                "model_context_window_source_revision",
                "lookup_id",
                lookupIdValue,
                "source_revision",
                entry.SourceRevision);
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public void PutMetricPoints(IEnumerable<StoredMetricPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        SqlBatch batch = new();
        foreach (StoredMetricPoint point in points)
        {
            ArgumentNullException.ThrowIfNull(point);

            string pointKeyValue = GuardedNonEmptyText("metric_point", "point_key", point.PointKey);
            string nameValue = GuardedNonEmptyText("metric_point", "name", point.Name);
            string kindValue = GuardedNonEmptyText("metric_point", "kind", point.Kind);

            string? unitValue = point.Unit is null ? null : GuardedNonEmptyText("metric_point_unit", "unit", point.Unit);
            string? timeValue = point.TimeUnixNano is null ? null : GuardedNonEmptyText("metric_point_time", "time_unix_nano", point.TimeUnixNano);
            string? startValue = point.StartTimeUnixNano is null ? null : GuardedNonEmptyText("metric_point_start_time", "start_time_unix_nano", point.StartTimeUnixNano);
            string? timestampValue = point.TimestampUtc is null ? null : GuardedNonEmptyText("metric_point_timestamp", "timestamp_utc", point.TimestampUtc);
            double? value = point.Value.HasValue ? RequireFiniteReal("metric_point_value", "value", point.Value.Value) : null;
            double? sum = point.PointSum.HasValue ? RequireFiniteReal("metric_point_sum", "point_sum", point.PointSum.Value) : null;
            string? sourceValue = point.Source is null ? null : GuardedNonEmptyText("metric_point_source", "source", point.Source);

            batch.Append(
                $"INSERT INTO metric_point (point_key, name, kind) VALUES ({batch.Add(pointKeyValue)}, {batch.Add(nameValue)}, {batch.Add(kindValue)}) ");
            batch.Append("ON CONFLICT(point_key) DO UPDATE SET name = excluded.name, kind = excluded.kind;\n");

            foreach (string childTable in MetricPointChildTables)
            {
                batch.Append(
                    $"DELETE FROM {childTable} WHERE point_id = (SELECT point_id FROM metric_point WHERE point_key = {batch.Add(pointKeyValue)});\n");
            }

            AppendMetricPointOptionalChild(batch, pointKeyValue, "metric_point_unit", "unit", unitValue);
            if (point.AggregationTemporality.HasValue)
            {
                AppendMetricPointOptionalChild(
                    batch,
                    pointKeyValue,
                    "metric_point_temporality",
                    "aggregation_temporality",
                    point.AggregationTemporality.Value);
            }

            AppendMetricPointOptionalChild(batch, pointKeyValue, "metric_point_time", "time_unix_nano", timeValue);
            AppendMetricPointOptionalChild(batch, pointKeyValue, "metric_point_start_time", "start_time_unix_nano", startValue);
            AppendMetricPointOptionalChild(batch, pointKeyValue, "metric_point_timestamp", "timestamp_utc", timestampValue);
            AppendMetricPointOptionalChild(batch, pointKeyValue, "metric_point_value", "value", value);
            if (point.PointCount.HasValue)
            {
                AppendMetricPointOptionalChild(
                    batch,
                    pointKeyValue,
                    "metric_point_count",
                    "point_count",
                    point.PointCount.Value);
            }

            AppendMetricPointOptionalChild(batch, pointKeyValue, "metric_point_sum", "point_sum", sum);
            AppendMetricPointOptionalChild(batch, pointKeyValue, "metric_point_source", "source", sourceValue);
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredMetricPoint> ListMetricPoints(string? name = null, int? limit = null)
    {
        if (limit.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit.Value);
        }

        Dictionary<string, object?> parameters = NewParameters();
        StringBuilder sql = new();
        sql.Append(
            "SELECT mp.point_key, mp.name, mp.kind, mpu.unit, mpt.aggregation_temporality, " +
            "mpti.time_unix_nano, mpst.start_time_unix_nano, mpts.timestamp_utc, mpv.value, " +
            "mpc.point_count, mpsu.point_sum, mpso.source " +
            "FROM metric_point mp " +
            "LEFT JOIN metric_point_unit mpu ON mpu.point_id = mp.point_id " +
            "LEFT JOIN metric_point_temporality mpt ON mpt.point_id = mp.point_id " +
            "LEFT JOIN metric_point_time mpti ON mpti.point_id = mp.point_id " +
            "LEFT JOIN metric_point_start_time mpst ON mpst.point_id = mp.point_id " +
            "LEFT JOIN metric_point_timestamp mpts ON mpts.point_id = mp.point_id " +
            "LEFT JOIN metric_point_value mpv ON mpv.point_id = mp.point_id " +
            "LEFT JOIN metric_point_count mpc ON mpc.point_id = mp.point_id " +
            "LEFT JOIN metric_point_sum mpsu ON mpsu.point_id = mp.point_id " +
            "LEFT JOIN metric_point_source mpso ON mpso.point_id = mp.point_id");

        if (name is not null)
        {
            sql.Append(" WHERE mp.name = ").Append(AddParameter(parameters, name));
        }

        sql.Append(" ORDER BY mp.point_id ASC");
        AppendLimitOffset(sql, parameters, limit, offset: null);
        sql.Append(";\n");

        return Query<StoredMetricPoint>(
            sql.ToString(),
            parameters,
            reader => new StoredMetricPoint(
                reader.GetString(reader.GetOrdinal("point_key")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("kind")),
                reader.IsDBNull(reader.GetOrdinal("unit")) ? null : reader.GetString(reader.GetOrdinal("unit")),
                reader.IsDBNull(reader.GetOrdinal("aggregation_temporality")) ? null : reader.GetInt32(reader.GetOrdinal("aggregation_temporality")),
                reader.IsDBNull(reader.GetOrdinal("time_unix_nano")) ? null : reader.GetString(reader.GetOrdinal("time_unix_nano")),
                reader.IsDBNull(reader.GetOrdinal("start_time_unix_nano")) ? null : reader.GetString(reader.GetOrdinal("start_time_unix_nano")),
                reader.IsDBNull(reader.GetOrdinal("timestamp_utc")) ? null : reader.GetString(reader.GetOrdinal("timestamp_utc")),
                reader.IsDBNull(reader.GetOrdinal("value")) ? null : reader.GetDouble(reader.GetOrdinal("value")),
                reader.IsDBNull(reader.GetOrdinal("point_count")) ? null : reader.GetInt64(reader.GetOrdinal("point_count")),
                reader.IsDBNull(reader.GetOrdinal("point_sum")) ? null : reader.GetDouble(reader.GetOrdinal("point_sum")),
                reader.IsDBNull(reader.GetOrdinal("source")) ? null : reader.GetString(reader.GetOrdinal("source"))));
    }

    /// <inheritdoc />
    public void RecordIngest(string source, int count, string timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timestampUtc);
        string sourceValue = GuardedNonEmptyText("ingest_log", "source", source);
        RequireNonNegative(count, "ingest_log", "record_count");
        string timestampValue = GuardedNonEmptyText("ingest_log", "timestamp_utc", timestampUtc);

        SqlBatch batch = new();
        batch.Append(
            $"INSERT INTO ingest_log (source, record_count, timestamp_utc) VALUES ({batch.Add(sourceValue)}, {batch.Add(count)}, {batch.Add(timestampValue)});\n");
        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public IReadOnlyList<IngestLogEntry> ListIngestLog(string? source = null)
    {
        Dictionary<string, object?> parameters = NewParameters();
        StringBuilder sql = new("SELECT ingest_id, source, record_count, timestamp_utc FROM ingest_log");
        if (source is not null)
        {
            sql.Append(" WHERE source = ").Append(AddParameter(parameters, source));
        }

        sql.Append(" ORDER BY ingest_id ASC;\n");

        return Query<IngestLogEntry>(
            sql.ToString(),
            parameters,
            reader => new IngestLogEntry(
                reader.GetInt32(reader.GetOrdinal("ingest_id")),
                reader.GetString(reader.GetOrdinal("source")),
                reader.GetInt32(reader.GetOrdinal("record_count")),
                reader.GetString(reader.GetOrdinal("timestamp_utc"))));
    }

    /// <inheritdoc />
    public void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        SqlBatch batch = new();
        foreach (RecordProvenanceRow row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);

            string spanIdValue = GuardedNonEmptyText("record_provenance", "span_id", row.SpanId);
            string harnessValue = GuardedNonEmptyText("record_provenance", "harness_id", row.HarnessId);
            string sourceKeyValue = GuardedNonEmptyText("record_provenance", "source_key", row.SourceKey);
            string sourceRevValue = GuardedTextValue("record_provenance", "source_revision", row.SourceRevision);
            string parserVerValue = GuardedTextValue("record_provenance", "parser_version", row.ParserVersion);
            string importedValue = GuardedNonEmptyText("record_provenance", "imported_at_utc", row.ImportedAtUtc);
            string locationValue = GuardedTextValue("record_provenance", "location_token", row.LocationToken);

            batch.Append($"DELETE FROM record_provenance WHERE span_id = {batch.Add(spanIdValue)};\n");
            batch.Append(
                "INSERT INTO record_provenance (span_id, harness_id, source_key, source_revision, parser_version, imported_at_utc, location_token) " +
                $"VALUES ({batch.Add(spanIdValue)}, {batch.Add(harnessValue)}, {batch.Add(sourceKeyValue)}, " +
                $"{batch.Add(sourceRevValue)}, {batch.Add(parserVerValue)}, {batch.Add(importedValue)}, {batch.Add(locationValue)});\n");

            AppendOptionalNonEmptyTextChild(
                batch,
                "record_provenance_native_session",
                "span_id",
                spanIdValue,
                "native_session_id",
                row.NativeSessionId);
            AppendOptionalNonEmptyTextChild(
                batch,
                "record_provenance_native_record",
                "span_id",
                spanIdValue,
                "native_record_id",
                row.NativeRecordId);
        }

        ExecuteTransaction(batch.ToStatements());
    }

    /// <inheritdoc />
    public IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        Dictionary<string, object?> parameters = NewParameters();
        string sql =
            "SELECT rp.span_id, rp.harness_id, rp.source_key, rpns.native_session_id, rpnr.native_record_id, " +
            "rp.source_revision, rp.parser_version, rp.imported_at_utc, rp.location_token " +
            "FROM record_provenance rp " +
            "LEFT JOIN record_provenance_native_session rpns ON rpns.span_id = rp.span_id " +
            "LEFT JOIN record_provenance_native_record rpnr ON rpnr.span_id = rp.span_id " +
            $"WHERE rp.harness_id = {AddParameter(parameters, harnessId)} AND rp.source_key = {AddParameter(parameters, sourceKey)} " +
            "ORDER BY rp.rowid ASC;\n";

        return Query<RecordProvenanceRow>(
            sql,
            parameters,
            reader => new RecordProvenanceRow(
                reader.GetString(reader.GetOrdinal("span_id")),
                reader.GetString(reader.GetOrdinal("harness_id")),
                reader.GetString(reader.GetOrdinal("source_key")),
                reader.IsDBNull(reader.GetOrdinal("native_session_id")) ? null : reader.GetString(reader.GetOrdinal("native_session_id")),
                reader.IsDBNull(reader.GetOrdinal("native_record_id")) ? null : reader.GetString(reader.GetOrdinal("native_record_id")),
                reader.GetString(reader.GetOrdinal("source_revision")),
                reader.GetString(reader.GetOrdinal("parser_version")),
                reader.GetString(reader.GetOrdinal("imported_at_utc")),
                reader.GetString(reader.GetOrdinal("location_token"))));
    }

    /// <summary>
    /// Compacts the store file: checkpoints the write-ahead log with <c>TRUNCATE</c> and then
    /// rebuilds the database with <c>VACUUM</c>, reclaiming the pages freed by deleted records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>VACUUM</c> refuses to run inside a transaction, so each statement runs through the
    /// plain query helper rather than through the transactional write helper.
    /// </para>
    /// <para>
    /// The checkpoint after <c>VACUUM</c> is not redundant. In WAL mode the rebuilt pages are
    /// written through the write-ahead log, so the database file keeps its previous length until a
    /// checkpoint copies them back and truncates it to the compacted size — the length an operator
    /// reclaims disk from only after that second checkpoint.
    /// </para>
    /// </remarks>
    public void Vacuum()
    {
        EnsureInitialized();

        lock (writeGate)
        {
            Query<long>(
                "PRAGMA wal_checkpoint(TRUNCATE);\n",
                NewParameters(),
                reader => reader.GetInt64(reader.GetOrdinal("checkpointed")));
            Query<long>("VACUUM;\n", NewParameters(), _ => 0L);
            Query<long>(
                "PRAGMA wal_checkpoint(TRUNCATE);\n",
                NewParameters(),
                reader => reader.GetInt64(reader.GetOrdinal("checkpointed")));
        }
    }

    private static string CellText(SqliteDataReader reader, string columnName) =>
        Convert.ToString(reader.GetValue(reader.GetOrdinal(columnName)), CultureInfo.InvariantCulture)
        ?? string.Empty;

    private string? ReadSingletonText(string tableName, string columnName)
    {
        string sql = $"SELECT {columnName} FROM {tableName} WHERE singleton_id = 1 LIMIT 1;\n";

        IReadOnlyList<string> rows = Query<string>(
            sql,
            NewParameters(),
            reader => reader.GetString(reader.GetOrdinal(columnName)));
        return rows.Count == 0 ? null : rows[0];
    }

    private IReadOnlyList<RefreshRunRow> QueryRefreshRuns(
        string? filterPredicate,
        IReadOnlyDictionary<string, object?> parameters,
        string orderAndLimit)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql =
            "SELECT rr.refresh_run_id, rr.started_at_utc, rrc.completed_at_utc, rr.status, rr.pid, rr.trigger, " +
            "rrs.summary, rrhw.history_weeks " +
            "FROM refresh_run rr " +
            "LEFT JOIN refresh_run_completion rrc ON rrc.refresh_run_id = rr.refresh_run_id " +
            "LEFT JOIN refresh_run_summary rrs ON rrs.refresh_run_id = rr.refresh_run_id " +
            $"LEFT JOIN refresh_run_history_window rrhw ON rrhw.refresh_run_id = rr.refresh_run_id{whereClause} {orderAndLimit};\n";

        return Query<RefreshRunRow>(
            sql,
            parameters,
            reader => new RefreshRunRow(
                reader.GetString(reader.GetOrdinal("refresh_run_id")),
                reader.GetString(reader.GetOrdinal("started_at_utc")),
                reader.IsDBNull(reader.GetOrdinal("completed_at_utc")) ? null : reader.GetString(reader.GetOrdinal("completed_at_utc")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetInt32(reader.GetOrdinal("pid")),
                reader.GetString(reader.GetOrdinal("trigger")),
                reader.IsDBNull(reader.GetOrdinal("summary")) ? null : reader.GetString(reader.GetOrdinal("summary")),
                reader.IsDBNull(reader.GetOrdinal("history_weeks")) ? null : reader.GetInt32(reader.GetOrdinal("history_weeks"))));
    }

    private IReadOnlyList<SourceCheckpoint> QuerySourceCheckpoints(
        string? filterPredicate,
        IReadOnlyDictionary<string, object?> parameters)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql =
            "SELECT sc.harness_id, sc.source_key, sc.provider_id, sc.parser_id, sc.parser_contract_version, " +
            "sc.format, sc.source_root_label, sc.revision_token, sc.covered_from_utc, sc.covered_through_utc, " +
            "sc.last_attempt_utc, scs.last_success_utc, sc.last_status, sce.last_error_code, " +
            "sc.unit_count, sc.record_count " +
            "FROM source_checkpoint sc " +
            "LEFT JOIN source_checkpoint_success scs ON scs.harness_id = sc.harness_id AND scs.source_key = sc.source_key " +
            $"LEFT JOIN source_checkpoint_error sce ON sce.harness_id = sc.harness_id AND sce.source_key = sc.source_key{whereClause} " +
            "ORDER BY sc.rowid ASC;\n";

        return Query<SourceCheckpoint>(
            sql,
            parameters,
            reader => new SourceCheckpoint(
                reader.GetString(reader.GetOrdinal("harness_id")),
                reader.GetString(reader.GetOrdinal("source_key")),
                reader.GetString(reader.GetOrdinal("provider_id")),
                reader.GetString(reader.GetOrdinal("parser_id")),
                reader.GetString(reader.GetOrdinal("parser_contract_version")),
                reader.GetString(reader.GetOrdinal("format")),
                reader.GetString(reader.GetOrdinal("source_root_label")),
                reader.GetString(reader.GetOrdinal("revision_token")),
                reader.GetString(reader.GetOrdinal("covered_from_utc")),
                reader.GetString(reader.GetOrdinal("covered_through_utc")),
                reader.GetString(reader.GetOrdinal("last_attempt_utc")),
                reader.IsDBNull(reader.GetOrdinal("last_success_utc")) ? null : reader.GetString(reader.GetOrdinal("last_success_utc")),
                reader.GetString(reader.GetOrdinal("last_status")),
                reader.IsDBNull(reader.GetOrdinal("last_error_code")) ? null : reader.GetString(reader.GetOrdinal("last_error_code")),
                reader.GetInt32(reader.GetOrdinal("unit_count")),
                reader.GetInt32(reader.GetOrdinal("record_count"))));
    }

    private IReadOnlyList<StoredIngestProblem> QueryIngestProblems(
        string? filterPredicate,
        Dictionary<string, object?> parameters,
        int? limit,
        int? offset)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        StringBuilder sql = new();
        sql.Append(
            "SELECT ip.problem_id, ip.problem_key, ip.severity, ip.code, ip.message, " +
            "ips.span_id, ipl.location, ipse.session_id, iph.harness_id, ipt.timestamp_utc, ipst.stack " +
            "FROM ingest_problem ip " +
            "LEFT JOIN ingest_problem_span ips ON ips.problem_id = ip.problem_id " +
            "LEFT JOIN ingest_problem_location ipl ON ipl.problem_id = ip.problem_id " +
            "LEFT JOIN ingest_problem_session ipse ON ipse.problem_id = ip.problem_id " +
            "LEFT JOIN ingest_problem_harness iph ON iph.problem_id = ip.problem_id " +
            "LEFT JOIN ingest_problem_timestamp ipt ON ipt.problem_id = ip.problem_id " +
            $"LEFT JOIN ingest_problem_stack ipst ON ipst.problem_id = ip.problem_id{whereClause} " +
            "ORDER BY ip.problem_id ASC");
        AppendLimitOffset(sql, parameters, limit, offset);
        sql.Append(";\n");

        return Query<StoredIngestProblem>(
            sql.ToString(),
            parameters,
            reader =>
            {
                CollectedProblem problem = new(
                    reader.GetString(reader.GetOrdinal("problem_key")),
                    reader.GetString(reader.GetOrdinal("severity")),
                    reader.GetString(reader.GetOrdinal("code")),
                    reader.GetString(reader.GetOrdinal("message")),
                    reader.IsDBNull(reader.GetOrdinal("span_id")) ? null : reader.GetString(reader.GetOrdinal("span_id")),
                    reader.IsDBNull(reader.GetOrdinal("location")) ? null : reader.GetString(reader.GetOrdinal("location")),
                    reader.IsDBNull(reader.GetOrdinal("session_id")) ? null : reader.GetString(reader.GetOrdinal("session_id")),
                    reader.IsDBNull(reader.GetOrdinal("harness_id")) ? null : reader.GetString(reader.GetOrdinal("harness_id")),
                    reader.IsDBNull(reader.GetOrdinal("timestamp_utc")) ? null : reader.GetString(reader.GetOrdinal("timestamp_utc")),
                    reader.IsDBNull(reader.GetOrdinal("stack")) ? null : reader.GetString(reader.GetOrdinal("stack")));
                return new StoredIngestProblem(reader.GetInt64(reader.GetOrdinal("problem_id")), problem);
            });
    }

    private IReadOnlyList<StoredQuarantinedSpan> QueryQuarantinedSpans(
        string? filterPredicate,
        Dictionary<string, object?> parameters,
        int? limit,
        int? offset)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        StringBuilder sql = new();
        sql.Append(
            "WITH selected AS (" +
            "SELECT qs.rowid AS sort_ord, qs.span_id AS span_id, qs.reason AS text, " +
            "qsrc.source AS source, qn.name AS name, qts.timestamp_utc AS timestamp_utc, qh.harness_id AS harness_id " +
            "FROM quarantined_span qs " +
            "LEFT JOIN quarantined_span_source qsrc ON qsrc.span_id = qs.span_id " +
            "LEFT JOIN quarantined_span_name qn ON qn.span_id = qs.span_id " +
            "LEFT JOIN quarantined_span_timestamp qts ON qts.span_id = qs.span_id " +
            $"LEFT JOIN quarantined_span_harness qh ON qh.span_id = qs.span_id{whereClause} " +
            "ORDER BY qs.rowid ASC");
        AppendLimitOffset(sql, parameters, limit, offset);
        sql.Append(
            ") " +
            "SELECT 'QSP' AS tag, sort_ord, span_id, text, source, name, timestamp_utc, harness_id " +
            "FROM selected " +
            "UNION ALL " +
            "SELECT 'QNS', qns.rowid, qns.span_id, qns.namespace_name, '', '', '', '' " +
            "FROM quarantined_span_namespace qns " +
            "JOIN selected s ON s.span_id = qns.span_id " +
            "ORDER BY 1 DESC, 2 ASC;\n");

        IReadOnlyList<QuarantinedSpanRow> rows = Query<QuarantinedSpanRow>(
            sql.ToString(),
            parameters,
            reader =>
            {
                string tag = reader.GetString(reader.GetOrdinal("tag"));
                return tag switch
                {
                    "QSP" => new QuarantinedSpanRow(
                        tag,
                        reader.GetString(reader.GetOrdinal("span_id")),
                        reader.GetString(reader.GetOrdinal("text")),
                        reader.IsDBNull(reader.GetOrdinal("source")) ? null : reader.GetString(reader.GetOrdinal("source")),
                        reader.IsDBNull(reader.GetOrdinal("name")) ? null : reader.GetString(reader.GetOrdinal("name")),
                        reader.IsDBNull(reader.GetOrdinal("timestamp_utc")) ? null : reader.GetString(reader.GetOrdinal("timestamp_utc")),
                        reader.IsDBNull(reader.GetOrdinal("harness_id")) ? null : reader.GetString(reader.GetOrdinal("harness_id"))),
                    "QNS" => new QuarantinedSpanRow(
                        tag,
                        reader.GetString(reader.GetOrdinal("span_id")),
                        reader.GetString(reader.GetOrdinal("text")),
                        null,
                        null,
                        null,
                        null),
                    _ => throw new StoredDataException($"Unexpected tag '{tag}' in quarantined_span query."),
                };
            });

        List<StoredQuarantinedSpan> results = [];
        Dictionary<string, List<string>> namespacesBySpan = new(StringComparer.Ordinal);

        foreach (QuarantinedSpanRow row in rows)
        {
            if (string.Equals(row.Tag, "QSP", StringComparison.Ordinal))
            {
                List<string> nsList = [];
                namespacesBySpan[row.SpanId] = nsList;
                results.Add(new StoredQuarantinedSpan(
                    row.SpanId,
                    row.Text,
                    nsList,
                    row.Source,
                    row.Name,
                    row.TimestampUtc,
                    row.HarnessId));
            }
            else if (namespacesBySpan.TryGetValue(row.SpanId, out List<string>? list))
            {
                list.Add(row.Text);
            }
        }

        return results;
    }

    private static void RequirePositiveInt(int value, string tableName, string columnName)
    {
        if (value <= 0)
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                $"INTEGER column '{tableName}.{columnName}' must be positive.");
        }
    }

    private static void RequireNonNegative<T>(T value, string tableName, string columnName)
        where T : INumberBase<T>
    {
        if (T.IsNegative(value))
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                $"INTEGER column '{tableName}.{columnName}' cannot be negative.");
        }
    }

    private static void RequireRangeInt(int value, int min, int max, string tableName, string columnName)
    {
        if (value < min || value > max)
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                $"INTEGER column '{tableName}.{columnName}' must be between {IntSql(min)} and {IntSql(max)}.");
        }
    }

    private static void AppendMetricPointOptionalChild(
        SqlBatch batch,
        string pointKeyValue,
        string tableName,
        string columnName,
        object? value)
    {
        if (value is null)
        {
            return;
        }

        string keyParam = batch.Add(pointKeyValue);
        string valueParam = batch.Add(value);
        batch.Append(
            $"INSERT INTO {tableName} (point_id, {columnName}) SELECT point_id, {valueParam} FROM metric_point WHERE point_key = {keyParam};\n");
    }

    private static void AppendLimitOffset(
        StringBuilder sql,
        Dictionary<string, object?> parameters,
        int? limit,
        int? offset)
    {
        if (limit.HasValue && offset.HasValue)
        {
            sql.Append(" LIMIT ")
                .Append(AddParameter(parameters, limit.Value))
                .Append(" OFFSET ")
                .Append(AddParameter(parameters, offset.Value));
        }
        else if (limit.HasValue)
        {
            sql.Append(" LIMIT ").Append(AddParameter(parameters, limit.Value));
        }
        else if (offset.HasValue && offset.Value > 0)
        {
            sql.Append(" LIMIT -1 OFFSET ").Append(AddParameter(parameters, offset.Value));
        }
    }

    private static Dictionary<string, object?> NewParameters() => new(StringComparer.Ordinal);

    /// <summary>Keeps the legacy guarded-text validation on the write path while returning the value to bind.</summary>
    private static string GuardedTextValue(string tableName, string columnName, string? value)
    {
        string validated = GuardedText(tableName, columnName, value);
        _ = LegacyGuardedTextValidator(tableName, columnName, validated);
        return validated;
    }

    private static string AddParameter(Dictionary<string, object?> parameters, object? value)
    {
        string name = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(name, value);
        return name;
    }

    private sealed record DirtyProjectionRow(string Tag, long FromSeq, long ToSeq, string SessionId);

    private sealed record QuarantinedSpanRow(
        string Tag,
        string SpanId,
        string Text,
        string? Source,
        string? Name,
        string? TimestampUtc,
        string? HarnessId);
}
