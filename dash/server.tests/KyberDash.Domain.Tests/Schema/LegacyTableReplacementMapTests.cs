using System;
using System.Collections.Generic;
using System.Linq;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Domain.Tests.Schema;

/// <summary>
/// Verification tests ensuring <see cref="LegacyTableReplacementMap"/> maps every legacy v24
/// table to typed replacement tables in <see cref="RelationalSchemaCatalog"/>
/// or an explicit removal rationale.
/// </summary>
public sealed partial class LegacyTableReplacementMapTests
{
    private static readonly string[] ExpectedLegacyTables =
    [
        "records",
        "session",
        "session_turn",
        "run",
        "execution",
        "harness_rollup",
        "finding",
        "prediction",
        "metadata",
        "quarantine",
        "pending_logs",
        "quarantined_logs",
        "enriched_logs",
        "problems",
        "ingest_log",
        "token_cache",
        "model_context_window_catalog",
        "model_context_window_catalog_vendor",
        "metric_point",
        "source_checkpoint",
        "record_provenance",
        "refresh_run",
    ];

    /// <summary>Verifies all 22 legacy v24 tables are uniquely mapped with no extra keys.</summary>
    [Fact]
    public void ReplacementMap_CoversAllTwentyTwoV24TablesWithoutGapsOrExtras()
    {
        Assert.Equal(22, ExpectedLegacyTables.Length);

        Assert.Equal(22, LegacyTableReplacementMap.Entries.Count);

        HashSet<string> mappedNames = new(StringComparer.Ordinal);
        foreach (LegacyTableReplacementEntry entry in LegacyTableReplacementMap.Entries)
        {
            Assert.True(mappedNames.Add(entry.LegacyTableName), $"Duplicate legacy table mapping '{entry.LegacyTableName}'.");
            Assert.True(LegacyTableReplacementMap.TryGetEntry(entry.LegacyTableName, out LegacyTableReplacementEntry? resolved));
            Assert.Same(entry, resolved);
        }

        foreach (string legacyTable in ExpectedLegacyTables)
        {
            Assert.True(mappedNames.Contains(legacyTable), $"Legacy table '{legacyTable}' is missing from LegacyTableReplacementMap.");
        }
    }

    /// <summary>Verifies every replacement target exists in <see cref="RelationalSchemaCatalog"/> and every removed table declares a rationale.</summary>
    [Fact]
    public void ReplacementMap_TargetsExistInCatalogAndRemovalsExplainReason()
    {
        HashSet<string> coveredCatalogTables = new(StringComparer.Ordinal);

        foreach (LegacyTableReplacementEntry entry in LegacyTableReplacementMap.Entries)
        {
            if (entry.Disposition == LegacyTableDisposition.Removed)
            {
                Assert.Empty(entry.ReplacementTables);
                Assert.False(string.IsNullOrWhiteSpace(entry.RemovedDetails), $"Removed legacy table '{entry.LegacyTableName}' must document its removal reason.");
                continue;
            }

            Assert.Equal(LegacyTableDisposition.Replaced, entry.Disposition);
            Assert.NotEmpty(entry.ReplacementTables);

            HashSet<string> perEntryTargets = new(StringComparer.Ordinal);
            foreach (string targetTable in entry.ReplacementTables)
            {
                Assert.True(perEntryTargets.Add(targetTable), $"Legacy table '{entry.LegacyTableName}' repeats target '{targetTable}'.");
                Assert.True(
                    RelationalSchemaCatalog.TryGetTable(targetTable, out _),
                    $"Legacy table '{entry.LegacyTableName}' maps to unknown catalog table '{targetTable}'.");
                _ = coveredCatalogTables.Add(targetTable);
            }
        }

        List<string> uncovered = [];
        foreach (TableDefinition catalogTable in RelationalSchemaCatalog.Tables)
        {
            if (!coveredCatalogTables.Contains(catalogTable.Name))
                uncovered.Add(catalogTable.Name);
        }

        Assert.True(uncovered.Count == 0, $"RelationalSchemaCatalog tables not linked from LegacyTableReplacementMap: {string.Join(", ", uncovered)}.");
    }

    /// <summary>Verifies explicit dispositions and key target groups for v24 tables and DD-10 log removals.</summary>
    [Fact]
    public void ReplacementMap_RecordsExpectedTargetFamiliesAndRemovedLogs()
    {
        LegacyTableReplacementEntry records = LegacyTableReplacementMap.GetEntry("records");
        Assert.Equal(LegacyTableDisposition.Replaced, records.Disposition);
        Assert.Contains("record", records.ReplacementTables);
        Assert.Contains("content_part", records.ReplacementTables);
        Assert.Contains("content_part_withheld", records.ReplacementTables);
        Assert.Contains("tool_definition", records.ReplacementTables);
        Assert.Contains("tool_call", records.ReplacementTables);
        Assert.Contains("agent_invocation", records.ReplacementTables);
        Assert.Contains("RQ-4", records.RemovedDetails ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("OD-6", records.RemovedDetails ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("RQ-3", records.RemovedDetails ?? string.Empty, StringComparison.Ordinal);

        LegacyTableReplacementEntry session = LegacyTableReplacementMap.GetEntry("session");
        Assert.Equal(LegacyTableDisposition.Replaced, session.Disposition);
        Assert.Contains("session", session.ReplacementTables);
        Assert.Contains("session_workspace", session.ReplacementTables);
        Assert.Contains("session_projection", session.ReplacementTables);
        Assert.Contains("session_context", session.ReplacementTables);
        Assert.Contains("session_context_unmeasurable", session.ReplacementTables);
        Assert.Contains("session_schema", session.ReplacementTables);
        Assert.Contains("session_schema_unmeasurable", session.ReplacementTables);
        Assert.Contains("session_timeline_subagent", session.ReplacementTables);
        Assert.Contains("DD-13", session.RemovedDetails ?? string.Empty, StringComparison.Ordinal);

        LegacyTableReplacementEntry sessionTurn = LegacyTableReplacementMap.GetEntry("session_turn");
        Assert.Equal(["session_turn"], sessionTurn.ReplacementTables);

        LegacyTableReplacementEntry metadata = LegacyTableReplacementMap.GetEntry("metadata");
        Assert.Equal(15, metadata.ReplacementTables.Count);
        Assert.Contains("store_state", metadata.ReplacementTables);
        Assert.Contains("operator_setting_dashboard", metadata.ReplacementTables);

        string[] removedLogs = ["pending_logs", "quarantined_logs", "enriched_logs"];
        foreach (string removedLog in removedLogs)
        {
            LegacyTableReplacementEntry logEntry = LegacyTableReplacementMap.GetEntry(removedLog);
            Assert.Equal(LegacyTableDisposition.Removed, logEntry.Disposition);
            Assert.Empty(logEntry.ReplacementTables);
            Assert.Contains("DD-10", logEntry.RemovedDetails ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies unknown legacy table lookup raises <see cref="KeyNotFoundException"/> with nearest match hint.</summary>
    [Fact]
    public void ReplacementMap_GetEntryProvidesNearestMatchHintForUnknownTable()
    {
        KeyNotFoundException error = Assert.Throws<KeyNotFoundException>(() =>
            LegacyTableReplacementMap.GetEntry("recordz"));
        Assert.Contains("records", error.Message, StringComparison.Ordinal);

        Assert.False(LegacyTableReplacementMap.TryGetEntry(string.Empty, out LegacyTableReplacementEntry? missing));
        Assert.Null(missing);
    }
}
