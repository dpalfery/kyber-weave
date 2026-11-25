using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace KyberDash.Domain.Schema;

/// <summary>Disposition of a legacy v24 legacy v24 schema table in the normalized relational schema.</summary>
public enum LegacyTableDisposition
{
    /// <summary>Replaced by one or more normalized tables in <see cref="RelationalSchemaCatalog"/>.</summary>
    Replaced,

    /// <summary>Explicitly removed with no replacement table.</summary>
    Removed,
}

/// <summary>
/// Mapping entry connecting a legacy v24 legacy v24 schema table to its typed replacement tables
/// in <see cref="RelationalSchemaCatalog"/> or documenting its removal rationale.
/// </summary>
/// <param name="LegacyTableName">Legacy v24 SQLite table name.</param>
/// <param name="Disposition">Whether the table is replaced by normalized tables or explicitly removed.</param>
/// <param name="ReplacementTables">Normalized target tables in <see cref="RelationalSchemaCatalog"/>; empty when removed.</param>
/// <param name="RemovedDetails">Documented reasons for removed tables or columns.</param>
public record LegacyTableReplacementEntry(
    string LegacyTableName,
    LegacyTableDisposition Disposition,
    IReadOnlyList<string> ReplacementTables,
    string? RemovedDetails = null);

/// <summary>
/// Authoritative mapping from all 22 legacy v24 legacy v24 schema tables to their
/// normalized replacement tables in <see cref="RelationalSchemaCatalog"/> or documented removals.
/// </summary>
public static class LegacyTableReplacementMap
{
    private static readonly IReadOnlyList<LegacyTableReplacementEntry> MapEntries = BuildAndValidate();

    private static readonly IReadOnlyDictionary<string, LegacyTableReplacementEntry> MapByLegacyName =
        new ReadOnlyDictionary<string, LegacyTableReplacementEntry>(
            MapEntries.ToDictionary(static entry => entry.LegacyTableName, StringComparer.Ordinal));

    /// <summary>All 22 v24 legacy v24 schema table replacement entries in declaration order.</summary>
    public static IReadOnlyList<LegacyTableReplacementEntry> Entries => MapEntries;

    /// <summary>Lookup table keyed by legacy v24 table name.</summary>
    public static IReadOnlyDictionary<string, LegacyTableReplacementEntry> EntriesByLegacyTable => MapByLegacyName;

    /// <summary>Returns the replacement mapping for <paramref name="legacyTableName"/>.</summary>
    /// <param name="legacyTableName">Exact v24 legacy table name.</param>
    /// <returns>The matching replacement entry.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="legacyTableName"/> is unknown, carrying the nearest match hint.</exception>
    public static LegacyTableReplacementEntry GetEntry(string legacyTableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyTableName);
        if (MapByLegacyName.TryGetValue(legacyTableName, out LegacyTableReplacementEntry? entry))
            return entry;

        string? nearest = RelationalSchemaCatalog.FindNearestName(
            legacyTableName,
            MapEntries.Select(static e => e.LegacyTableName));
        string hint = nearest is null
            ? "Inspect LegacyTableReplacementMap.Entries for known v24 table names."
            : $"Did you mean '{nearest}'?";
        throw new KeyNotFoundException($"Unknown legacy v24 table '{legacyTableName}'. {hint}");
    }

    /// <summary>Attempts to resolve the replacement mapping for <paramref name="legacyTableName"/>.</summary>
    /// <param name="legacyTableName">Legacy v24 table name to locate.</param>
    /// <param name="entry">Resolved entry when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if found; otherwise <see langword="false"/>.</returns>
    public static bool TryGetEntry(string legacyTableName, out LegacyTableReplacementEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(legacyTableName))
        {
            entry = null;
            return false;
        }

        return MapByLegacyName.TryGetValue(legacyTableName, out entry);
    }

    private static IReadOnlyList<LegacyTableReplacementEntry> BuildAndValidate()
    {
        LegacyTableReplacementEntry[] entries =
        [
            Replaced(
                "records",
                [
                    "harness",
                    "source",
                    "record",
                    "record_session_link",
                    "record_parent",
                    "record_trace",
                    "record_model",
                    "record_response_id",
                    "record_request_id",
                    "record_end",
                    "record_reported_duration",
                    "record_token_count",
                    "record_reported_total",
                    "record_reported_cost",
                    "record_skill_invocation",
                    "record_agent_instance",
                    "record_error",
                    "record_edit_failure",
                    "record_correction",
                    "record_correction_rule",
                    "record_declared_context_window",
                    "content_part",
                    "content_part_text",
                    "content_part_withheld",
                    "content_part_reported_tokens",
                    "content_part_server",
                    "content_part_role",
                    "content_part_residency",
                    "content_part_truncation",
                    "tool_definition",
                    "tool_definition_server",
                    "tool_call",
                    "tool_call_mcp",
                    "tool_call_input_size",
                    "tool_call_input_fingerprint",
                    "tool_call_input_text",
                    "tool_call_input_truncation",
                    "tool_call_result_size",
                    "tool_call_result_text",
                    "tool_call_result_truncation",
                    "tool_call_outcome",
                    "tool_call_error",
                    "tool_call_error_code",
                    "tool_call_exit_code",
                    "agent_invocation",
                    "agent_invocation_spawn",
                    "agent_invocation_outcome",
                    "agent_invocation_error",
                ],
                "measurability_json dropped per RQ-4; raw dropped per OD-6; priced cost computed at read time per RQ-3."),
            Replaced(
                "session",
                [
                    "session",
                    "session_workspace",
                    "session_repository",
                    "session_branch",
                    "session_title",
                    "session_model_usage",
                    "session_projection",
                    "session_projection_parent",
                    "session_projection_agent",
                    "session_projection_included_in_parent",
                    "session_request",
                    "session_context",
                    "session_context_unmeasurable",
                    "session_context_observed_part",
                    "session_context_derived_count",
                    "session_context_flagged_turn",
                    "session_context_turn",
                    "session_context_turn_headroom",
                    "session_context_turn_accumulation",
                    "session_context_turn_bucket",
                    "session_context_turn_server_definitions",
                    "session_context_turn_stripped_instructions",
                    "session_context_turn_fresh_jump",
                    "session_context_turn_non_resident",
                    "session_schema",
                    "session_schema_unmeasurable",
                    "session_schema_ranked_tool",
                    "session_schema_ranked_tool_server",
                    "session_schema_server",
                    "session_schema_unused",
                    "session_timeline_subagent",
                    "session_timeline_auxiliary",
                ],
                "synced_at, sync_status, settled_at, transcript_path, and transcript_offset removed as unused; problems_count and payload aggregates computed at read time per DD-13."),
            Replaced(
                "session_turn",
                ["session_turn"],
                "turn_json removed; turn payload computed at read time from record rows per DD-13."),
            Replaced(
                "run",
                ["run", "run_label", "run_working_directory", "run_bounds"],
                "payload removed because it was always null."),
            Replaced(
                "execution",
                ["execution", "execution_session", "execution_parent", "execution_agent", "execution_bounds"],
                "payload removed because it was always null; parent_linkage_json computed at read time."),
            Replaced(
                "harness_rollup",
                ["harness_rollup", "harness_rollup_metric"],
                "measurability_json dropped per RQ-4; payload, sample_count, and field_coverage computed at read time per DD-13."),
            Replaced(
                "finding",
                [
                    "finding",
                    "finding_estimated_waste",
                    "finding_error_bar",
                    "finding_evidence_link",
                    "finding_run",
                    "finding_session",
                    "finding_measurement_class",
                    "finding_confidence_basis",
                    "finding_what_would_raise_it",
                    "finding_compaction_hazard",
                ]),
            Replaced(
                "prediction",
                ["prediction", "prediction_comparison", "prediction_observed_delta", "prediction_calibration"],
                "error_bar_json and payload removed because they were never set."),
            Replaced(
                "metadata",
                [
                    "store_state",
                    "store_write_marker",
                    "store_clean_stamp",
                    "projection_state",
                    "projection_dirty_range",
                    "projection_dirty_session",
                    "session_key_alias",
                    "content_retention",
                    "content_purge_mark",
                    "context_window_catalog_state",
                    "operator_setting_folder_import",
                    "operator_setting_jobs",
                    "operator_setting_refresh_cadence",
                    "operator_setting_receiver",
                    "operator_setting_dashboard",
                ]),
            Replaced(
                "quarantine",
                [
                    "quarantined_span",
                    "quarantined_span_namespace",
                    "quarantined_span_source",
                    "quarantined_span_name",
                    "quarantined_span_timestamp",
                    "quarantined_span_harness",
                ],
                "raw_payload dropped per OD-6."),
            Removed(
                "pending_logs",
                "DD-10: OTLP logs are mapped on receipt to collector DTOs or counted in ingest_problem with no raw payload kept."),
            Removed(
                "quarantined_logs",
                "DD-10: OTLP logs are mapped on receipt to collector DTOs or counted in ingest_problem with no raw payload kept."),
            Removed(
                "enriched_logs",
                "DD-10: OTLP logs are mapped on receipt to collector DTOs or counted in ingest_problem with no raw payload kept."),
            Replaced(
                "problems",
                [
                    "ingest_problem",
                    "ingest_problem_span",
                    "ingest_problem_location",
                    "ingest_problem_session",
                    "ingest_problem_harness",
                    "ingest_problem_timestamp",
                    "ingest_problem_stack",
                ]),
            Replaced("ingest_log", ["ingest_log"]),
            Replaced("token_cache", ["token_cache", "token_cache_model"]),
            Replaced("model_context_window_catalog", ["model_context_window_catalog", "model_context_window_source_revision"]),
            Replaced(
                "model_context_window_catalog_vendor",
                [
                    "model_context_window_catalog_vendor",
                    "model_context_window_catalog_vendor_refresh",
                    "model_context_window_catalog_vendor_error",
                ]),
            Replaced(
                "metric_point",
                [
                    "metric_point",
                    "metric_point_unit",
                    "metric_point_temporality",
                    "metric_point_time",
                    "metric_point_start_time",
                    "metric_point_timestamp",
                    "metric_point_value",
                    "metric_point_count",
                    "metric_point_sum",
                    "metric_point_source",
                ],
                "attributes_json, resource_json, and scope_json removed because they were written and never read."),
            Replaced("source_checkpoint", ["source_checkpoint", "source_checkpoint_success", "source_checkpoint_error"]),
            Replaced("record_provenance", ["record_provenance", "record_provenance_native_session", "record_provenance_native_record"]),
            Replaced("refresh_run", ["refresh_run", "refresh_run_completion", "refresh_run_summary", "refresh_run_history_window"]),
        ];

        Validate(entries);
        return Array.AsReadOnly(entries);
    }

    private static LegacyTableReplacementEntry Replaced(
        string legacyTableName,
        IReadOnlyList<string> replacementTables,
        string? removedDetails = null) =>
        new(legacyTableName, LegacyTableDisposition.Replaced, replacementTables, removedDetails);

    private static LegacyTableReplacementEntry Removed(string legacyTableName, string reason) =>
        new(legacyTableName, LegacyTableDisposition.Removed, [], reason);

    private static void Validate(IReadOnlyList<LegacyTableReplacementEntry> entries)
    {
        HashSet<string> seenLegacy = new(StringComparer.Ordinal);
        foreach (LegacyTableReplacementEntry entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.LegacyTableName))
                throw new InvalidOperationException("Legacy table replacement entry must declare a non-empty legacy table name.");

            if (!seenLegacy.Add(entry.LegacyTableName))
                throw new InvalidOperationException($"Duplicate legacy table replacement mapping '{entry.LegacyTableName}'.");

            if (entry.Disposition == LegacyTableDisposition.Removed)
            {
                if (entry.ReplacementTables.Count > 0)
                    throw new InvalidOperationException($"Removed legacy table '{entry.LegacyTableName}' must not list replacement tables.");

                if (string.IsNullOrWhiteSpace(entry.RemovedDetails))
                    throw new InvalidOperationException($"Removed legacy table '{entry.LegacyTableName}' must explain its removal reason.");

                continue;
            }

            if (entry.ReplacementTables.Count == 0)
                throw new InvalidOperationException($"Replaced legacy table '{entry.LegacyTableName}' must list at least one replacement table.");

            foreach (string target in entry.ReplacementTables)
            {
                if (!RelationalSchemaCatalog.TablesByName.ContainsKey(target))
                {
                    throw new InvalidOperationException(
                        $"Legacy table '{entry.LegacyTableName}' references unknown catalog table '{target}'.");
                }
            }
        }
    }
}
