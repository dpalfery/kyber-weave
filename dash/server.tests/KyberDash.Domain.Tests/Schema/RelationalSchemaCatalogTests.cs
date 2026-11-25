using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Domain.Tests.Schema;

/// <summary>
/// Structural and DDL invariants for <see cref="RelationalSchemaCatalog"/> and
/// <see cref="SchemaDdlGenerator"/> covering the complete reconciled relational schema.
/// </summary>
public sealed partial class RelationalSchemaCatalogTests
{
    private static readonly string[] ExpectedCoreTables =
    [
        "harness",
        "source",
        "record_source_claim",
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
        "record_skill_listing",
        "record_agent_instance",
        "record_error",
        "record_edit_failure",
        "record_correction",
        "record_correction_rule",
        "record_declared_context_window",
        "content_part",
        "content_part_text",
        "content_part_withheld",
        "content_part_withheld_tokenizer",
        "content_part_reported_tokens",
        "content_part_server",
        "content_part_role",
        "content_part_residency",
        "content_part_truncation",
        "tool_definition",
        "tool_definition_tokenizer",
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
        "session_turn",
        "session_context",
        "session_context_unmeasurable",
        "session_context_observed_part",
        "session_context_derived_count",
        "session_context_flagged_turn",
        "session_context_turn",
        "session_context_turn_tokenizer",
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
        "run",
        "run_label",
        "run_working_directory",
        "run_bounds",
        "execution",
        "execution_session",
        "execution_parent",
        "execution_agent",
        "execution_bounds",
        "harness_rollup",
        "harness_rollup_metric",
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
        "prediction",
        "prediction_comparison",
        "prediction_observed_delta",
        "prediction_calibration",
        "store_state",
        "store_write_marker",
        "store_clean_stamp",
        "projection_state",
        "projection_dirty_range",
        "content_retention",
        "content_purge_mark",
        "context_window_catalog_state",
        "operator_setting_folder_import",
        "operator_setting_jobs",
        "operator_setting_refresh_cadence",
        "operator_setting_receiver",
        "operator_setting_dashboard",
        "projection_dirty_session",
        "session_key_alias",
        "quarantined_span",
        "quarantined_span_namespace",
        "quarantined_span_source",
        "quarantined_span_name",
        "quarantined_span_timestamp",
        "quarantined_span_harness",
        "ingest_problem",
        "ingest_problem_span",
        "ingest_problem_location",
        "ingest_problem_session",
        "ingest_problem_harness",
        "ingest_problem_timestamp",
        "ingest_problem_stack",
        "ingest_log",
        "token_cache",
        "token_cache_model",
        "model_context_window_catalog",
        "model_context_window_source_revision",
        "model_context_window_catalog_vendor",
        "model_context_window_catalog_vendor_refresh",
        "model_context_window_catalog_vendor_error",
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
        "source_checkpoint",
        "source_checkpoint_success",
        "source_checkpoint_error",
        "record_provenance",
        "record_provenance_native_session",
        "record_provenance_native_record",
        "refresh_run",
        "refresh_run_completion",
        "refresh_run_summary",
        "refresh_run_history_window",
    ];

    private static readonly HashSet<string> DeclaredRootTables = new(StringComparer.Ordinal)
    {
        "harness",
        "finding",
        "prediction",
        "store_state",
        "store_write_marker",
        "store_clean_stamp",
        "projection_state",
        "projection_dirty_range",
        "content_retention",
        "content_purge_mark",
        "context_window_catalog_state",
        "operator_setting_folder_import",
        "operator_setting_jobs",
        "operator_setting_refresh_cadence",
        "operator_setting_receiver",
        "operator_setting_dashboard",
        "projection_dirty_session",
        "session_key_alias",
        "quarantined_span",
        "ingest_problem",
        "ingest_log",
        "token_cache",
        "model_context_window_catalog",
        "model_context_window_catalog_vendor",
        "metric_point",
        "source_checkpoint",
        "refresh_run",
    };

    private static readonly HashSet<string> NonRepeatedCompositeKeyTables = new(StringComparer.Ordinal)
    {
        "record_source_claim",
        "record_token_count",
        "record_reported_total",
        "record_skill_invocation",
        "session_model_usage",
        "session_context_observed_part",
        "session_context_derived_count",
        "session_context_flagged_turn",
        "session_context_turn_bucket",
        "session_context_turn_server_definitions",
        "session_schema_server",
        "session_timeline_subagent",
        "session_timeline_auxiliary",
        "harness_rollup_metric",
        "quarantined_span_namespace",
        "source_checkpoint",
        "source_checkpoint_success",
        "source_checkpoint_error",
    };

    private static readonly string[] SingletonRootTables =
    [
        "store_state",
        "store_write_marker",
        "store_clean_stamp",
        "projection_state",
        "projection_dirty_range",
        "content_retention",
        "content_purge_mark",
        "context_window_catalog_state",
        "operator_setting_folder_import",
        "operator_setting_jobs",
        "operator_setting_refresh_cadence",
        "operator_setting_receiver",
        "operator_setting_dashboard",
    ];

    private static readonly HashSet<string> EavKeyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "key",
        "attr_key",
        "attribute_key",
        "attribute_name",
        "property_name",
        "field_name",
        "tag_key",
    };

    private static readonly HashSet<string> EavValueColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "value",
        "attr_value",
        "attribute_value",
        "property_value",
        "field_value",
        "tag_value",
    };

    [GeneratedRegex(@"CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+(?<name>[a-z][a-z0-9_]*)\s*\((?<body>.*?)\);", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTableRegex();

    [GeneratedRegex(@"\bBLOB\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlobTokenRegex();

    [Fact]
    public void Catalog_DefinesEveryRequiredRecordSessionAndTurnTable()
    {
        Assert.NotEmpty(RelationalSchemaCatalog.Tables);

        HashSet<string> tableNames = new(StringComparer.Ordinal);
        foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
        {
            Assert.True(tableNames.Add(table.Name), $"Duplicate table '{table.Name}' in RelationalSchemaCatalog.");
            Assert.True(RelationalSchemaCatalog.TryGetTable(table.Name, out TableDefinition? resolved));
            Assert.Same(table, resolved);
        }

        List<string> missing = [];
        foreach (string expected in ExpectedCoreTables)
        {
            if (!tableNames.Contains(expected))
                missing.Add(expected);
        }

        HashSet<string> expectedSet = new(ExpectedCoreTables, StringComparer.Ordinal);
        List<string> unexpected = [];
        foreach (string actual in tableNames)
        {
            if (!expectedSet.Contains(actual))
                unexpected.Add(actual);
        }

        Assert.True(missing.Count == 0, $"Catalog is missing required tables: {string.Join(", ", missing)}.");
        Assert.True(unexpected.Count == 0, $"Catalog contains unexpected tables: {string.Join(", ", unexpected)}.");
    }

    [Fact]
    public void Catalog_EveryColumnIsNotNullAndUsesOnlyIntegerRealOrText()
    {
        foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
        {
            Assert.NotEmpty(table.Columns);
            Assert.NotEmpty(table.PrimaryKeyColumns);

            foreach (ColumnDefinition column in table.Columns)
            {
                Assert.True(
                    column.IsNotNull,
                    $"Table '{table.Name}' column '{column.Name}' must be NOT NULL.");
                Assert.True(
                    column.Type is SqliteColumnType.SqliteInteger or SqliteColumnType.SqliteReal or SqliteColumnType.SqliteText,
                    $"Table '{table.Name}' column '{column.Name}' uses forbidden type '{column.Type}'.");
            }

            string ddl = SchemaDdlGenerator.GenerateTableDdl(table);
            Match tableMatch = CreateTableRegex().Match(ddl);
            Assert.True(tableMatch.Success, $"Generated DDL for '{table.Name}' did not match CREATE TABLE syntax.");
            Assert.False(BlobTokenRegex().IsMatch(ddl), $"Generated DDL for '{table.Name}' contains BLOB:\n{ddl}");

            string[] bodyLines = tableMatch.Groups["body"].Value.Split('\n');
            foreach (ColumnDefinition column in table.Columns)
            {
                string expectedType = column.Type switch
                {
                    SqliteColumnType.SqliteInteger => "INTEGER",
                    SqliteColumnType.SqliteReal => "REAL",
                    SqliteColumnType.SqliteText => "TEXT",
                    _ => throw new InvalidOperationException($"Unexpected type {column.Type}."),
                };

                string? columnLine = bodyLines.FirstOrDefault(line =>
                    line.TrimStart().StartsWith($"{column.Name} ", StringComparison.Ordinal));
                Assert.True(
                    columnLine is not null,
                    $"Generated DDL for '{table.Name}' does not declare column '{column.Name}'.");
                Assert.Contains($" {expectedType} NOT NULL", columnLine, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Catalog_ContainsNoJsonPayloadRawOrEavShapes()
    {
        foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
        {
            bool hasEavKey = false;
            bool hasEavValue = false;

            foreach (ColumnDefinition column in table.Columns)
            {
                Assert.False(
                    column.Name.EndsWith("_json", StringComparison.OrdinalIgnoreCase),
                    $"Table '{table.Name}' declares forbidden *_json column '{column.Name}'.");
                Assert.False(
                    string.Equals(column.Name, "payload", StringComparison.OrdinalIgnoreCase)
                    || column.Name.EndsWith("_payload", StringComparison.OrdinalIgnoreCase)
                    || column.Name.StartsWith("payload_", StringComparison.OrdinalIgnoreCase),
                    $"Table '{table.Name}' declares forbidden payload column '{column.Name}'.");
                Assert.False(
                    string.Equals(column.Name, "raw", StringComparison.OrdinalIgnoreCase)
                    || column.Name.StartsWith("raw_", StringComparison.OrdinalIgnoreCase)
                    || column.Name.EndsWith("_raw", StringComparison.OrdinalIgnoreCase),
                    $"Table '{table.Name}' declares forbidden raw column '{column.Name}'.");

                if (EavKeyColumns.Contains(column.Name))
                    hasEavKey = true;
                if (EavValueColumns.Contains(column.Name))
                    hasEavValue = true;
            }

            Assert.False(
                hasEavKey && hasEavValue,
                $"Table '{table.Name}' exhibits an EAV key/value shape.");
        }
    }

    [Fact]
    public void Catalog_AllForeignKeysUseOnDeleteCascadeAndTopologicalOrder()
    {
        Dictionary<string, int> emissionOrder = new(StringComparer.Ordinal);
        for (int i = 0; i < RelationalSchemaCatalog.Tables.Count; i++)
            emissionOrder[RelationalSchemaCatalog.Tables[i].Name] = i;

        foreach (string rootTable in DeclaredRootTables)
        {
            Assert.True(
                RelationalSchemaCatalog.TryGetTable(rootTable, out TableDefinition? resolvedRoot),
                $"Declared root table '{rootTable}' is missing from RelationalSchemaCatalog.");
            Assert.Empty(resolvedRoot!.ForeignKeys);
        }

        foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
        {
            if (!DeclaredRootTables.Contains(table.Name))
            {
                Assert.True(
                    table.ForeignKeys.Count > 0,
                    $"Non-root table '{table.Name}' must declare at least one foreign key.");
            }

            string ddl = SchemaDdlGenerator.GenerateTableDdl(table);
            foreach (ForeignKeyDefinition fk in table.ForeignKeys)
            {
                Assert.True(
                    fk.OnDelete == ForeignKeyDeleteAction.Cascade,
                    $"Foreign key on '{table.Name}' -> '{fk.ReferencedTable}' must use ON DELETE CASCADE.");
                Assert.True(
                    RelationalSchemaCatalog.TryGetTable(fk.ReferencedTable, out TableDefinition? parent),
                    $"Foreign key on '{table.Name}' references unknown table '{fk.ReferencedTable}'.");
                Assert.Equal(parent!.PrimaryKeyColumns, fk.ReferencedColumns);
                Assert.True(
                    emissionOrder[fk.ReferencedTable] < emissionOrder[table.Name],
                    $"Parent table '{fk.ReferencedTable}' must precede child '{table.Name}' in topological order.");

                string expectedClause =
                    $"FOREIGN KEY ({string.Join(", ", fk.Columns)}) REFERENCES {fk.ReferencedTable} ({string.Join(", ", fk.ReferencedColumns)}) ON DELETE CASCADE";
                Assert.Contains(expectedClause, ddl, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Catalog_EveryRepeatedItemTableCarriesNonNullPosition()
    {
        List<TableDefinition> repeatedTables = RelationalSchemaCatalog.Tables
            .Where(static table => table.IsRepeatedItem)
            .ToList();

        Assert.NotEmpty(repeatedTables);

        foreach (TableDefinition table in repeatedTables)
        {
            ColumnDefinition? position = table.Columns.FirstOrDefault(static column =>
                string.Equals(column.Name, "position", StringComparison.Ordinal));
            Assert.True(
                position is not null,
                $"Repeated-item table '{table.Name}' must declare a 'position' column.");
            Assert.Equal(SqliteColumnType.SqliteInteger, position.Type);
            Assert.True(position.IsNotNull);
            Assert.Contains("position >= 0", position.CheckConstraint ?? string.Empty, StringComparison.Ordinal);
            if (table.PrimaryKeyColumns.Count > 1)
            {
                Assert.Contains("position", table.PrimaryKeyColumns);
            }
        }

        foreach (string compositeTableName in NonRepeatedCompositeKeyTables)
        {
            Assert.True(
                RelationalSchemaCatalog.TryGetTable(compositeTableName, out TableDefinition? compositeTable),
                $"Declared non-repeated composite-key table '{compositeTableName}' is missing from RelationalSchemaCatalog.");
            Assert.True(
                compositeTable!.PrimaryKeyColumns.Count > 1,
                $"Table '{compositeTableName}' in NonRepeatedCompositeKeyTables must declare a composite primary key.");
            Assert.False(
                compositeTable.IsRepeatedItem,
                $"Table '{compositeTableName}' in NonRepeatedCompositeKeyTables must have IsRepeatedItem = false.");
        }

        foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
        {
            if (table.PrimaryKeyColumns.Count > 1 && !NonRepeatedCompositeKeyTables.Contains(table.Name))
            {
                Assert.True(
                    table.IsRepeatedItem,
                    $"Multi-column PK table '{table.Name}' outside NonRepeatedCompositeKeyTables must set IsRepeatedItem = true.");

                ColumnDefinition? position = table.Columns.FirstOrDefault(static column =>
                    string.Equals(column.Name, "position", StringComparison.Ordinal));
                Assert.True(
                    position is not null,
                    $"Multi-column PK table '{table.Name}' outside NonRepeatedCompositeKeyTables must declare 'position'.");
                Assert.Equal(SqliteColumnType.SqliteInteger, position.Type);
                Assert.True(position.IsNotNull);
                Assert.Contains("position >= 0", position.CheckConstraint ?? string.Empty, StringComparison.Ordinal);
                Assert.Contains("position", table.PrimaryKeyColumns);
            }
        }
    }

    [Fact]
    public void Generator_EmitsDeterministicDdlInTopologicalOrderWithCheckConstraintsAndIndexes()
    {
        string first = SchemaDdlGenerator.GenerateSchemaDdl();
        string second = SchemaDdlGenerator.GenerateSchemaDdl();

        Assert.Equal(first, second);
        Assert.DoesNotContain("\r\n", first, StringComparison.Ordinal);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.Contains("CHECK (", first, StringComparison.Ordinal);
        Assert.Contains("ON DELETE CASCADE", first, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS", first, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS", first, StringComparison.Ordinal);

        int lastCreateOffset = -1;
        Dictionary<string, int> createOffsets = new(StringComparer.Ordinal);
        foreach (Match match in CreateTableRegex().Matches(first))
        {
            string name = match.Groups["name"].Value;
            Assert.True(match.Index > lastCreateOffset);
            lastCreateOffset = match.Index;
            createOffsets[name] = match.Index;
        }

        Assert.Equal(RelationalSchemaCatalog.Tables.Count, createOffsets.Count);
        foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
        {
            foreach (ForeignKeyDefinition fk in table.ForeignKeys)
            {
                Assert.True(
                    createOffsets[fk.ReferencedTable] < createOffsets[table.Name],
                    $"DDL emits '{table.Name}' before its parent '{fk.ReferencedTable}'.");
            }
        }
    }

    [Fact]
    public void Generator_RejectsNullableColumnsBlobTypesJsonPayloadRawEavMissingCascadeAndMissingPosition()
    {
        TableDefinition validParent = new(
            Name: "sample_parent",
            Columns: [new ColumnDefinition("sample_id", SqliteColumnType.SqliteText, IsNotNull: true)],
            PrimaryKeyColumns: ["sample_id"],
            ForeignKeys: [],
            Indexes: []);

        TableDefinition nullableColumn = new(
            Name: "bad_nullable",
            Columns:
            [
                new ColumnDefinition("sample_id", SqliteColumnType.SqliteText, IsNotNull: true),
                new ColumnDefinition("note", SqliteColumnType.SqliteText, IsNotNull: false),
            ],
            PrimaryKeyColumns: ["sample_id"],
            ForeignKeys: [],
            Indexes: []);

        TableDefinition blobColumn = new(
            Name: "bad_blob",
            Columns:
            [
                new ColumnDefinition("sample_id", SqliteColumnType.SqliteText),
                new ColumnDefinition("data", (SqliteColumnType)999),
            ],
            PrimaryKeyColumns: ["sample_id"],
            ForeignKeys: [],
            Indexes: []);

        TableDefinition jsonColumn = new(
            Name: "bad_json",
            Columns:
            [
                new ColumnDefinition("sample_id", SqliteColumnType.SqliteText),
                new ColumnDefinition("tokens_json", SqliteColumnType.SqliteText),
            ],
            PrimaryKeyColumns: ["sample_id"],
            ForeignKeys: [],
            Indexes: []);

        TableDefinition payloadColumn = new(
            Name: "bad_payload",
            Columns:
            [
                new ColumnDefinition("sample_id", SqliteColumnType.SqliteText),
                new ColumnDefinition("payload", SqliteColumnType.SqliteText),
            ],
            PrimaryKeyColumns: ["sample_id"],
            ForeignKeys: [],
            Indexes: []);

        TableDefinition rawColumn = new(
            Name: "bad_raw",
            Columns:
            [
                new ColumnDefinition("sample_id", SqliteColumnType.SqliteText),
                new ColumnDefinition("raw", SqliteColumnType.SqliteText),
            ],
            PrimaryKeyColumns: ["sample_id"],
            ForeignKeys: [],
            Indexes: []);

        TableDefinition eavTable = new(
            Name: "bad_eav",
            Columns:
            [
                new ColumnDefinition("sample_id", SqliteColumnType.SqliteText),
                new ColumnDefinition("attribute_name", SqliteColumnType.SqliteText),
                new ColumnDefinition("attribute_value", SqliteColumnType.SqliteText),
            ],
            PrimaryKeyColumns: ["sample_id", "attribute_name"],
            ForeignKeys: [],
            Indexes: []);

        TableDefinition missingCascade = new(
            Name: "bad_fk",
            Columns: [new ColumnDefinition("sample_id", SqliteColumnType.SqliteText)],
            PrimaryKeyColumns: ["sample_id"],
            ForeignKeys:
            [
                new ForeignKeyDefinition(
                    ["sample_id"],
                    "sample_parent",
                    ["sample_id"],
                    ForeignKeyDeleteAction.Restrict),
            ],
            Indexes: []);

        TableDefinition missingPosition = new(
            Name: "bad_repeated",
            Columns:
            [
                new ColumnDefinition("sample_id", SqliteColumnType.SqliteText),
                new ColumnDefinition("item_name", SqliteColumnType.SqliteText),
            ],
            PrimaryKeyColumns: ["sample_id", "item_name"],
            ForeignKeys:
            [
                new ForeignKeyDefinition(
                    ["sample_id"],
                    "sample_parent",
                    ["sample_id"],
                    ForeignKeyDeleteAction.Cascade),
            ],
            Indexes: [],
            IsRepeatedItem: true);

        Assert.Throws<ArgumentException>(() => SchemaDdlGenerator.GenerateTableDdl(nullableColumn));
        Assert.Throws<ArgumentException>(() => SchemaDdlGenerator.GenerateTableDdl(blobColumn));
        Assert.Throws<ArgumentException>(() => SchemaDdlGenerator.GenerateTableDdl(jsonColumn));
        Assert.Throws<ArgumentException>(() => SchemaDdlGenerator.GenerateTableDdl(payloadColumn));
        Assert.Throws<ArgumentException>(() => SchemaDdlGenerator.GenerateTableDdl(rawColumn));
        Assert.Throws<ArgumentException>(() => SchemaDdlGenerator.GenerateTableDdl(eavTable));
        Assert.Throws<ArgumentException>(() => SchemaDdlGenerator.GenerateSchemaDdl([validParent, missingCascade]));
        Assert.Throws<ArgumentException>(() => SchemaDdlGenerator.GenerateSchemaDdl([validParent, missingPosition]));

        TableDefinition cyclicA = new(
            Name: "cyclic_a",
            Columns: [new ColumnDefinition("id", SqliteColumnType.SqliteText)],
            PrimaryKeyColumns: ["id"],
            ForeignKeys: [new ForeignKeyDefinition(["id"], "cyclic_b", ["id"])],
            Indexes: []);
        TableDefinition cyclicB = new(
            Name: "cyclic_b",
            Columns: [new ColumnDefinition("id", SqliteColumnType.SqliteText)],
            PrimaryKeyColumns: ["id"],
            ForeignKeys: [new ForeignKeyDefinition(["id"], "cyclic_a", ["id"])],
            Indexes: []);
        Assert.Throws<ArgumentException>(() => SchemaDdlGenerator.GenerateSchemaDdl([cyclicA, cyclicB]));
        Assert.Throws<ArgumentException>(() => SchemaDdlGenerator.GenerateSchemaDdl([validParent, validParent]));
    }

    [Fact]
    public void Catalog_RepeatedRecordEntitiesAndDdlExcludeForbiddenTokens()
    {
        TableDefinition contentPart = RelationalSchemaCatalog.GetTable("content_part");
        TableDefinition toolDefinition = RelationalSchemaCatalog.GetTable("tool_definition");
        TableDefinition toolCall = RelationalSchemaCatalog.GetTable("tool_call");
        TableDefinition agentInvocation = RelationalSchemaCatalog.GetTable("agent_invocation");

        Assert.True(contentPart.IsRepeatedItem);
        Assert.True(toolDefinition.IsRepeatedItem);
        Assert.Contains(contentPart.Columns, static column => string.Equals(column.Name, "position", StringComparison.Ordinal));
        Assert.Contains(toolDefinition.Columns, static column => string.Equals(column.Name, "position", StringComparison.Ordinal));
        Assert.Contains("position", contentPart.PrimaryKeyColumns);
        Assert.Contains("position", toolDefinition.PrimaryKeyColumns);

        Assert.False(toolCall.IsRepeatedItem);
        Assert.False(agentInvocation.IsRepeatedItem);
        Assert.Equal(["span_id"], toolCall.PrimaryKeyColumns);
        Assert.Equal(["span_id"], agentInvocation.PrimaryKeyColumns);

        string fullDdl = SchemaDdlGenerator.GenerateSchemaDdl();
        Assert.False(BlobTokenRegex().IsMatch(fullDdl), "Full DDL must not contain BLOB.");
        Assert.DoesNotContain("_json", fullDdl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" payload ", fullDdl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" raw ", fullDdl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_ExcludesRemovedT1TablesAndComputableProjectionColumns()
    {
        string[] removedTables =
        [
            "record_tokens",
            "record_reasoning_tokens",
            "record_cost",
            "record_cost_value",
            "record_cost_currency",
            "record_cost_by_model",
            "record_measurability",
            "record_measurability_reason",
            "record_content_bucket",
            "record_skill_invoked",
            "record_skill_listed",
            "record_skill_listed_description",
            "record_bash_command",
            "record_tool_sequence_entry",
            "record_offered_tool",
            "record_exit_code",
            "record_provider",
            "record_context_window",
            "record_deduplication",
            "record_turn_identity",
            "record_native_identity",
            "record_initiator",
            "record_session_label",
            "record_termination",
            "record_active_duration",
            "record_web_search_requests",
            "record_repo",
            "record_branch",
            "record_agent_attribute",
            "record_provenance_digest",
            "record_provenance_parser",
            "content_part_tokens",
            "content_part_resident",
            "content_part_truncated",
            "tool_definition_tokens",
            "tool_definition_description",
            "tool_call_parent",
            "tool_call_arguments",
            "tool_call_arguments_hash",
            "tool_call_result",
            "tool_call_timing",
            "tool_call_completed_at",
            "agent_invocation_agent_id",
            "agent_invocation_parent_tool_call",
            "agent_invocation_target_session",
            "agent_invocation_description",
            "session_sync_state",
            "session_settled",
            "session_transcript",
            "session_total_input",
            "session_total_output",
            "session_total_cache_read",
            "session_total_cache_creation",
            "session_cache_hit_ratio",
            "session_cache_creation_coverage",
            "session_tool_call_count",
            "session_cost",
            "session_cost_amount",
            "session_cost_basis_mismatch",
            "session_cost_basis_mismatch_basis",
            "session_cost_basis_mismatch_total",
            "session_auxiliary_cost",
            "session_auxiliary_cost_amount",
            "session_model",
            "session_tool_invoked",
            "session_tool_offered",
            "session_context_summary",
            "session_context_limit_source",
            "session_context_unmeasurable_reason",
            "session_tool_summary",
            "session_tool_summary_server",
            "session_mcp_server",
            "session_schema_analysis",
            "session_schema_unmeasurable_reason",
            "session_schema_unused_range",
            "session_schema_unused_range_currency",
            "session_request_model",
            "session_request_prompt",
            "session_reconciliation",
            "session_subagent",
            "session_subagent_parent",
            "session_measurability",
            "session_measurability_reason",
            "session_turn_span",
            "session_turn_model",
            "session_turn_reasoning",
            "session_turn_pressure",
            "session_turn_bucket",
            "session_turn_tool_server",
            "session_turn_fresh_jump",
            "session_turn_non_resident_tools",
        ];

        foreach (string removed in removedTables)
        {
            Assert.False(
                RelationalSchemaCatalog.TryGetTable(removed, out _),
                $"Removed T1 table '{removed}' must not exist in RelationalSchemaCatalog.");
        }

        TableDefinition recordTable = RelationalSchemaCatalog.GetTable("record");
        Assert.DoesNotContain(recordTable.Columns, static c => string.Equals(c.Name, "duration_ms", StringComparison.Ordinal));
        Assert.Contains(recordTable.Columns, static c => string.Equals(c.Name, "started_at_utc", StringComparison.Ordinal));

        TableDefinition contentPart = RelationalSchemaCatalog.GetTable("content_part");
        Assert.DoesNotContain(contentPart.Columns, static c => string.Equals(c.Name, "part_text", StringComparison.Ordinal));

        TableDefinition toolDefinition = RelationalSchemaCatalog.GetTable("tool_definition");
        Assert.DoesNotContain(toolDefinition.Columns, static c => string.Equals(c.Name, "definition_text", StringComparison.Ordinal));
        Assert.DoesNotContain(toolDefinition.Columns, static c => string.Equals(c.Name, "is_resident", StringComparison.Ordinal));

        TableDefinition session = RelationalSchemaCatalog.GetTable("session");
        Assert.Equal(["session_id", "harness_id", "native_session_id"], session.Columns.Select(static c => c.Name).ToArray());

        TableDefinition sessionTurn = RelationalSchemaCatalog.GetTable("session_turn");
        Assert.Equal(["session_id", "position", "span_id"], sessionTurn.Columns.Select(static c => c.Name).ToArray());
    }

    [Fact]
    public void Catalog_ModelsMutuallyExclusiveTablesSingletonsAndRetainedIdentifiers()
    {
        TableDefinition record = RelationalSchemaCatalog.GetTable("record");
        Assert.Equal(["span_id"], record.PrimaryKeyColumns);

        TableDefinition session = RelationalSchemaCatalog.GetTable("session");
        Assert.Equal(["session_id"], session.PrimaryKeyColumns);

        TableDefinition sessionLink = RelationalSchemaCatalog.GetTable("record_session_link");
        Assert.Equal(["span_id", "session_key"], sessionLink.Columns.Select(static c => c.Name).ToArray());
        Assert.Equal(["span_id"], sessionLink.PrimaryKeyColumns);

        TableDefinition workspace = RelationalSchemaCatalog.GetTable("session_workspace");
        Assert.Equal(["session_id", "working_directory"], workspace.Columns.Select(static c => c.Name).ToArray());

        TableDefinition contentPartText = RelationalSchemaCatalog.GetTable("content_part_text");
        TableDefinition contentPartWithheld = RelationalSchemaCatalog.GetTable("content_part_withheld");
        Assert.Equal(["span_id", "position"], contentPartText.PrimaryKeyColumns);
        Assert.Equal(["span_id", "position"], contentPartWithheld.PrimaryKeyColumns);

        TableDefinition sessionContext = RelationalSchemaCatalog.GetTable("session_context");
        TableDefinition sessionContextUnmeasurable = RelationalSchemaCatalog.GetTable("session_context_unmeasurable");
        Assert.Equal(["session_id"], sessionContext.PrimaryKeyColumns);
        Assert.Equal(["session_id"], sessionContextUnmeasurable.PrimaryKeyColumns);

        TableDefinition sessionSchema = RelationalSchemaCatalog.GetTable("session_schema");
        TableDefinition sessionSchemaUnmeasurable = RelationalSchemaCatalog.GetTable("session_schema_unmeasurable");
        Assert.Equal(["session_id"], sessionSchema.PrimaryKeyColumns);
        Assert.Equal(["session_id"], sessionSchemaUnmeasurable.PrimaryKeyColumns);

        foreach (string singletonTableName in SingletonRootTables)
        {
            TableDefinition singleton = RelationalSchemaCatalog.GetTable(singletonTableName);
            Assert.Empty(singleton.ForeignKeys);
            Assert.Equal(["singleton_id"], singleton.PrimaryKeyColumns);
            ColumnDefinition singletonCol = Assert.Single(singleton.Columns, static c => string.Equals(c.Name, "singleton_id", StringComparison.Ordinal));
            Assert.Equal(SqliteColumnType.SqliteInteger, singletonCol.Type);
            Assert.Contains("singleton_id = 1", singletonCol.CheckConstraint ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Catalog_GetTableProvidesNearestMatchHintForUnknownName()
    {
        KeyNotFoundException error = Assert.Throws<KeyNotFoundException>(() =>
            RelationalSchemaCatalog.GetTable("reord"));
        Assert.Contains("record", error.Message, StringComparison.Ordinal);
    }
}
