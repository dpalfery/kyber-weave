using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Domain;

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

    /// <inheritdoc />
    public SettingsView ReadSettings()
    {
        string sql = TabReadScriptHeader +
                     "SELECT " +
                     "COALESCE((SELECT scheduled FROM operator_setting_folder_import WHERE singleton_id = 1), 0), " +
                     "COALESCE((SELECT paused FROM operator_setting_jobs WHERE singleton_id = 1), 0), " +
                     "COALESCE((SELECT minutes FROM operator_setting_refresh_cadence WHERE singleton_id = 1), 5), " +
                     "COALESCE((SELECT hosted FROM operator_setting_receiver WHERE singleton_id = 1), 0), " +
                     "COALESCE((SELECT port FROM operator_setting_dashboard WHERE singleton_id = 1), 4747);\n";

        string output = ExecuteSqlite(sql);
        string[] cols = SplitSingleRow(output, 5, "operator_setting");
        return new SettingsView(
            FolderImportScheduled: ParseBoolFlag(cols[0], "operator_setting_folder_import.scheduled"),
            JobsPaused: ParseBoolFlag(cols[1], "operator_setting_jobs.paused"),
            RefreshCadenceMinutes: ParseInt32Required(cols[2], "operator_setting_refresh_cadence.minutes"),
            ReceiverHosted: ParseBoolFlag(cols[3], "operator_setting_receiver.hosted"),
            DashboardPort: ParseInt32Required(cols[4], "operator_setting_dashboard.port"));
    }

    /// <inheritdoc />
    public void SaveSettings(SettingsView settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RequireRangeInt(settings.RefreshCadenceMinutes, 1, 1440, "operator_setting_refresh_cadence", "minutes");
        RequireRangeInt(settings.DashboardPort, 1, 65535, "operator_setting_dashboard", "port");

        StringBuilder body = new();
        body.Append(
            CultureInfo.InvariantCulture,
            $"INSERT OR REPLACE INTO operator_setting_folder_import (singleton_id, scheduled) VALUES (1, {BoolSql(settings.FolderImportScheduled)});\n");
        body.Append(
            CultureInfo.InvariantCulture,
            $"INSERT OR REPLACE INTO operator_setting_jobs (singleton_id, paused) VALUES (1, {BoolSql(settings.JobsPaused)});\n");
        body.Append(
            CultureInfo.InvariantCulture,
            $"INSERT OR REPLACE INTO operator_setting_refresh_cadence (singleton_id, minutes) VALUES (1, {IntSql(settings.RefreshCadenceMinutes)});\n");
        body.Append(
            CultureInfo.InvariantCulture,
            $"INSERT OR REPLACE INTO operator_setting_receiver (singleton_id, hosted) VALUES (1, {BoolSql(settings.ReceiverHosted)});\n");
        body.Append(
            CultureInfo.InvariantCulture,
            $"INSERT OR REPLACE INTO operator_setting_dashboard (singleton_id, port) VALUES (1, {IntSql(settings.DashboardPort)});\n");

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public StoreStateView? GetStoreState()
    {
        string sql = TabReadScriptHeader +
                     "SELECT schema_version, detector_version FROM store_state WHERE singleton_id = 1 LIMIT 1;\n";

        string output = ExecuteSqlite(sql);
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 2, "store_state");
            return new StoreStateView(
                ParseInt32Required(cols[0], "store_state.schema_version"),
                ParseInt32Required(cols[1], "store_state.detector_version"));
        }

        return null;
    }

    /// <inheritdoc />
    public void SetStoreState(StoreStateView state)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequirePositiveInt(state.SchemaVersion, "store_state", "schema_version");
        RequirePositiveInt(state.DetectorVersion, "store_state", "detector_version");

        ExecuteTransaction(
            string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO store_state (singleton_id, schema_version, detector_version) VALUES (1, {IntSql(state.SchemaVersion)}, {IntSql(state.DetectorVersion)});\n"));
    }

    /// <inheritdoc />
    public long GetWriteMarker()
    {
        string sql = TabReadScriptHeader +
                     "SELECT records_write_seq FROM store_write_marker WHERE singleton_id = 1 LIMIT 1;\n";

        string output = ExecuteSqlite(sql);
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 1, "store_write_marker");
            return ParseInt64Required(cols[0], "store_write_marker.records_write_seq");
        }

        return 0L;
    }

    /// <inheritdoc />
    public void SetWriteMarker(long recordsWriteSeq)
    {
        RequireNonNegativeLong(recordsWriteSeq, "store_write_marker", "records_write_seq");
        ExecuteTransaction(
            string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO store_write_marker (singleton_id, records_write_seq) VALUES (1, {LongSql(recordsWriteSeq)});\n"));
    }

    /// <inheritdoc />
    public string? GetLastCleanAtUtc() =>
        ReadSingletonHexString("store_clean_stamp", "last_clean_at_utc");

    /// <inheritdoc />
    public void SetLastCleanAtUtc(string lastCleanAtUtc)
    {
        string valueExpr = GuardedNonEmptyTextExpr("store_clean_stamp", "last_clean_at_utc", lastCleanAtUtc);
        ExecuteTransaction($"INSERT OR REPLACE INTO store_clean_stamp (singleton_id, last_clean_at_utc) VALUES (1, {valueExpr});\n");
    }

    /// <inheritdoc />
    public int? GetContentRetentionDays()
    {
        string sql = TabReadScriptHeader +
                     "SELECT retention_days FROM content_retention WHERE singleton_id = 1 LIMIT 1;\n";

        string output = ExecuteSqlite(sql);
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 1, "content_retention");
            return ParseInt32Required(cols[0], "content_retention.retention_days");
        }

        return null;
    }

    /// <inheritdoc />
    public void SetContentRetentionDays(int retentionDays)
    {
        RequirePositiveInt(retentionDays, "content_retention", "retention_days");
        ExecuteTransaction(
            string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO content_retention (singleton_id, retention_days) VALUES (1, {IntSql(retentionDays)});\n"));
    }

    /// <inheritdoc />
    public string? GetContentPurgeMarkUtc() =>
        ReadSingletonHexString("content_purge_mark", "purged_through_utc");

    /// <inheritdoc />
    public void SetContentPurgeMarkUtc(string purgedThroughUtc)
    {
        string valueExpr = GuardedNonEmptyTextExpr("content_purge_mark", "purged_through_utc", purgedThroughUtc);
        ExecuteTransaction($"INSERT OR REPLACE INTO content_purge_mark (singleton_id, purged_through_utc) VALUES (1, {valueExpr});\n");
    }

    /// <inheritdoc />
    public ProjectionStateView? GetProjectionState()
    {
        string sql = TabReadScriptHeader +
                     "SELECT projection_schema_version, records_marker FROM projection_state WHERE singleton_id = 1 LIMIT 1;\n";

        string output = ExecuteSqlite(sql);
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 2, "projection_state");
            return new ProjectionStateView(
                ParseInt32Required(cols[0], "projection_state.projection_schema_version"),
                ParseInt64Required(cols[1], "projection_state.records_marker"));
        }

        return null;
    }

    /// <inheritdoc />
    public void SetProjectionState(ProjectionStateView state)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequireNonNegativeLong(state.RecordsMarker, "projection_state", "records_marker");

        ExecuteTransaction(
            string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT OR REPLACE INTO projection_state (singleton_id, projection_schema_version, records_marker) " +
                $"VALUES (1, {IntSql(state.ProjectionSchemaVersion)}, {LongSql(state.RecordsMarker)});\n"));
    }

    /// <inheritdoc />
    public DirtyProjectionState? GetProjectionDirtyState()
    {
        string sql = TabReadScriptHeader +
                     "SELECT 'RNG', 0, from_seq, to_seq FROM projection_dirty_range WHERE singleton_id = 1 " +
                     "UNION ALL " +
                     "SELECT 'SES', rowid, hex(CAST(session_id AS BLOB)), '' FROM projection_dirty_session " +
                     "ORDER BY 1 ASC, 2 ASC;\n";

        string output = ExecuteSqlite(sql);
        long? fromSeq = null;
        long? toSeq = null;
        List<string> keys = [];

        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 4, "projection_dirty");
            switch (cols[0])
            {
                case "RNG":
                    fromSeq = ParseInt64Required(cols[2], "projection_dirty_range.from_seq");
                    toSeq = ParseInt64Required(cols[3], "projection_dirty_range.to_seq");
                    break;
                case "SES":
                    keys.Add(ParseHexRequired(cols[2], "projection_dirty_session.session_id"));
                    break;
                default:
                    throw new StoredDataException($"Unexpected tag '{cols[0]}' in projection_dirty query.");
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
        RequireNonNegativeLong(state.FromSeq, "projection_dirty_range", "from_seq");
        RequireNonNegativeLong(state.ToSeq, "projection_dirty_range", "to_seq");

        StringBuilder body = new();
        body.Append(
            CultureInfo.InvariantCulture,
            $"INSERT OR REPLACE INTO projection_dirty_range (singleton_id, from_seq, to_seq) VALUES (1, {LongSql(state.FromSeq)}, {LongSql(state.ToSeq)});\n");
        body.Append("DELETE FROM projection_dirty_session;\n");

        if (!state.IsWildcard && state.Keys is not null)
        {
            HashSet<string> seenKeys = new(StringComparer.Ordinal);
            foreach (string key in state.Keys)
            {
                string keyExpr = GuardedNonEmptyTextExpr("projection_dirty_session", "session_id", key);
                if (!seenKeys.Add(key))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Duplicate session_id '{key}' in projection_dirty_session.");
                }

                body.Append(CultureInfo.InvariantCulture, $"INSERT INTO projection_dirty_session (session_id) VALUES ({keyExpr});\n");
            }
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void ClearProjectionDirtyState()
    {
        ExecuteTransaction("DELETE FROM projection_dirty_range;\nDELETE FROM projection_dirty_session;\n");
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionKeyAlias> ListSessionKeyAliases(string? sessionId = null)
    {
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append("SELECT hex(CAST(alias_key AS BLOB)), hex(CAST(session_id AS BLOB)) FROM session_key_alias");
        if (sessionId is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" WHERE session_id = {TextExpr(sessionId)}");
        }

        sql.Append(" ORDER BY rowid ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        List<SessionKeyAlias> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 2, "session_key_alias");
            results.Add(new SessionKeyAlias(
                ParseHexRequired(cols[0], "session_key_alias.alias_key"),
                ParseHexRequired(cols[1], "session_key_alias.session_id")));
        }

        return results;
    }

    /// <inheritdoc />
    public void ReplaceSessionKeyAliases(IEnumerable<SessionKeyAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        StringBuilder body = new();
        body.Append("DELETE FROM session_key_alias;\n");

        HashSet<string> seenAliases = new(StringComparer.Ordinal);
        foreach (SessionKeyAlias alias in aliases)
        {
            ArgumentNullException.ThrowIfNull(alias);
            string aliasExpr = GuardedNonEmptyTextExpr("session_key_alias", "alias_key", alias.AliasKey);
            string sessionExpr = GuardedNonEmptyTextExpr("session_key_alias", "session_id", alias.SessionId);
            if (!seenAliases.Add(alias.AliasKey))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate alias_key '{alias.AliasKey}' in session_key_alias.");
            }

            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO session_key_alias (alias_key, session_id) VALUES ({aliasExpr}, {sessionExpr});\n");
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void UpsertRefreshRun(RefreshRunRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        string idExpr = GuardedNonEmptyTextExpr("refresh_run", "refresh_run_id", row.Id);
        string startedExpr = GuardedNonEmptyTextExpr("refresh_run", "started_at_utc", row.StartedAt);
        string statusExpr = GuardedNonEmptyTextExpr("refresh_run", "status", row.Status);
        string triggerExpr = GuardedNonEmptyTextExpr("refresh_run", "trigger", row.Trigger);

        if (row.HistoryWeeks.HasValue)
        {
            RequirePositiveInt(row.HistoryWeeks.Value, "refresh_run_history_window", "history_weeks");
        }

        StringBuilder body = new();
        body.Append(CultureInfo.InvariantCulture, $"DELETE FROM refresh_run WHERE refresh_run_id = {idExpr};\n");
        body.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO refresh_run (refresh_run_id, started_at_utc, status, pid, trigger) " +
            $"VALUES ({idExpr}, {startedExpr}, {statusExpr}, {IntSql(row.Pid)}, {triggerExpr});\n");

        AppendOptionalNonEmptyTextChild(body, "refresh_run_completion", "refresh_run_id", idExpr, "completed_at_utc", row.CompletedAt);
        AppendOptionalNonEmptyTextChild(body, "refresh_run_summary", "refresh_run_id", idExpr, "summary", row.Summary);
        if (row.HistoryWeeks.HasValue)
        {
            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO refresh_run_history_window (refresh_run_id, history_weeks) VALUES ({idExpr}, {IntSql(row.HistoryWeeks.Value)});\n");
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public RefreshRunRow? GetRefreshRun(string refreshRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshRunId);
        IReadOnlyList<RefreshRunRow> rows = QueryRefreshRuns($"rr.refresh_run_id = {TextExpr(refreshRunId)}", "ORDER BY rr.rowid ASC");
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return QueryRefreshRuns(filterPredicate: null, string.Create(CultureInfo.InvariantCulture, $"ORDER BY rr.started_at_utc DESC, rr.rowid ASC LIMIT {IntSql(limit)}"));
    }

    /// <inheritdoc />
    public SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        IReadOnlyList<SourceCheckpoint> rows = QuerySourceCheckpoints(
            $"sc.harness_id = {TextExpr(harnessId)} AND sc.source_key = {TextExpr(sourceKey)}");
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public void UpsertSourceCheckpoint(SourceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        string harnessExpr = GuardedNonEmptyTextExpr("source_checkpoint", "harness_id", checkpoint.HarnessId);
        string sourceKeyExpr = GuardedNonEmptyTextExpr("source_checkpoint", "source_key", checkpoint.SourceKey);
        string providerExpr = GuardedNonEmptyTextExpr("source_checkpoint", "provider_id", checkpoint.ProviderId);
        string parserExpr = GuardedNonEmptyTextExpr("source_checkpoint", "parser_id", checkpoint.ParserId);
        string contractExpr = GuardedNonEmptyTextExpr("source_checkpoint", "parser_contract_version", checkpoint.ParserContractVersion);
        string formatExpr = GuardedNonEmptyTextExpr("source_checkpoint", "format", checkpoint.Format);
        string rootLabelExpr = GuardedTextExpr("source_checkpoint", "source_root_label", checkpoint.SourceRootLabel);
        string revisionExpr = GuardedTextExpr("source_checkpoint", "revision_token", checkpoint.RevisionToken);
        string coveredFromExpr = GuardedNonEmptyTextExpr("source_checkpoint", "covered_from_utc", checkpoint.CoveredFromUtc);
        string coveredThroughExpr = GuardedNonEmptyTextExpr("source_checkpoint", "covered_through_utc", checkpoint.CoveredThroughUtc);
        string lastAttemptExpr = GuardedNonEmptyTextExpr("source_checkpoint", "last_attempt_utc", checkpoint.LastAttemptUtc);
        string lastStatusExpr = GuardedNonEmptyTextExpr("source_checkpoint", "last_status", checkpoint.LastStatus);

        RequireNonNegativeInt(checkpoint.UnitCount, "source_checkpoint", "unit_count");
        RequireNonNegativeInt(checkpoint.RecordCount, "source_checkpoint", "record_count");

        string? successExpr = checkpoint.LastSuccessUtc is null
            ? null
            : GuardedNonEmptyTextExpr("source_checkpoint_success", "last_success_utc", checkpoint.LastSuccessUtc);
        string? errorExpr = checkpoint.LastErrorCode is null
            ? null
            : GuardedNonEmptyTextExpr("source_checkpoint_error", "last_error_code", checkpoint.LastErrorCode);

        StringBuilder body = new();
        body.Append(
            CultureInfo.InvariantCulture,
            $"DELETE FROM source_checkpoint WHERE harness_id = {harnessExpr} AND source_key = {sourceKeyExpr};\n");
        body.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO source_checkpoint (harness_id, source_key, provider_id, parser_id, parser_contract_version, format, " +
            $"source_root_label, revision_token, covered_from_utc, covered_through_utc, last_attempt_utc, last_status, unit_count, record_count) " +
            $"VALUES ({harnessExpr}, {sourceKeyExpr}, {providerExpr}, {parserExpr}, {contractExpr}, {formatExpr}, " +
            $"{rootLabelExpr}, {revisionExpr}, {coveredFromExpr}, {coveredThroughExpr}, {lastAttemptExpr}, {lastStatusExpr}, " +
            $"{IntSql(checkpoint.UnitCount)}, {IntSql(checkpoint.RecordCount)});\n");

        if (successExpr is not null)
        {
            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO source_checkpoint_success (harness_id, source_key, last_success_utc) VALUES ({harnessExpr}, {sourceKeyExpr}, {successExpr});\n");
        }

        if (errorExpr is not null)
        {
            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO source_checkpoint_error (harness_id, source_key, last_error_code) VALUES ({harnessExpr}, {sourceKeyExpr}, {errorExpr});\n");
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null) =>
        QuerySourceCheckpoints(harnessId is null ? null : $"sc.harness_id = {TextExpr(harnessId)}");

    /// <inheritdoc />
    public StoredIngestProblem? GetIngestProblem(long problemId)
    {
        IReadOnlyList<StoredIngestProblem> rows = QueryIngestProblems($"ip.problem_id = {LongSql(problemId)}", limit: null, offset: null);
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

        List<string> predicates = [];
        if (harnessId is not null)
        {
            predicates.Add($"iph.harness_id = {TextExpr(harnessId)}");
        }

        if (spanId is not null)
        {
            predicates.Add($"ips.span_id = {TextExpr(spanId)}");
        }

        string? where = predicates.Count > 0 ? string.Join(" AND ", predicates) : null;
        return QueryIngestProblems(where, limit, offset);
    }

    /// <inheritdoc />
    public int CountIngestProblems(string? harnessId = null)
    {
        string sql = harnessId is null
            ? TabReadScriptHeader + "SELECT COUNT(*) FROM ingest_problem;\n"
            : TabReadScriptHeader + $"SELECT COUNT(*) FROM ingest_problem_harness WHERE harness_id = {TextExpr(harnessId)};\n";

        string output = ExecuteSqlite(sql);
        string[] cols = SplitSingleRow(output, 1, "ingest_problem count");
        return ParseInt32Required(cols[0], "ingest_problem count");
    }

    /// <inheritdoc />
    public void PutQuarantinedSpan(StoredQuarantinedSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(span.ObservedNamespaces);

        string spanIdExpr = GuardedNonEmptyTextExpr("quarantined_span", "span_id", span.SpanId);
        string reasonExpr = GuardedNonEmptyTextExpr("quarantined_span", "reason", span.Reason);

        List<string> namespaceExprs = new(span.ObservedNamespaces.Count);
        HashSet<string> seenNamespaces = new(StringComparer.Ordinal);
        foreach (string ns in span.ObservedNamespaces)
        {
            string nsExpr = GuardedNonEmptyTextExpr("quarantined_span_namespace", "namespace_name", ns);
            if (!seenNamespaces.Add(ns))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate namespace_name '{ns}' for quarantined_span '{span.SpanId}'.");
            }

            namespaceExprs.Add(nsExpr);
        }

        StringBuilder body = new();
        body.Append(CultureInfo.InvariantCulture, $"DELETE FROM quarantined_span WHERE span_id = {spanIdExpr};\n");
        body.Append(CultureInfo.InvariantCulture, $"INSERT INTO quarantined_span (span_id, reason) VALUES ({spanIdExpr}, {reasonExpr});\n");

        foreach (string nsExpr in namespaceExprs)
        {
            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO quarantined_span_namespace (span_id, namespace_name) VALUES ({spanIdExpr}, {nsExpr});\n");
        }

        AppendOptionalNonEmptyTextChild(body, "quarantined_span_source", "span_id", spanIdExpr, "source", span.Source);
        AppendOptionalNonEmptyTextChild(body, "quarantined_span_name", "span_id", spanIdExpr, "name", span.Name);
        AppendOptionalNonEmptyTextChild(body, "quarantined_span_timestamp", "span_id", spanIdExpr, "timestamp_utc", span.TimestampUtc);
        AppendOptionalNonEmptyTextChild(body, "quarantined_span_harness", "span_id", spanIdExpr, "harness_id", span.HarnessId);

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public StoredQuarantinedSpan? GetQuarantinedSpan(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);
        IReadOnlyList<StoredQuarantinedSpan> rows = QueryQuarantinedSpans($"qs.span_id = {TextExpr(spanId)}", limit: null, offset: null);
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

        string? filter = harnessId is null ? null : $"qh.harness_id = {TextExpr(harnessId)}";
        return QueryQuarantinedSpans(filter, limit, offset);
    }

    /// <inheritdoc />
    public int CountQuarantinedSpans(string? harnessId = null)
    {
        string sql = harnessId is null
            ? TabReadScriptHeader + "SELECT COUNT(*) FROM quarantined_span;\n"
            : TabReadScriptHeader + $"SELECT COUNT(*) FROM quarantined_span_harness WHERE harness_id = {TextExpr(harnessId)};\n";

        string output = ExecuteSqlite(sql);
        string[] cols = SplitSingleRow(output, 1, "quarantined_span count");
        return ParseInt32Required(cols[0], "quarantined_span count");
    }

    /// <inheritdoc />
    public long? GetCachedTokenCount(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        string sql = TabReadScriptHeader +
                     $"SELECT token_count FROM token_cache WHERE hash = {TextExpr(hash)} LIMIT 1;\n";

        string output = ExecuteSqlite(sql);
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 1, "token_cache");
            return ParseInt64Required(cols[0], "token_cache.token_count");
        }

        return null;
    }

    /// <inheritdoc />
    public void PutCachedTokenCounts(IEnumerable<StoredTokenCacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        StringBuilder body = new();
        foreach (StoredTokenCacheEntry entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            string hashExpr = GuardedNonEmptyTextExpr("token_cache", "hash", entry.Hash);
            RequireNonNegativeLong(entry.TokenCount, "token_cache", "token_count");
            string? modelExpr = entry.ModelId is null
                ? null
                : GuardedNonEmptyTextExpr("token_cache_model", "model_id", entry.ModelId);

            body.Append(CultureInfo.InvariantCulture, $"DELETE FROM token_cache WHERE hash = {hashExpr};\n");
            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO token_cache (hash, token_count) VALUES ({hashExpr}, {LongSql(entry.TokenCount)});\n");
            if (modelExpr is not null)
            {
                body.Append(
                    CultureInfo.InvariantCulture,
                    $"INSERT INTO token_cache_model (hash, model_id) VALUES ({hashExpr}, {modelExpr});\n");
            }
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public StoredModelCatalogSnapshot GetModelContextWindowCatalog()
    {
        string sql = TabReadScriptHeader +
                     "SELECT 'CAT', hex(CAST(status AS BLOB)) FROM context_window_catalog_state WHERE singleton_id = 1;\n" +
                     "SELECT 'VND', hex(CAST(v.vendor AS BLOB)), hex(CAST(v.status AS BLOB)), " +
                     "typeof(vr.last_refresh_at_utc), COALESCE(hex(CAST(vr.last_refresh_at_utc AS BLOB)), ''), " +
                     "typeof(ve.last_error), COALESCE(hex(CAST(ve.last_error AS BLOB)), '') " +
                     "FROM model_context_window_catalog_vendor v " +
                     "LEFT JOIN model_context_window_catalog_vendor_refresh vr ON vr.vendor = v.vendor " +
                     "LEFT JOIN model_context_window_catalog_vendor_error ve ON ve.vendor = v.vendor " +
                     "ORDER BY v.rowid ASC;\n" +
                     "SELECT 'ENT', hex(CAST(e.lookup_id AS BLOB)), hex(CAST(e.canonical_model_id AS BLOB)), e.context_window, " +
                     "hex(CAST(e.vendor AS BLOB)), hex(CAST(e.documentation_url AS BLOB)), hex(CAST(e.retrieved_at_utc AS BLOB)), " +
                     "typeof(r.source_revision), COALESCE(hex(CAST(r.source_revision AS BLOB)), '') " +
                     "FROM model_context_window_catalog e " +
                     "LEFT JOIN model_context_window_source_revision r ON r.lookup_id = e.lookup_id " +
                     "ORDER BY e.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        string status = "empty";
        List<StoredModelCatalogVendorStatus> vendors = [];
        List<StoredModelContextWindowEntry> entries = [];

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t');
            if (cols.Length == 0)
            {
                continue;
            }

            switch (cols[0])
            {
                case "CAT":
                    if (cols.Length != 2)
                    {
                        throw new StoredDataException($"Expected 2 columns for context_window_catalog_state, got {cols.Length}.");
                    }

                    status = ParseHexRequired(cols[1], "context_window_catalog_state.status");
                    break;

                case "VND":
                    if (cols.Length != 7)
                    {
                        throw new StoredDataException($"Expected 7 columns for model_context_window_catalog_vendor, got {cols.Length}.");
                    }

                    vendors.Add(new StoredModelCatalogVendorStatus(
                        ParseHexRequired(cols[1], "model_context_window_catalog_vendor.vendor"),
                        ParseHexRequired(cols[2], "model_context_window_catalog_vendor.status"),
                        ParseNullableHex(cols[3], cols[4], "model_context_window_catalog_vendor_refresh.last_refresh_at_utc"),
                        ParseNullableHex(cols[5], cols[6], "model_context_window_catalog_vendor_error.last_error")));
                    break;

                case "ENT":
                    if (cols.Length != 9)
                    {
                        throw new StoredDataException($"Expected 9 columns for model_context_window_catalog, got {cols.Length}.");
                    }

                    entries.Add(new StoredModelContextWindowEntry(
                        ParseHexRequired(cols[1], "model_context_window_catalog.lookup_id"),
                        ParseHexRequired(cols[2], "model_context_window_catalog.canonical_model_id"),
                        ParseInt32Required(cols[3], "model_context_window_catalog.context_window"),
                        ParseHexRequired(cols[4], "model_context_window_catalog.vendor"),
                        new Uri(ParseHexRequired(cols[5], "model_context_window_catalog.documentation_url"), UriKind.RelativeOrAbsolute),
                        ParseHexRequired(cols[6], "model_context_window_catalog.retrieved_at_utc"),
                        ParseNullableHex(cols[7], cols[8], "model_context_window_source_revision.source_revision")));
                    break;

                default:
                    throw new StoredDataException($"Unexpected tag '{cols[0]}' in model_context_window_catalog query.");
            }
        }

        return new StoredModelCatalogSnapshot(status, vendors, entries);
    }

    /// <inheritdoc />
    public void ReplaceModelContextWindowCatalog(StoredModelCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Vendors);
        ArgumentNullException.ThrowIfNull(snapshot.Entries);

        string statusExpr = GuardedNonEmptyTextExpr("context_window_catalog_state", "status", snapshot.Status);

        StringBuilder body = new();
        body.Append(
            CultureInfo.InvariantCulture,
            $"INSERT OR REPLACE INTO context_window_catalog_state (singleton_id, status) VALUES (1, {statusExpr});\n");
        body.Append("DELETE FROM model_context_window_catalog_vendor;\n");

        HashSet<string> seenVendors = new(StringComparer.Ordinal);
        foreach (StoredModelCatalogVendorStatus vendor in snapshot.Vendors)
        {
            ArgumentNullException.ThrowIfNull(vendor);
            string vendorExpr = GuardedNonEmptyTextExpr("model_context_window_catalog_vendor", "vendor", vendor.Vendor);
            string vendorStatusExpr = GuardedNonEmptyTextExpr("model_context_window_catalog_vendor", "status", vendor.Status);
            if (!seenVendors.Add(vendor.Vendor))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate vendor '{vendor.Vendor}' in model_context_window_catalog_vendor.");
            }

            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO model_context_window_catalog_vendor (vendor, status) VALUES ({vendorExpr}, {vendorStatusExpr});\n");
            AppendOptionalNonEmptyTextChild(
                body,
                "model_context_window_catalog_vendor_refresh",
                "vendor",
                vendorExpr,
                "last_refresh_at_utc",
                vendor.LastRefreshAtUtc);
            AppendOptionalNonEmptyTextChild(
                body,
                "model_context_window_catalog_vendor_error",
                "vendor",
                vendorExpr,
                "last_error",
                vendor.LastError);
        }

        body.Append("DELETE FROM model_context_window_catalog;\n");

        HashSet<string> seenLookupIds = new(StringComparer.Ordinal);
        foreach (StoredModelContextWindowEntry entry in snapshot.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            string lookupIdExpr = GuardedNonEmptyTextExpr("model_context_window_catalog", "lookup_id", entry.LookupId);
            string canonicalExpr = GuardedNonEmptyTextExpr("model_context_window_catalog", "canonical_model_id", entry.CanonicalModelId);
            RequirePositiveInt(entry.ContextWindow, "model_context_window_catalog", "context_window");
            string entryVendorExpr = GuardedNonEmptyTextExpr("model_context_window_catalog", "vendor", entry.Vendor);
            string docUrlExpr = GuardedNonEmptyTextExpr(
                "model_context_window_catalog",
                "documentation_url",
                entry.DocumentationUrl?.ToString());
            string retrievedExpr = GuardedNonEmptyTextExpr("model_context_window_catalog", "retrieved_at_utc", entry.RetrievedAtUtc);

            if (!seenLookupIds.Add(entry.LookupId))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Duplicate lookup_id '{entry.LookupId}' in model_context_window_catalog.");
            }

            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO model_context_window_catalog (lookup_id, canonical_model_id, context_window, vendor, documentation_url, retrieved_at_utc) " +
                $"VALUES ({lookupIdExpr}, {canonicalExpr}, {IntSql(entry.ContextWindow)}, {entryVendorExpr}, {docUrlExpr}, {retrievedExpr});\n");
            AppendOptionalNonEmptyTextChild(
                body,
                "model_context_window_source_revision",
                "lookup_id",
                lookupIdExpr,
                "source_revision",
                entry.SourceRevision);
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public void PutMetricPoints(IEnumerable<StoredMetricPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        StringBuilder body = new();
        foreach (StoredMetricPoint point in points)
        {
            ArgumentNullException.ThrowIfNull(point);

            string pointKeyExpr = GuardedNonEmptyTextExpr("metric_point", "point_key", point.PointKey);
            string nameExpr = GuardedNonEmptyTextExpr("metric_point", "name", point.Name);
            string kindExpr = GuardedNonEmptyTextExpr("metric_point", "kind", point.Kind);

            string? unitExpr = point.Unit is null ? null : GuardedNonEmptyTextExpr("metric_point_unit", "unit", point.Unit);
            string? timeExpr = point.TimeUnixNano is null ? null : GuardedNonEmptyTextExpr("metric_point_time", "time_unix_nano", point.TimeUnixNano);
            string? startExpr = point.StartTimeUnixNano is null ? null : GuardedNonEmptyTextExpr("metric_point_start_time", "start_time_unix_nano", point.StartTimeUnixNano);
            string? tsExpr = point.TimestampUtc is null ? null : GuardedNonEmptyTextExpr("metric_point_timestamp", "timestamp_utc", point.TimestampUtc);
            string? valueSql = point.Value.HasValue ? RealSql("metric_point_value", "value", point.Value.Value) : null;
            string? sumSql = point.PointSum.HasValue ? RealSql("metric_point_sum", "point_sum", point.PointSum.Value) : null;
            string? sourceExpr = point.Source is null ? null : GuardedNonEmptyTextExpr("metric_point_source", "source", point.Source);

            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO metric_point (point_key, name, kind) VALUES ({pointKeyExpr}, {nameExpr}, {kindExpr}) ");
            body.Append("ON CONFLICT(point_key) DO UPDATE SET name = excluded.name, kind = excluded.kind;\n");

            foreach (string childTable in MetricPointChildTables)
            {
                body.Append(
                    CultureInfo.InvariantCulture,
                    $"DELETE FROM {childTable} WHERE point_id = (SELECT point_id FROM metric_point WHERE point_key = {pointKeyExpr});\n");
            }

            AppendMetricPointOptionalChild(body, pointKeyExpr, "metric_point_unit", "unit", unitExpr);
            if (point.AggregationTemporality.HasValue)
            {
                AppendMetricPointOptionalChild(
                    body,
                    pointKeyExpr,
                    "metric_point_temporality",
                    "aggregation_temporality",
                    IntSql(point.AggregationTemporality.Value));
            }

            AppendMetricPointOptionalChild(body, pointKeyExpr, "metric_point_time", "time_unix_nano", timeExpr);
            AppendMetricPointOptionalChild(body, pointKeyExpr, "metric_point_start_time", "start_time_unix_nano", startExpr);
            AppendMetricPointOptionalChild(body, pointKeyExpr, "metric_point_timestamp", "timestamp_utc", tsExpr);
            AppendMetricPointOptionalChild(body, pointKeyExpr, "metric_point_value", "value", valueSql);
            if (point.PointCount.HasValue)
            {
                AppendMetricPointOptionalChild(
                    body,
                    pointKeyExpr,
                    "metric_point_count",
                    "point_count",
                    LongSql(point.PointCount.Value));
            }

            AppendMetricPointOptionalChild(body, pointKeyExpr, "metric_point_sum", "point_sum", sumSql);
            AppendMetricPointOptionalChild(body, pointKeyExpr, "metric_point_source", "source", sourceExpr);
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredMetricPoint> ListMetricPoints(string? name = null, int? limit = null)
    {
        if (limit.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit.Value);
        }

        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(
            "SELECT hex(CAST(mp.point_key AS BLOB)), hex(CAST(mp.name AS BLOB)), hex(CAST(mp.kind AS BLOB)), " +
            "typeof(mpu.unit), COALESCE(hex(CAST(mpu.unit AS BLOB)), ''), " +
            "typeof(mpt.aggregation_temporality), COALESCE(CAST(mpt.aggregation_temporality AS TEXT), ''), " +
            "typeof(mpti.time_unix_nano), COALESCE(hex(CAST(mpti.time_unix_nano AS BLOB)), ''), " +
            "typeof(mpst.start_time_unix_nano), COALESCE(hex(CAST(mpst.start_time_unix_nano AS BLOB)), ''), " +
            "typeof(mpts.timestamp_utc), COALESCE(hex(CAST(mpts.timestamp_utc AS BLOB)), ''), " +
            "typeof(mpv.value), COALESCE(CAST(mpv.value AS TEXT), ''), " +
            "typeof(mpc.point_count), COALESCE(CAST(mpc.point_count AS TEXT), ''), " +
            "typeof(mpsu.point_sum), COALESCE(CAST(mpsu.point_sum AS TEXT), ''), " +
            "typeof(mpso.source), COALESCE(hex(CAST(mpso.source AS BLOB)), '') " +
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
            sql.Append(CultureInfo.InvariantCulture, $" WHERE mp.name = {TextExpr(name)}");
        }

        sql.Append(" ORDER BY mp.point_id ASC");
        AppendLimitOffset(sql, limit, offset: null);
        sql.Append(";\n");

        string output = ExecuteSqlite(sql.ToString());
        List<StoredMetricPoint> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 21, "metric_point");
            long? temp = ParseNullableInt64(cols[5], cols[6], "metric_point_temporality.aggregation_temporality");
            results.Add(new StoredMetricPoint(
                ParseHexRequired(cols[0], "metric_point.point_key"),
                ParseHexRequired(cols[1], "metric_point.name"),
                ParseHexRequired(cols[2], "metric_point.kind"),
                ParseNullableHex(cols[3], cols[4], "metric_point_unit.unit"),
                temp.HasValue ? (int)temp.Value : null,
                ParseNullableHex(cols[7], cols[8], "metric_point_time.time_unix_nano"),
                ParseNullableHex(cols[9], cols[10], "metric_point_start_time.start_time_unix_nano"),
                ParseNullableHex(cols[11], cols[12], "metric_point_timestamp.timestamp_utc"),
                ParseNullableDouble(cols[13], cols[14], "metric_point_value.value"),
                ParseNullableInt64(cols[15], cols[16], "metric_point_count.point_count"),
                ParseNullableDouble(cols[17], cols[18], "metric_point_sum.point_sum"),
                ParseNullableHex(cols[19], cols[20], "metric_point_source.source")));
        }

        return results;
    }

    /// <inheritdoc />
    public void RecordIngest(string source, int count, string timestampUtc)
    {
        string sourceExpr = GuardedNonEmptyTextExpr("ingest_log", "source", source);
        RequireNonNegativeInt(count, "ingest_log", "record_count");
        string timestampExpr = GuardedNonEmptyTextExpr("ingest_log", "timestamp_utc", timestampUtc);

        ExecuteTransaction(
            string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT INTO ingest_log (source, record_count, timestamp_utc) VALUES ({sourceExpr}, {IntSql(count)}, {timestampExpr});\n"));
    }

    /// <inheritdoc />
    public IReadOnlyList<IngestLogEntry> ListIngestLog(string? source = null)
    {
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append("SELECT ingest_id, hex(CAST(source AS BLOB)), record_count, hex(CAST(timestamp_utc AS BLOB)) FROM ingest_log");
        if (source is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" WHERE source = {TextExpr(source)}");
        }

        sql.Append(" ORDER BY ingest_id ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        List<IngestLogEntry> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 4, "ingest_log");
            results.Add(new IngestLogEntry(
                ParseInt32Required(cols[0], "ingest_log.ingest_id"),
                ParseHexRequired(cols[1], "ingest_log.source"),
                ParseInt32Required(cols[2], "ingest_log.record_count"),
                ParseHexRequired(cols[3], "ingest_log.timestamp_utc")));
        }

        return results;
    }

    /// <inheritdoc />
    public void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        StringBuilder body = new();
        foreach (RecordProvenanceRow row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);

            string spanIdExpr = GuardedNonEmptyTextExpr("record_provenance", "span_id", row.SpanId);
            string harnessExpr = GuardedNonEmptyTextExpr("record_provenance", "harness_id", row.HarnessId);
            string sourceKeyExpr = GuardedNonEmptyTextExpr("record_provenance", "source_key", row.SourceKey);
            string sourceRevExpr = GuardedTextExpr("record_provenance", "source_revision", row.SourceRevision);
            string parserVerExpr = GuardedTextExpr("record_provenance", "parser_version", row.ParserVersion);
            string importedExpr = GuardedNonEmptyTextExpr("record_provenance", "imported_at_utc", row.ImportedAtUtc);
            string locationExpr = GuardedTextExpr("record_provenance", "location_token", row.LocationToken);

            body.Append(CultureInfo.InvariantCulture, $"DELETE FROM record_provenance WHERE span_id = {spanIdExpr};\n");
            body.Append(
                CultureInfo.InvariantCulture,
                $"INSERT INTO record_provenance (span_id, harness_id, source_key, source_revision, parser_version, imported_at_utc, location_token) " +
                $"VALUES ({spanIdExpr}, {harnessExpr}, {sourceKeyExpr}, {sourceRevExpr}, {parserVerExpr}, {importedExpr}, {locationExpr});\n");

            AppendOptionalNonEmptyTextChild(
                body,
                "record_provenance_native_session",
                "span_id",
                spanIdExpr,
                "native_session_id",
                row.NativeSessionId);
            AppendOptionalNonEmptyTextChild(
                body,
                "record_provenance_native_record",
                "span_id",
                spanIdExpr,
                "native_record_id",
                row.NativeRecordId);
        }

        ExecuteTransaction(body.ToString());
    }

    /// <inheritdoc />
    public IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(rp.span_id AS BLOB)), hex(CAST(rp.harness_id AS BLOB)), hex(CAST(rp.source_key AS BLOB)), " +
                     "typeof(rpns.native_session_id), COALESCE(hex(CAST(rpns.native_session_id AS BLOB)), ''), " +
                     "typeof(rpnr.native_record_id), COALESCE(hex(CAST(rpnr.native_record_id AS BLOB)), ''), " +
                     "hex(CAST(rp.source_revision AS BLOB)), hex(CAST(rp.parser_version AS BLOB)), " +
                     "hex(CAST(rp.imported_at_utc AS BLOB)), hex(CAST(rp.location_token AS BLOB)) " +
                     "FROM record_provenance rp " +
                     "LEFT JOIN record_provenance_native_session rpns ON rpns.span_id = rp.span_id " +
                     "LEFT JOIN record_provenance_native_record rpnr ON rpnr.span_id = rp.span_id " +
                     $"WHERE rp.harness_id = {TextExpr(harnessId)} AND rp.source_key = {TextExpr(sourceKey)} " +
                     "ORDER BY rp.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<RecordProvenanceRow> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 11, "record_provenance");
            results.Add(new RecordProvenanceRow(
                ParseHexRequired(cols[0], "record_provenance.span_id"),
                ParseHexRequired(cols[1], "record_provenance.harness_id"),
                ParseHexRequired(cols[2], "record_provenance.source_key"),
                ParseNullableHex(cols[3], cols[4], "record_provenance_native_session.native_session_id"),
                ParseNullableHex(cols[5], cols[6], "record_provenance_native_record.native_record_id"),
                ParseHexAllowEmpty(cols[7], "record_provenance.source_revision"),
                ParseHexAllowEmpty(cols[8], "record_provenance.parser_version"),
                ParseHexRequired(cols[9], "record_provenance.imported_at_utc"),
                ParseHexAllowEmpty(cols[10], "record_provenance.location_token")));
        }

        return results;
    }

    private string? ReadSingletonHexString(string tableName, string columnName)
    {
        string sql = TabReadScriptHeader +
                     $"SELECT hex(CAST({columnName} AS BLOB)) FROM {tableName} WHERE singleton_id = 1 LIMIT 1;\n";

        string output = ExecuteSqlite(sql);
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 1, tableName);
            return ParseHexRequired(cols[0], $"{tableName}.{columnName}");
        }

        return null;
    }

    private IReadOnlyList<RefreshRunRow> QueryRefreshRuns(string? filterPredicate, string orderAndLimit)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(rr.refresh_run_id AS BLOB)), hex(CAST(rr.started_at_utc AS BLOB)), " +
                     "typeof(rrc.completed_at_utc), COALESCE(hex(CAST(rrc.completed_at_utc AS BLOB)), ''), " +
                     "hex(CAST(rr.status AS BLOB)), rr.pid, hex(CAST(rr.trigger AS BLOB)), " +
                     "typeof(rrs.summary), COALESCE(hex(CAST(rrs.summary AS BLOB)), ''), " +
                     "typeof(rrhw.history_weeks), COALESCE(CAST(rrhw.history_weeks AS TEXT), '') " +
                     "FROM refresh_run rr " +
                     "LEFT JOIN refresh_run_completion rrc ON rrc.refresh_run_id = rr.refresh_run_id " +
                     "LEFT JOIN refresh_run_summary rrs ON rrs.refresh_run_id = rr.refresh_run_id " +
                     $"LEFT JOIN refresh_run_history_window rrhw ON rrhw.refresh_run_id = rr.refresh_run_id{whereClause} {orderAndLimit};\n";

        string output = ExecuteSqlite(sql);
        List<RefreshRunRow> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 11, "refresh_run");
            long? historyWeeks = ParseNullableInt64(cols[9], cols[10], "refresh_run_history_window.history_weeks");
            results.Add(new RefreshRunRow(
                ParseHexRequired(cols[0], "refresh_run.refresh_run_id"),
                ParseHexRequired(cols[1], "refresh_run.started_at_utc"),
                ParseNullableHex(cols[2], cols[3], "refresh_run_completion.completed_at_utc"),
                ParseHexRequired(cols[4], "refresh_run.status"),
                ParseInt32Required(cols[5], "refresh_run.pid"),
                ParseHexRequired(cols[6], "refresh_run.trigger"),
                ParseNullableHex(cols[7], cols[8], "refresh_run_summary.summary"),
                historyWeeks.HasValue ? (int)historyWeeks.Value : null));
        }

        return results;
    }

    private IReadOnlyList<SourceCheckpoint> QuerySourceCheckpoints(string? filterPredicate)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(sc.harness_id AS BLOB)), hex(CAST(sc.source_key AS BLOB)), hex(CAST(sc.provider_id AS BLOB)), " +
                     "hex(CAST(sc.parser_id AS BLOB)), hex(CAST(sc.parser_contract_version AS BLOB)), hex(CAST(sc.format AS BLOB)), " +
                     "hex(CAST(sc.source_root_label AS BLOB)), hex(CAST(sc.revision_token AS BLOB)), " +
                     "hex(CAST(sc.covered_from_utc AS BLOB)), hex(CAST(sc.covered_through_utc AS BLOB)), hex(CAST(sc.last_attempt_utc AS BLOB)), " +
                     "typeof(scs.last_success_utc), COALESCE(hex(CAST(scs.last_success_utc AS BLOB)), ''), " +
                     "hex(CAST(sc.last_status AS BLOB)), " +
                     "typeof(sce.last_error_code), COALESCE(hex(CAST(sce.last_error_code AS BLOB)), ''), " +
                     "sc.unit_count, sc.record_count " +
                     "FROM source_checkpoint sc " +
                     "LEFT JOIN source_checkpoint_success scs ON scs.harness_id = sc.harness_id AND scs.source_key = sc.source_key " +
                     $"LEFT JOIN source_checkpoint_error sce ON sce.harness_id = sc.harness_id AND sce.source_key = sc.source_key{whereClause} " +
                     "ORDER BY sc.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<SourceCheckpoint> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 18, "source_checkpoint");
            results.Add(new SourceCheckpoint(
                ParseHexRequired(cols[0], "source_checkpoint.harness_id"),
                ParseHexRequired(cols[1], "source_checkpoint.source_key"),
                ParseHexRequired(cols[2], "source_checkpoint.provider_id"),
                ParseHexRequired(cols[3], "source_checkpoint.parser_id"),
                ParseHexRequired(cols[4], "source_checkpoint.parser_contract_version"),
                ParseHexRequired(cols[5], "source_checkpoint.format"),
                ParseHexAllowEmpty(cols[6], "source_checkpoint.source_root_label"),
                ParseHexAllowEmpty(cols[7], "source_checkpoint.revision_token"),
                ParseHexRequired(cols[8], "source_checkpoint.covered_from_utc"),
                ParseHexRequired(cols[9], "source_checkpoint.covered_through_utc"),
                ParseHexRequired(cols[10], "source_checkpoint.last_attempt_utc"),
                ParseNullableHex(cols[11], cols[12], "source_checkpoint_success.last_success_utc"),
                ParseHexRequired(cols[13], "source_checkpoint.last_status"),
                ParseNullableHex(cols[14], cols[15], "source_checkpoint_error.last_error_code"),
                ParseInt32Required(cols[16], "source_checkpoint.unit_count"),
                ParseInt32Required(cols[17], "source_checkpoint.record_count")));
        }

        return results;
    }

    private IReadOnlyList<StoredIngestProblem> QueryIngestProblems(string? filterPredicate, int? limit, int? offset)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(
            "SELECT ip.problem_id, hex(CAST(ip.problem_key AS BLOB)), hex(CAST(ip.severity AS BLOB)), hex(CAST(ip.code AS BLOB)), hex(CAST(ip.message AS BLOB)), " +
            "typeof(ips.span_id), COALESCE(hex(CAST(ips.span_id AS BLOB)), ''), " +
            "typeof(ipl.location), COALESCE(hex(CAST(ipl.location AS BLOB)), ''), " +
            "typeof(ipse.session_id), COALESCE(hex(CAST(ipse.session_id AS BLOB)), ''), " +
            "typeof(iph.harness_id), COALESCE(hex(CAST(iph.harness_id AS BLOB)), ''), " +
            "typeof(ipt.timestamp_utc), COALESCE(hex(CAST(ipt.timestamp_utc AS BLOB)), ''), " +
            "typeof(ipst.stack), COALESCE(hex(CAST(ipst.stack AS BLOB)), '') " +
            "FROM ingest_problem ip " +
            "LEFT JOIN ingest_problem_span ips ON ips.problem_id = ip.problem_id " +
            "LEFT JOIN ingest_problem_location ipl ON ipl.problem_id = ip.problem_id " +
            "LEFT JOIN ingest_problem_session ipse ON ipse.problem_id = ip.problem_id " +
            "LEFT JOIN ingest_problem_harness iph ON iph.problem_id = ip.problem_id " +
            "LEFT JOIN ingest_problem_timestamp ipt ON ipt.problem_id = ip.problem_id " +
            $"LEFT JOIN ingest_problem_stack ipst ON ipst.problem_id = ip.problem_id{whereClause} " +
            "ORDER BY ip.problem_id ASC");
        AppendLimitOffset(sql, limit, offset);
        sql.Append(";\n");

        string output = ExecuteSqlite(sql.ToString());
        List<StoredIngestProblem> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 17, "ingest_problem");
            long problemId = ParseInt64Required(cols[0], "ingest_problem.problem_id");
            CollectedProblem problem = new(
                ParseHexRequired(cols[1], "ingest_problem.problem_key"),
                ParseHexRequired(cols[2], "ingest_problem.severity"),
                ParseHexRequired(cols[3], "ingest_problem.code"),
                ParseHexRequired(cols[4], "ingest_problem.message"),
                ParseNullableHex(cols[5], cols[6], "ingest_problem_span.span_id"),
                ParseNullableHex(cols[7], cols[8], "ingest_problem_location.location"),
                ParseNullableHex(cols[9], cols[10], "ingest_problem_session.session_id"),
                ParseNullableHex(cols[11], cols[12], "ingest_problem_harness.harness_id"),
                ParseNullableHex(cols[13], cols[14], "ingest_problem_timestamp.timestamp_utc"),
                ParseNullableHex(cols[15], cols[16], "ingest_problem_stack.stack"));
            results.Add(new StoredIngestProblem(problemId, problem));
        }

        return results;
    }

    private IReadOnlyList<StoredQuarantinedSpan> QueryQuarantinedSpans(string? filterPredicate, int? limit, int? offset)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(
            "WITH selected AS (" +
            "SELECT qs.rowid AS sort_ord, qs.span_id AS span_id, qs.reason AS reason, " +
            "qsrc.source AS source, qn.name AS name, qts.timestamp_utc AS timestamp_utc, qh.harness_id AS harness_id " +
            "FROM quarantined_span qs " +
            "LEFT JOIN quarantined_span_source qsrc ON qsrc.span_id = qs.span_id " +
            "LEFT JOIN quarantined_span_name qn ON qn.span_id = qs.span_id " +
            "LEFT JOIN quarantined_span_timestamp qts ON qts.span_id = qs.span_id " +
            $"LEFT JOIN quarantined_span_harness qh ON qh.span_id = qs.span_id{whereClause} " +
            "ORDER BY qs.rowid ASC");
        AppendLimitOffset(sql, limit, offset);
        sql.Append(
            ") " +
            "SELECT 'QSP', sort_ord, hex(CAST(span_id AS BLOB)), hex(CAST(reason AS BLOB)), " +
            "typeof(source), COALESCE(hex(CAST(source AS BLOB)), ''), " +
            "typeof(name), COALESCE(hex(CAST(name AS BLOB)), ''), " +
            "typeof(timestamp_utc), COALESCE(hex(CAST(timestamp_utc AS BLOB)), ''), " +
            "typeof(harness_id), COALESCE(hex(CAST(harness_id AS BLOB)), '') " +
            "FROM selected " +
            "UNION ALL " +
            "SELECT 'QNS', qns.rowid, hex(CAST(qns.span_id AS BLOB)), hex(CAST(qns.namespace_name AS BLOB)), " +
            "'', '', '', '', '', '', '', '' " +
            "FROM quarantined_span_namespace qns " +
            "JOIN selected s ON s.span_id = qns.span_id " +
            "ORDER BY 1 DESC, 2 ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        List<StoredQuarantinedSpan> results = [];
        Dictionary<string, List<string>> namespacesBySpan = new(StringComparer.Ordinal);

        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 12, "quarantined_span");
            switch (cols[0])
            {
                case "QSP":
                    string spanId = ParseHexRequired(cols[2], "quarantined_span.span_id");
                    List<string> nsList = [];
                    namespacesBySpan[spanId] = nsList;
                    results.Add(new StoredQuarantinedSpan(
                        spanId,
                        ParseHexRequired(cols[3], "quarantined_span.reason"),
                        nsList,
                        ParseNullableHex(cols[4], cols[5], "quarantined_span_source.source"),
                        ParseNullableHex(cols[6], cols[7], "quarantined_span_name.name"),
                        ParseNullableHex(cols[8], cols[9], "quarantined_span_timestamp.timestamp_utc"),
                        ParseNullableHex(cols[10], cols[11], "quarantined_span_harness.harness_id")));
                    break;

                case "QNS":
                    string childSpanId = ParseHexRequired(cols[2], "quarantined_span_namespace.span_id");
                    string ns = ParseHexRequired(cols[3], "quarantined_span_namespace.namespace_name");
                    if (namespacesBySpan.TryGetValue(childSpanId, out List<string>? list))
                    {
                        list.Add(ns);
                    }

                    break;

                default:
                    throw new StoredDataException($"Unexpected tag '{cols[0]}' in quarantined_span query.");
            }
        }

        return results;
    }

    private static string ParseHexAllowEmpty(string hexCell, string columnName) =>
        hexCell.Length == 0 || string.Equals(hexCell, "\"\"", StringComparison.Ordinal)
            ? string.Empty
            : ParseHexRequired(hexCell, columnName);

    private static string[] SplitSingleRow(string output, int expectedColumns, string context)
    {
        List<string> lines = [.. TabLines(output)];
        if (lines.Count != 1)
        {
            throw new StoredDataException($"Expected single scalar row from {context} query, got {lines.Count}.");
        }

        return SplitTabLine(lines[0], expectedColumns, context);
    }

    private static string GuardedNonEmptyTextExpr(string tableName, string columnName, string? value)
    {
        string expr = GuardedTextExpr(tableName, columnName, value);
        if (value!.Length == 0)
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                $"Non-empty TEXT column '{tableName}.{columnName}' cannot be empty.");
        }

        return expr;
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

    private static void RequireNonNegativeInt(int value, string tableName, string columnName)
    {
        if (value < 0)
        {
            throw new DashValidationException(
                ConstraintViolatedCode,
                $"INTEGER column '{tableName}.{columnName}' cannot be negative.");
        }
    }

    private static void RequireNonNegativeLong(long value, string tableName, string columnName)
    {
        if (value < 0)
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

    private static void AppendOptionalNonEmptyTextChild(
        StringBuilder sql,
        string tableName,
        string keyColumn,
        string keyExpr,
        string valueColumn,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        string valueExpr = GuardedNonEmptyTextExpr(tableName, valueColumn, value);
        sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO {tableName} ({keyColumn}, {valueColumn}) VALUES ({keyExpr}, {valueExpr});\n");
    }

    private static void AppendMetricPointOptionalChild(
        StringBuilder sql,
        string pointKeyExpr,
        string tableName,
        string columnName,
        string? valueSql)
    {
        if (valueSql is null)
        {
            return;
        }

        sql.Append(
            CultureInfo.InvariantCulture,
            $"INSERT INTO {tableName} (point_id, {columnName}) SELECT point_id, {valueSql} FROM metric_point WHERE point_key = {pointKeyExpr};\n");
    }

    private static void AppendLimitOffset(StringBuilder sql, int? limit, int? offset)
    {
        if (limit.HasValue && offset.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {IntSql(limit.Value)} OFFSET {IntSql(offset.Value)}");
        }
        else if (limit.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {IntSql(limit.Value)}");
        }
        else if (offset.HasValue && offset.Value > 0)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT -1 OFFSET {IntSql(offset.Value)}");
        }
    }
}
