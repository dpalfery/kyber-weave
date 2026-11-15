using System.Globalization;

namespace KyberDash.Domain;

/// <summary>
/// Canonical SQLite schema contract, version constants, and normalization helpers for
/// KyberDash (<c>canon.db</c>), matching <c>dash/src/canon/store-schema.ts</c>,
/// <c>dash/src/canon/source-state.ts</c>, and <c>dash/src/canon/refresh-run.ts</c>.
/// </summary>
public static class CanonSchema
{
    /// <summary>Current canonical SQLite schema version.</summary>
    public const int SchemaVersion = 24;

    /// <summary>Current detector generation stamped in <c>metadata</c>.</summary>
    public const int DetectorVersion = 5;

    /// <summary>Maximum retained WAL journal size in bytes after checkpoint (32 MiB).</summary>
    public const int WalSizeLimitBytes = 32 * 1024 * 1024;

    /// <summary>Metadata key tracking the monotonic write sequence of <c>records</c>.</summary>
    public const string RecordsWriteMarkerKey = "records_write_seq";

    /// <summary>Metadata key recording dirty session keys awaiting incremental projection.</summary>
    public const string ProjectionDirtySessionsKey = "projection_dirty_sessions";

    /// <summary>Metadata key mapping canonical session keys to built session IDs.</summary>
    public const string ProjectionSessionKeyMapKey = "projection_session_key_map";

    /// <summary>Maximum persisted length of an ingest audit source key.</summary>
    public const int MaxIngestSourceLength = 256;

    /// <summary>DDL for the metadata table required before reading the stored schema version.</summary>
    public const string MetadataSql = """
        CREATE TABLE IF NOT EXISTS metadata (
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );
        """;

    /// <summary>DDL for the normalized <c>session_turn</c> child table.</summary>
    public const string SessionTurnTableSql = """
        CREATE TABLE IF NOT EXISTS session_turn (
          session_id TEXT NOT NULL,
          turn_index INTEGER NOT NULL,
          turn_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS session_turn_by_session ON session_turn (session_id, turn_index);
        """;

    /// <summary>DDL for source-unit checkpoints and record provenance tables.</summary>
    public const string SourceStateSql = """
        CREATE TABLE IF NOT EXISTS source_checkpoint (
          harness_id TEXT NOT NULL,
          source_key TEXT NOT NULL,
          provider_id TEXT NOT NULL,
          parser_id TEXT NOT NULL,
          parser_contract_version TEXT NOT NULL,
          format TEXT NOT NULL,
          source_root_label TEXT NOT NULL,
          revision_token TEXT NOT NULL,
          covered_from_utc TEXT NOT NULL,
          covered_through_utc TEXT NOT NULL,
          last_attempt_utc TEXT NOT NULL,
          last_success_utc TEXT,
          last_status TEXT NOT NULL,
          last_error_code TEXT,
          unit_count INTEGER NOT NULL DEFAULT 0,
          record_count INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (harness_id, source_key)
        );
        CREATE INDEX IF NOT EXISTS source_checkpoint_by_harness ON source_checkpoint (harness_id);

        CREATE TABLE IF NOT EXISTS record_provenance (
          span_id TEXT NOT NULL PRIMARY KEY CHECK (length(span_id) > 0),
          harness_id TEXT NOT NULL,
          source_key TEXT NOT NULL,
          native_session_id TEXT,
          native_record_id TEXT,
          source_revision TEXT NOT NULL,
          parser_version TEXT NOT NULL,
          imported_at_utc TEXT NOT NULL,
          location_token TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS record_provenance_by_source ON record_provenance (harness_id, source_key);
        CREATE INDEX IF NOT EXISTS record_provenance_by_native
          ON record_provenance (harness_id, native_session_id, native_record_id);

        CREATE INDEX IF NOT EXISTS records_by_harness_session_time
          ON records (harness, session_id, timestamp);
        """;

    /// <summary>DDL for the refresh execution audit table.</summary>
    public const string RefreshRunSql = """
        CREATE TABLE IF NOT EXISTS refresh_run (
          id TEXT PRIMARY KEY,
          started_at TEXT NOT NULL,
          completed_at TEXT,
          status TEXT NOT NULL,
          pid INTEGER NOT NULL,
          trigger TEXT NOT NULL,
          summary TEXT,
          history_weeks INTEGER
        );
        CREATE INDEX IF NOT EXISTS refresh_run_by_started_at ON refresh_run (started_at DESC);
        CREATE INDEX IF NOT EXISTS refresh_run_by_status ON refresh_run (status, started_at DESC);
        """;

    /// <summary>Core canonical store DDL (idempotent <c>CREATE IF NOT EXISTS</c> statements).</summary>
    public const string CoreSchemaSql = """
        CREATE TABLE IF NOT EXISTS records (
          span_id TEXT PRIMARY KEY,
          trace_id TEXT,
          parent_span_id TEXT,
          source TEXT NOT NULL,
          harness TEXT NOT NULL,
          session_id TEXT,
          name TEXT NOT NULL,
          op TEXT NOT NULL,
          kind TEXT NOT NULL,
          timestamp TEXT NOT NULL,
          duration_ms INTEGER NOT NULL,
          status TEXT NOT NULL,
          tokens_json TEXT NOT NULL,
          content_json TEXT NOT NULL,
          cost_json TEXT NOT NULL,
          measurability_json TEXT,
          parts_json BLOB,
          raw BLOB
        );
        CREATE INDEX IF NOT EXISTS records_by_trace ON records (trace_id);
        CREATE INDEX IF NOT EXISTS records_by_timestamp ON records (timestamp);
        CREATE INDEX IF NOT EXISTS records_by_session ON records (session_id);
        CREATE INDEX IF NOT EXISTS records_by_session_key
          ON records (COALESCE(session_id, trace_id), timestamp);
        CREATE TABLE IF NOT EXISTS session (
          session_id TEXT PRIMARY KEY,
          harness TEXT NOT NULL,
          label TEXT,
          is_subagent INTEGER NOT NULL DEFAULT 0,
          parent_session TEXT,
          agent_name TEXT,
          repo TEXT,
          branch TEXT,
          started TEXT,
          ended TEXT,
          synced_at TEXT,
          sync_status TEXT DEFAULT 'pending',
          settled_at TEXT,
          transcript_path TEXT,
          transcript_offset INTEGER DEFAULT 0,
          summary_json TEXT,
          problems_count INTEGER,
          context_limit_source TEXT,
          payload TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS session_by_started ON session (started);
        CREATE TABLE IF NOT EXISTS run (
          run_id TEXT PRIMARY KEY,
          harness TEXT NOT NULL,
          label TEXT,
          grouping_basis TEXT NOT NULL,
          grouping_rule TEXT,
          working_directory TEXT,
          started TEXT,
          ended TEXT,
          execution_count INTEGER NOT NULL DEFAULT 0,
          payload TEXT
        );
        CREATE INDEX IF NOT EXISTS run_by_harness ON run (harness);
        CREATE INDEX IF NOT EXISTS run_by_started ON run (started);
        CREATE TABLE IF NOT EXISTS execution (
          execution_id TEXT PRIMARY KEY,
          run_id TEXT NOT NULL,
          session_id TEXT,
          parent_execution_id TEXT,
          harness TEXT NOT NULL,
          agent_name TEXT,
          is_root INTEGER NOT NULL DEFAULT 0,
          started TEXT,
          ended TEXT,
          parent_linkage_json TEXT NOT NULL,
          payload TEXT
        );
        CREATE INDEX IF NOT EXISTS execution_by_run ON execution (run_id);
        CREATE INDEX IF NOT EXISTS execution_by_parent ON execution (parent_execution_id);
        CREATE INDEX IF NOT EXISTS execution_by_session ON execution (session_id);
        CREATE TABLE IF NOT EXISTS token_cache (
          hash TEXT PRIMARY KEY,
          count INTEGER NOT NULL,
          model TEXT
        );
        CREATE TABLE IF NOT EXISTS quarantine (
          span_id TEXT PRIMARY KEY,
          namespaces TEXT NOT NULL,
          reason TEXT NOT NULL,
          source TEXT,
          name TEXT,
          timestamp TEXT,
          raw_payload TEXT,
          harness TEXT
        );
        CREATE TABLE IF NOT EXISTS pending_logs (
          log_id TEXT PRIMARY KEY,
          payload TEXT NOT NULL,
          harness TEXT
        );
        CREATE TABLE IF NOT EXISTS quarantined_logs (
          log_id TEXT PRIMARY KEY,
          payload TEXT NOT NULL,
          reason TEXT NOT NULL,
          harness TEXT
        );
        CREATE TABLE IF NOT EXISTS enriched_logs (
          log_id TEXT PRIMARY KEY
        );
        CREATE TABLE IF NOT EXISTS problems (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          span_id TEXT,
          problem_key TEXT NOT NULL DEFAULT '',
          severity TEXT NOT NULL,
          code TEXT NOT NULL,
          message TEXT NOT NULL,
          location TEXT,
          session_id TEXT,
          harness TEXT,
          timestamp TEXT,
          stack TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS problems_by_identity ON problems (problem_key);
        CREATE TABLE IF NOT EXISTS ingest_log (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          source TEXT NOT NULL,
          count INTEGER NOT NULL,
          timestamp TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ingest_log_by_source ON ingest_log (source);
        CREATE INDEX IF NOT EXISTS ingest_log_by_timestamp ON ingest_log (timestamp);
        CREATE TABLE IF NOT EXISTS metadata (
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS harness_rollup (
          harness TEXT PRIMARY KEY,
          sample_count INTEGER NOT NULL DEFAULT 0,
          context_pressure_median REAL,
          context_pressure_p95 REAL,
          cache_hit_rate REAL,
          tool_yield REAL,
          delegation_overhead REAL,
          field_coverage REAL,
          measurability_json TEXT NOT NULL,
          payload TEXT
        );
        CREATE TABLE IF NOT EXISTS finding (
          id TEXT PRIMARY KEY,
          detector_id TEXT NOT NULL,
          title TEXT NOT NULL,
          mechanism TEXT NOT NULL,
          confidence TEXT NOT NULL,
          estimated_waste_tokens INTEGER,
          recommendation TEXT NOT NULL,
          error_bar_json TEXT NOT NULL,
          evidence_links_json TEXT NOT NULL,
          outcome_risk_caveat TEXT NOT NULL,
          run_id TEXT,
          session_id TEXT,
          rank_score REAL NOT NULL DEFAULT 0.0,
          payload TEXT
        );
        CREATE INDEX IF NOT EXISTS finding_by_run ON finding (run_id);
        CREATE INDEX IF NOT EXISTS finding_by_session ON finding (session_id);
        CREATE INDEX IF NOT EXISTS finding_by_rank_score ON finding (rank_score DESC);
        CREATE INDEX IF NOT EXISTS finding_by_detector ON finding (detector_id);
        CREATE TABLE IF NOT EXISTS prediction (
          id TEXT PRIMARY KEY,
          finding_id TEXT NOT NULL,
          run_id TEXT NOT NULL,
          predicted_waste_tokens INTEGER NOT NULL,
          confidence REAL NOT NULL,
          confidence_tier TEXT,
          error_bar_json TEXT,
          created_at TEXT NOT NULL,
          comparison_run_id TEXT,
          observed_delta_tokens INTEGER,
          calibration_score REAL,
          payload TEXT
        );
        CREATE INDEX IF NOT EXISTS prediction_by_finding ON prediction (finding_id);
        CREATE INDEX IF NOT EXISTS prediction_by_run ON prediction (run_id);
        CREATE INDEX IF NOT EXISTS prediction_by_created_at ON prediction (created_at);
        CREATE TABLE IF NOT EXISTS model_context_window_catalog (
          lookup_id TEXT PRIMARY KEY,
          canonical_model_id TEXT NOT NULL,
          context_window INTEGER NOT NULL CHECK (context_window > 0),
          vendor TEXT NOT NULL,
          documentation_url TEXT NOT NULL,
          retrieved_at TEXT NOT NULL,
          source_revision TEXT
        );
        CREATE INDEX IF NOT EXISTS model_context_window_catalog_by_vendor
          ON model_context_window_catalog (vendor);
        CREATE TABLE IF NOT EXISTS model_context_window_catalog_vendor (
          vendor TEXT PRIMARY KEY,
          status TEXT NOT NULL,
          last_refresh_at TEXT,
          last_error TEXT
        );
        CREATE TABLE IF NOT EXISTS metric_point (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          point_key TEXT NOT NULL UNIQUE,
          name TEXT NOT NULL,
          kind TEXT NOT NULL,
          unit TEXT,
          aggregation_temporality INTEGER,
          time_unix_nano TEXT,
          start_time_unix_nano TEXT,
          timestamp TEXT,
          value REAL,
          point_count INTEGER,
          point_sum REAL,
          attributes_json TEXT NOT NULL,
          resource_json TEXT NOT NULL,
          scope_json TEXT NOT NULL,
          source TEXT
        );
        CREATE INDEX IF NOT EXISTS metric_point_by_name ON metric_point (name);
        """;

    /// <summary>Full schema DDL combining core tables, source state, and refresh run log.</summary>
    public const string SchemaSql = CoreSchemaSql + "\n" + SourceStateSql + "\n" + RefreshRunSql;

    /// <summary>Covering index for session list queries.</summary>
    public const string SessionDenormIndexSql = """
        CREATE INDEX IF NOT EXISTS session_denorm_idx
          ON session (started DESC, session_id, harness, label, is_subagent,
                      parent_session, agent_name, repo, branch, ended,
                      summary_json, problems_count);
        """;

    /// <summary>
    /// Formats the refusal message emitted when opening a canonical store written by a newer build.
    /// </summary>
    public static string SchemaVersionRefusal(
        string path,
        int fromVersion,
        int toVersion = SchemaVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"canon store at {path} was built with schema version {fromVersion}, but this build understands version {toVersion}; upgrade KyberDash");
    }

    /// <summary>
    /// Normalizes a persisted history coverage window according to "absent is not zero":
    /// only positive 32-bit integers are retained; non-positive or null values return <see langword="null"/>.
    /// </summary>
    public static int? NormalizeHistoryWeeks(long? value) =>
        value is > 0 and <= int.MaxValue ? (int)value.Value : null;

    /// <summary>
    /// Normalizes an ingest audit source key: blank inputs fold to <c>"otlp"</c>, and values longer
    /// than <see cref="MaxIngestSourceLength"/> characters are truncated.
    /// </summary>
    public static string NormalizeIngestSource(string? source)
    {
        string trimmed = source?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return "otlp";

        return trimmed.Length > MaxIngestSourceLength
            ? trimmed[..MaxIngestSourceLength]
            : trimmed;
    }
}
