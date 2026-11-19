using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Contracts.Repositories;

/// <summary>Persisted schema and detector version state (<c>store_state</c>).</summary>
/// <param name="SchemaVersion">Relational schema version.</param>
/// <param name="DetectorVersion">Finding detector version.</param>
public record StoreStateView(int SchemaVersion, int DetectorVersion);

/// <summary>Persisted projection watermark state (<c>projection_state</c>).</summary>
/// <param name="ProjectionSchemaVersion">Projection schema version.</param>
/// <param name="RecordsMarker">Records write marker covered by the projection.</param>
public record ProjectionStateView(int ProjectionSchemaVersion, long RecordsMarker);

/// <summary>Session key alias mapping (<c>session_key_alias</c>).</summary>
/// <param name="AliasKey">Alias session or trace key.</param>
/// <param name="SessionId">Canonical session identifier.</param>
public record SessionKeyAlias(string AliasKey, string SessionId);

/// <summary>Persisted ingest problem with its database row identifier (<c>ingest_problem*</c>).</summary>
/// <param name="ProblemId">Database primary key (<c>ingest_problem.problem_id</c>).</param>
/// <param name="Problem">Static problem payload.</param>
public record StoredIngestProblem(long ProblemId, CollectedProblem Problem);

/// <summary>Normalized quarantined span (<c>quarantined_span*</c>).</summary>
/// <param name="SpanId">Span identifier.</param>
/// <param name="Reason">Quarantine reason.</param>
/// <param name="ObservedNamespaces">Observed attribute namespaces (<c>quarantined_span_namespace</c>).</param>
/// <param name="Source">Optional source locator (<c>quarantined_span_source</c>).</param>
/// <param name="Name">Optional span name (<c>quarantined_span_name</c>).</param>
/// <param name="TimestampUtc">Optional UTC timestamp (<c>quarantined_span_timestamp</c>).</param>
/// <param name="HarnessId">Optional attributed harness (<c>quarantined_span_harness</c>).</param>
public record StoredQuarantinedSpan(
    string SpanId,
    string Reason,
    IReadOnlyList<string> ObservedNamespaces,
    string? Source = null,
    string? Name = null,
    string? TimestampUtc = null,
    string? HarnessId = null);

/// <summary>Token cache entry (<c>token_cache</c> and <c>token_cache_model</c>).</summary>
/// <param name="Hash">Content+model SHA-256 hash.</param>
/// <param name="TokenCount">Measured token count.</param>
/// <param name="ModelId">Optional model identifier.</param>
public record StoredTokenCacheEntry(string Hash, long TokenCount, string? ModelId = null);

/// <summary>Model context window catalog entry (<c>model_context_window_catalog</c> and <c>model_context_window_source_revision</c>).</summary>
/// <param name="LookupId">Catalog lookup identifier.</param>
/// <param name="CanonicalModelId">Canonical model identifier.</param>
/// <param name="ContextWindow">Context window token capacity.</param>
/// <param name="Vendor">Vendor identifier.</param>
/// <param name="DocumentationUrl">Vendor documentation URI.</param>
/// <param name="RetrievedAtUtc">UTC retrieval timestamp.</param>
/// <param name="SourceRevision">Optional source revision (<c>model_context_window_source_revision</c>).</param>
public record StoredModelContextWindowEntry(
    string LookupId,
    string CanonicalModelId,
    int ContextWindow,
    string Vendor,
    Uri DocumentationUrl,
    string RetrievedAtUtc,
    string? SourceRevision = null);

/// <summary>Model context window catalog vendor refresh status (<c>model_context_window_catalog_vendor*</c>).</summary>
/// <param name="Vendor">Vendor identifier.</param>
/// <param name="Status">Refresh status.</param>
/// <param name="LastRefreshAtUtc">Optional last refresh UTC timestamp.</param>
/// <param name="LastError">Optional last error message.</param>
public record StoredModelCatalogVendorStatus(
    string Vendor,
    string Status,
    string? LastRefreshAtUtc = null,
    string? LastError = null);

/// <summary>Complete snapshot of the model context window catalog and vendor statuses.</summary>
/// <param name="Status">Overall catalog state (<c>context_window_catalog_state.status</c>).</param>
/// <param name="Vendors">Per-vendor status entries.</param>
/// <param name="Entries">Catalog model entries.</param>
public record StoredModelCatalogSnapshot(
    string Status,
    IReadOnlyList<StoredModelCatalogVendorStatus> Vendors,
    IReadOnlyList<StoredModelContextWindowEntry> Entries);

/// <summary>Normalized OTLP metric point (<c>metric_point*</c>).</summary>
/// <param name="PointKey">Idempotent point key.</param>
/// <param name="Name">Metric name.</param>
/// <param name="Kind">Metric kind.</param>
/// <param name="Unit">Optional unit (<c>metric_point_unit</c>).</param>
/// <param name="AggregationTemporality">Optional temporality (<c>metric_point_temporality</c>).</param>
/// <param name="TimeUnixNano">Optional point timestamp in nanoseconds (<c>metric_point_time</c>).</param>
/// <param name="StartTimeUnixNano">Optional start timestamp in nanoseconds (<c>metric_point_start_time</c>).</param>
/// <param name="TimestampUtc">Optional ISO-8601 UTC timestamp (<c>metric_point_timestamp</c>).</param>
/// <param name="Value">Optional scalar metric value (<c>metric_point_value</c>).</param>
/// <param name="PointCount">Optional observation count (<c>metric_point_count</c>).</param>
/// <param name="PointSum">Optional observation sum (<c>metric_point_sum</c>).</param>
/// <param name="Source">Optional metric source (<c>metric_point_source</c>).</param>
public record StoredMetricPoint(
    string PointKey,
    string Name,
    string Kind,
    string? Unit = null,
    int? AggregationTemporality = null,
    string? TimeUnixNano = null,
    string? StartTimeUnixNano = null,
    string? TimestampUtc = null,
    double? Value = null,
    long? PointCount = null,
    double? PointSum = null,
    string? Source = null);

/// <summary>
/// Typed persistence contract for operational state, settings, checkpoints, diagnostics,
/// quarantine, token cache, model context-window catalog, and metric points.
/// </summary>
public interface IOperationalStore
{
    /// <summary>Reads typed operator settings (<c>operator_setting_*</c>).</summary>
    SettingsView ReadSettings();

    /// <summary>Persists typed operator settings (<c>operator_setting_*</c>).</summary>
    /// <param name="settings">Validated operator settings.</param>
    void SaveSettings(SettingsView settings);

    /// <summary>Reads schema and detector versions (<c>store_state</c>).</summary>
    StoreStateView? GetStoreState();

    /// <summary>Persists schema and detector versions (<c>store_state</c>).</summary>
    /// <param name="state">Store version state.</param>
    void SetStoreState(StoreStateView state);

    /// <summary>Reads the monotonic records write sequence (<c>store_write_marker</c>).</summary>
    long GetWriteMarker();

    /// <summary>Persists the monotonic records write sequence (<c>store_write_marker</c>).</summary>
    /// <param name="recordsWriteSeq">Write sequence value.</param>
    void SetWriteMarker(long recordsWriteSeq);

    /// <summary>Reads the latest store wipe/clean timestamp (<c>store_clean_stamp</c>).</summary>
    string? GetLastCleanAtUtc();

    /// <summary>Persists the latest store wipe/clean timestamp (<c>store_clean_stamp</c>).</summary>
    /// <param name="lastCleanAtUtc">ISO-8601 UTC timestamp.</param>
    void SetLastCleanAtUtc(string lastCleanAtUtc);

    /// <summary>Reads content retention days (<c>content_retention</c>).</summary>
    int? GetContentRetentionDays();

    /// <summary>Persists content retention days (<c>content_retention</c>).</summary>
    /// <param name="retentionDays">Positive retention day count.</param>
    void SetContentRetentionDays(int retentionDays);

    /// <summary>Reads the latest content purge cutoff timestamp (<c>content_purge_mark</c>).</summary>
    string? GetContentPurgeMarkUtc();

    /// <summary>Persists the latest content purge cutoff timestamp (<c>content_purge_mark</c>).</summary>
    /// <param name="purgedThroughUtc">ISO-8601 UTC cutoff timestamp.</param>
    void SetContentPurgeMarkUtc(string purgedThroughUtc);

    /// <summary>Reads projection version and watermark (<c>projection_state</c>).</summary>
    ProjectionStateView? GetProjectionState();

    /// <summary>Persists projection version and watermark (<c>projection_state</c>).</summary>
    /// <param name="state">Projection state.</param>
    void SetProjectionState(ProjectionStateView state);

    /// <summary>Reads dirty projection range and session keys (<c>projection_dirty_range</c>, <c>projection_dirty_session</c>).</summary>
    DirtyProjectionState? GetProjectionDirtyState();

    /// <summary>Persists dirty projection range and session keys (<c>projection_dirty_range</c>, <c>projection_dirty_session</c>).</summary>
    /// <param name="state">Dirty projection state.</param>
    void SetProjectionDirtyState(DirtyProjectionState state);

    /// <summary>Clears dirty projection range and session keys.</summary>
    void ClearProjectionDirtyState();

    /// <summary>Lists session key alias mappings (<c>session_key_alias</c>).</summary>
    /// <param name="sessionId">Optional canonical session identifier filter.</param>
    IReadOnlyList<SessionKeyAlias> ListSessionKeyAliases(string? sessionId = null);

    /// <summary>Replaces session key alias mappings (<c>session_key_alias</c>).</summary>
    /// <param name="aliases">Alias mappings to persist.</param>
    void ReplaceSessionKeyAliases(IEnumerable<SessionKeyAlias> aliases);

    /// <summary>Inserts or updates a refresh run audit record (<c>refresh_run*</c>).</summary>
    /// <param name="row">Refresh run audit row.</param>
    void UpsertRefreshRun(RefreshRunRow row);

    /// <summary>Gets one refresh run audit record by <paramref name="refreshRunId"/>.</summary>
    /// <param name="refreshRunId">Refresh run identifier.</param>
    RefreshRunRow? GetRefreshRun(string refreshRunId);

    /// <summary>Lists recent refresh run audit records (<c>refresh_run*</c>).</summary>
    /// <param name="limit">Maximum rows to return.</param>
    IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20);

    /// <summary>Gets one source coverage checkpoint (<c>source_checkpoint*</c>).</summary>
    /// <param name="harnessId">Harness identifier.</param>
    /// <param name="sourceKey">Source key within the harness.</param>
    SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey);

    /// <summary>Inserts or updates a source coverage checkpoint (<c>source_checkpoint*</c>).</summary>
    /// <param name="checkpoint">Source checkpoint to persist.</param>
    void UpsertSourceCheckpoint(SourceCheckpoint checkpoint);

    /// <summary>Lists source coverage checkpoints optionally filtered by <paramref name="harnessId"/>.</summary>
    /// <param name="harnessId">Optional harness filter.</param>
    IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null);

    /// <summary>Gets one stored ingest problem by numeric <paramref name="problemId"/>.</summary>
    /// <param name="problemId">Database row identifier.</param>
    StoredIngestProblem? GetIngestProblem(long problemId);

    /// <summary>Lists stored ingest problems (<c>ingest_problem*</c>).</summary>
    /// <param name="harnessId">Optional harness filter.</param>
    /// <param name="spanId">Optional span identifier filter.</param>
    /// <param name="limit">Optional page size.</param>
    /// <param name="offset">Optional page offset.</param>
    IReadOnlyList<StoredIngestProblem> ListIngestProblems(
        string? harnessId = null,
        string? spanId = null,
        int? limit = null,
        int? offset = null);

    /// <summary>Counts stored ingest problems optionally filtered by <paramref name="harnessId"/>.</summary>
    /// <param name="harnessId">Optional harness filter.</param>
    int CountIngestProblems(string? harnessId = null);

    /// <summary>Persists a quarantined span (<c>quarantined_span*</c>).</summary>
    /// <param name="span">Quarantined span entry.</param>
    void PutQuarantinedSpan(StoredQuarantinedSpan span);

    /// <summary>Gets one quarantined span by <paramref name="spanId"/>.</summary>
    /// <param name="spanId">Span identifier.</param>
    StoredQuarantinedSpan? GetQuarantinedSpan(string spanId);

    /// <summary>Lists quarantined spans (<c>quarantined_span*</c>).</summary>
    /// <param name="harnessId">Optional harness filter.</param>
    /// <param name="limit">Optional page size.</param>
    /// <param name="offset">Optional page offset.</param>
    IReadOnlyList<StoredQuarantinedSpan> ListQuarantinedSpans(
        string? harnessId = null,
        int? limit = null,
        int? offset = null);

    /// <summary>Counts quarantined spans optionally filtered by <paramref name="harnessId"/>.</summary>
    /// <param name="harnessId">Optional harness filter.</param>
    int CountQuarantinedSpans(string? harnessId = null);

    /// <summary>Looks up a cached token count by content+model SHA-256 hash (<c>token_cache</c>).</summary>
    /// <param name="hash">Content+model SHA-256 digest.</param>
    long? GetCachedTokenCount(string hash);

    /// <summary>Persists computed token counts (<c>token_cache</c> and <c>token_cache_model</c>).</summary>
    /// <param name="entries">Token cache entries to persist.</param>
    void PutCachedTokenCounts(IEnumerable<StoredTokenCacheEntry> entries);

    /// <summary>Reads the model context window catalog snapshot (<c>model_context_window_catalog*</c>).</summary>
    StoredModelCatalogSnapshot GetModelContextWindowCatalog();

    /// <summary>Replaces the model context window catalog snapshot (<c>model_context_window_catalog*</c>).</summary>
    /// <param name="snapshot">Catalog snapshot to persist.</param>
    void ReplaceModelContextWindowCatalog(StoredModelCatalogSnapshot snapshot);

    /// <summary>Persists normalized OTLP metric points (<c>metric_point*</c>).</summary>
    /// <param name="points">Metric points to persist.</param>
    void PutMetricPoints(IEnumerable<StoredMetricPoint> points);

    /// <summary>Lists persisted OTLP metric points (<c>metric_point*</c>).</summary>
    /// <param name="name">Optional metric name filter.</param>
    /// <param name="limit">Optional maximum rows to return.</param>
    IReadOnlyList<StoredMetricPoint> ListMetricPoints(string? name = null, int? limit = null);

    /// <summary>Records an ingest audit entry (<c>ingest_log</c>).</summary>
    /// <param name="source">Ingest source locator.</param>
    /// <param name="count">Number of records ingested.</param>
    /// <param name="timestampUtc">ISO-8601 UTC timestamp.</param>
    void RecordIngest(string source, int count, string timestampUtc);

    /// <summary>Lists ingest audit entries (<c>ingest_log</c>).</summary>
    /// <param name="source">Optional source filter.</param>
    IReadOnlyList<IngestLogEntry> ListIngestLog(string? source = null);

    /// <summary>Persists record provenance entries (<c>record_provenance*</c>).</summary>
    /// <param name="rows">Provenance rows to persist.</param>
    void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows);

    /// <summary>Lists record provenance entries for one source unit (<c>record_provenance*</c>).</summary>
    /// <param name="harnessId">Harness identifier.</param>
    /// <param name="sourceKey">Source key within the harness.</param>
    IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey);
}
