using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace KyberDash.Domain.Schema;

/// <summary>SQLite storage affinities permitted by the relational KyberDash schema.</summary>
public enum SqliteColumnType
{
    /// <summary>64-bit signed integer affinity (<c>INTEGER</c>).</summary>
    SqliteInteger,

    /// <summary>64-bit IEEE floating-point affinity (<c>REAL</c>).</summary>
    SqliteReal,

    /// <summary>UTF-8 string affinity (<c>TEXT</c>).</summary>
    SqliteText,
}

/// <summary>Foreign key <c>ON DELETE</c> actions recognized during schema validation.</summary>
public enum ForeignKeyDeleteAction
{
    /// <summary>Deletes child rows automatically when the parent row is deleted (<c>ON DELETE CASCADE</c>).</summary>
    Cascade,

    /// <summary>Refuses parent deletion when child rows exist (<c>ON DELETE RESTRICT</c>).</summary>
    Restrict,

    /// <summary>Sets child columns to null on parent deletion (<c>ON DELETE SET NULL</c>).</summary>
    SetNull,

    /// <summary>Performs no action on parent deletion (<c>ON DELETE NO ACTION</c>).</summary>
    NoAction,
}

/// <summary>Sort direction for an indexed column.</summary>
public enum IndexSortDirection
{
    /// <summary>Ascending index key order (<c>ASC</c>).</summary>
    Ascending,

    /// <summary>Descending index key order (<c>DESC</c>).</summary>
    Descending,
}

/// <summary>Column specification inside a <see cref="TableDefinition"/>.</summary>
/// <param name="Name">Lowercase <c>snake_case</c> column identifier.</param>
/// <param name="Type">Permitted SQLite affinity (<c>INTEGER</c>, <c>REAL</c>, or <c>TEXT</c>).</param>
/// <param name="IsNotNull">Whether the column carries <c>NOT NULL</c>; every relational catalog column must be non-null.</param>
/// <param name="CheckConstraint">Optional column-level <c>CHECK</c> expression.</param>
public record ColumnDefinition(
    string Name,
    SqliteColumnType Type,
    bool IsNotNull = true,
    string? CheckConstraint = null);

/// <summary>Foreign key relationship declared on a relational table.</summary>
/// <param name="Columns">Local child column names forming the referencing key.</param>
/// <param name="ReferencedTable">Parent table name referenced by this key.</param>
/// <param name="ReferencedColumns">Parent key column names in the same order as <paramref name="Columns"/>.</param>
/// <param name="OnDelete">Delete propagation policy; every catalog foreign key must use <see cref="ForeignKeyDeleteAction.Cascade"/>.</param>
public record ForeignKeyDefinition(
    IReadOnlyList<string> Columns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    ForeignKeyDeleteAction OnDelete = ForeignKeyDeleteAction.Cascade);

/// <summary>Column reference inside an <see cref="IndexDefinition"/>.</summary>
/// <param name="Name">Indexed column name belonging to the owning table.</param>
/// <param name="Direction">Key ordering direction.</param>
public record IndexColumnDefinition(
    string Name,
    IndexSortDirection Direction = IndexSortDirection.Ascending);

/// <summary>Secondary or unique index declared on a <see cref="TableDefinition"/>.</summary>
/// <param name="Name">Lowercase <c>snake_case</c> index identifier.</param>
/// <param name="Columns">Ordered list of indexed columns.</param>
/// <param name="IsUnique">Whether the index enforces uniqueness.</param>
public record IndexDefinition(
    string Name,
    IReadOnlyList<IndexColumnDefinition> Columns,
    bool IsUnique = false);

/// <summary>Immutable definition of one normalized SQLite table and its constraints.</summary>
/// <param name="Name">Lowercase <c>snake_case</c> table identifier.</param>
/// <param name="Columns">Ordered column definitions.</param>
/// <param name="PrimaryKeyColumns">Primary key column names in key order.</param>
/// <param name="ForeignKeys">Foreign keys owned by this table.</param>
/// <param name="Indexes">Indexes declared on this table.</param>
/// <param name="CheckConstraints">Optional table-level <c>CHECK</c> expressions.</param>
/// <param name="IsRepeatedItem">True when this table represents an ordered multi-item child collection requiring a <c>position</c> column.</param>
public record TableDefinition(
    string Name,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<string> PrimaryKeyColumns,
    IReadOnlyList<ForeignKeyDefinition> ForeignKeys,
    IReadOnlyList<IndexDefinition> Indexes,
    IReadOnlyList<string>? CheckConstraints = null,
    bool IsRepeatedItem = false);

/// <summary>
/// Single authoritative catalog of normalized relational tables for KyberDash telemetry records,
/// content parts, tool definitions, tool calls, agent invocations, sessions, projections, runs,
/// executions, rollups, findings, predictions, and operational/store-state entities.
/// </summary>
/// <remarks>
/// <para>
/// Every column in the catalog is <c>NOT NULL</c> and typed solely as <c>INTEGER</c>, <c>REAL</c>, or
/// <c>TEXT</c>. Optional attributes live in 0..1 child tables linked via <c>ON DELETE CASCADE</c>
/// rather than nullable columns, and ordered collections carry an explicit <c>position</c> integer.
/// </para>
/// <para>
/// Cross-entity links whose target can arrive out of order or survive target rebuilds (such as
/// <c>record_parent.parent_span_id</c>, <c>record_session_link.session_key</c>,
/// <c>session_projection_parent.parent_session_id</c>, <c>agent_invocation_spawn.spawning_record_id</c>,
/// <c>finding_run.run_id</c>, <c>finding_session.session_id</c>, and <c>prediction.finding_id</c>)
/// store indexed scalar identifiers without a parent-row foreign key so child-first OTLP batches and
/// projection rebuilds never fail under <c>PRAGMA foreign_keys = ON</c>.
/// </para>
/// </remarks>
public static partial class RelationalSchemaCatalog
{
    private static readonly HashSet<string> EavKeyColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key",
        "attr_key",
        "attribute_key",
        "attribute_name",
        "property_name",
        "field_name",
        "tag_key",
    };

    private static readonly HashSet<string> EavValueColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "value",
        "attr_value",
        "attribute_value",
        "property_value",
        "field_value",
        "tag_value",
    };

    private static readonly IReadOnlyList<TableDefinition> CatalogTables = BuildAndValidateCatalog();

    private static readonly IReadOnlyDictionary<string, TableDefinition> CatalogByName =
        new ReadOnlyDictionary<string, TableDefinition>(
            CatalogTables.ToDictionary(static table => table.Name, StringComparer.Ordinal));

    /// <summary>All normalized schema tables in topological parent-before-child order.</summary>
    public static IReadOnlyList<TableDefinition> Tables => CatalogTables;

    /// <summary>Lookup table of all normalized schema tables keyed by table name.</summary>
    public static IReadOnlyDictionary<string, TableDefinition> TablesByName => CatalogByName;

    /// <summary>Returns the <see cref="TableDefinition"/> named <paramref name="tableName"/>.</summary>
    /// <param name="tableName">Exact table name to resolve.</param>
    /// <returns>The matching table definition.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="tableName"/> does not exist, carrying the nearest match hint.</exception>
    public static TableDefinition GetTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        if (CatalogByName.TryGetValue(tableName, out TableDefinition? table))
            return table;

        string? nearest = FindNearestName(tableName, CatalogTables.Select(static table => table.Name));
        string hint = nearest is null
            ? "Inspect RelationalSchemaCatalog.Tables for declared table names."
            : $"Did you mean '{nearest}'?";
        throw new KeyNotFoundException($"Unknown relational schema table '{tableName}'. {hint}");
    }

    /// <summary>Attempts to resolve the <see cref="TableDefinition"/> named <paramref name="tableName"/>.</summary>
    /// <param name="tableName">Table name to locate.</param>
    /// <param name="table">Resolved table definition when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if found; otherwise <see langword="false"/>.</returns>
    public static bool TryGetTable(string tableName, out TableDefinition? table)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            table = null;
            return false;
        }

        return CatalogByName.TryGetValue(tableName, out table);
    }

    /// <summary>
    /// Validates a single <see cref="TableDefinition"/> against all relational invariants:
    /// valid identifiers, <c>NOT NULL</c>, permitted types (<c>INTEGER</c>/<c>REAL</c>/<c>TEXT</c>),
    /// no <c>*_json</c>/<c>payload</c>/<c>raw</c>/EAV shapes, <c>ON DELETE CASCADE</c> on foreign keys,
    /// and <c>position</c> on repeated-item tables.
    /// </summary>
    /// <param name="table">Table definition to validate.</param>
    /// <exception cref="ArgumentException">Thrown when any schema invariant is violated.</exception>
    public static void ValidateTable(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        ValidateIdentifier(table.Name, "table name");

        if (table.Columns.Count == 0)
            throw new ArgumentException($"Table '{table.Name}' must define at least one column.", nameof(table));

        if (table.PrimaryKeyColumns.Count == 0)
            throw new ArgumentException($"Table '{table.Name}' must define a primary key.", nameof(table));

        Dictionary<string, ColumnDefinition> columnsByName = new(table.Columns.Count, StringComparer.Ordinal);
        bool hasEavKey = false;
        bool hasEavValue = false;

        foreach (ColumnDefinition column in table.Columns)
        {
            ArgumentNullException.ThrowIfNull(column);
            ValidateIdentifier(column.Name, $"column name on table '{table.Name}'");

            if (!columnsByName.TryAdd(column.Name, column))
                throw new ArgumentException($"Table '{table.Name}' declares duplicate column '{column.Name}'.", nameof(table));

            if (!column.IsNotNull)
            {
                throw new ArgumentException(
                    $"Table '{table.Name}' column '{column.Name}' must be NOT NULL; model optional values in a child table.",
                    nameof(table));
            }

            if (column.Type is not (SqliteColumnType.SqliteInteger or SqliteColumnType.SqliteReal or SqliteColumnType.SqliteText))
            {
                throw new ArgumentException(
                    $"Table '{table.Name}' column '{column.Name}' uses unsupported type '{column.Type}'; only INTEGER, REAL, and TEXT are allowed.",
                    nameof(table));
            }

            ValidateForbiddenColumnName(table.Name, column.Name);

            if (EavKeyColumnNames.Contains(column.Name))
                hasEavKey = true;
            if (EavValueColumnNames.Contains(column.Name))
                hasEavValue = true;
        }

        if (hasEavKey && hasEavValue)
        {
            throw new ArgumentException(
                $"Table '{table.Name}' uses a forbidden Entity-Attribute-Value (EAV) key/value shape.",
                nameof(table));
        }

        HashSet<string> pkSet = new(StringComparer.Ordinal);
        foreach (string pkColumn in table.PrimaryKeyColumns)
        {
            if (!columnsByName.ContainsKey(pkColumn))
                throw new ArgumentException($"Table '{table.Name}' primary key references unknown column '{pkColumn}'.", nameof(table));

            if (!pkSet.Add(pkColumn))
                throw new ArgumentException($"Table '{table.Name}' primary key repeats column '{pkColumn}'.", nameof(table));
        }

        if (table.IsRepeatedItem)
        {
            if (!columnsByName.TryGetValue("position", out ColumnDefinition? position))
            {
                throw new ArgumentException(
                    $"Repeated-item table '{table.Name}' must declare an INTEGER NOT NULL 'position' column.",
                    nameof(table));
            }

            if (position.Type != SqliteColumnType.SqliteInteger || !position.IsNotNull)
            {
                throw new ArgumentException(
                    $"Repeated-item table '{table.Name}' column 'position' must be INTEGER NOT NULL.",
                    nameof(table));
            }

            if (table.PrimaryKeyColumns.Count > 1 && !pkSet.Contains("position"))
            {
                throw new ArgumentException(
                    $"Composite-key repeated-item table '{table.Name}' must include 'position' in its primary key.",
                    nameof(table));
            }
        }

        foreach (ForeignKeyDefinition fk in table.ForeignKeys)
        {
            ArgumentNullException.ThrowIfNull(fk);
            ValidateIdentifier(fk.ReferencedTable, $"referenced table on '{table.Name}'");

            if (fk.OnDelete != ForeignKeyDeleteAction.Cascade)
            {
                throw new ArgumentException(
                    $"Foreign key on '{table.Name}' referencing '{fk.ReferencedTable}' must declare ON DELETE CASCADE.",
                    nameof(table));
            }

            if (fk.Columns.Count == 0 || fk.Columns.Count != fk.ReferencedColumns.Count)
            {
                throw new ArgumentException(
                    $"Foreign key on '{table.Name}' referencing '{fk.ReferencedTable}' must map equal non-empty column lists.",
                    nameof(table));
            }

            foreach (string col in fk.Columns)
            {
                if (!columnsByName.ContainsKey(col))
                    throw new ArgumentException($"Foreign key on '{table.Name}' references unknown local column '{col}'.", nameof(table));
            }

            foreach (string refCol in fk.ReferencedColumns)
                ValidateIdentifier(refCol, $"referenced column on '{table.Name}'");
        }

        HashSet<string> indexNames = new(StringComparer.Ordinal);
        foreach (IndexDefinition index in table.Indexes)
        {
            ArgumentNullException.ThrowIfNull(index);
            ValidateIdentifier(index.Name, $"index name on '{table.Name}'");

            if (!indexNames.Add(index.Name))
                throw new ArgumentException($"Table '{table.Name}' declares duplicate index '{index.Name}'.", nameof(table));

            if (index.Columns.Count == 0)
                throw new ArgumentException($"Index '{index.Name}' on table '{table.Name}' must reference at least one column.", nameof(table));

            foreach (IndexColumnDefinition idxCol in index.Columns)
            {
                ArgumentNullException.ThrowIfNull(idxCol);
                if (!columnsByName.ContainsKey(idxCol.Name))
                {
                    throw new ArgumentException(
                        $"Index '{index.Name}' on table '{table.Name}' references unknown column '{idxCol.Name}'.",
                        nameof(table));
                }
            }
        }

        if (table.CheckConstraints is not null)
        {
            foreach (string check in table.CheckConstraints)
            {
                if (string.IsNullOrWhiteSpace(check))
                    throw new ArgumentException($"Table '{table.Name}' declares an empty CHECK constraint.", nameof(table));
            }
        }
    }

    /// <summary>
    /// Validates a complete set of <see cref="TableDefinition"/> entries and returns them in
    /// deterministic topological order (parents before children).
    /// </summary>
    /// <param name="tables">Tables to validate and sort.</param>
    /// <returns>Topologically ordered list of validated tables.</returns>
    /// <exception cref="ArgumentException">Thrown when any table invariant, foreign key target, or dependency cycle fails validation.</exception>
    public static IReadOnlyList<TableDefinition> ValidateAndOrderCatalog(IEnumerable<TableDefinition> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        List<TableDefinition> list = [.. tables];
        if (list.Count == 0)
            throw new ArgumentException("Schema catalog must contain at least one table.", nameof(tables));

        Dictionary<string, TableDefinition> byName = new(list.Count, StringComparer.Ordinal);
        Dictionary<string, int> declarationOrder = new(list.Count, StringComparer.Ordinal);
        HashSet<string> allIndexNames = new(StringComparer.Ordinal);

        for (int i = 0; i < list.Count; i++)
        {
            TableDefinition table = list[i];
            ValidateTable(table);

            if (!byName.TryAdd(table.Name, table))
                throw new ArgumentException($"Duplicate table name '{table.Name}' in schema catalog.", nameof(tables));

            declarationOrder[table.Name] = i;

            foreach (IndexDefinition index in table.Indexes)
            {
                if (!allIndexNames.Add(index.Name))
                    throw new ArgumentException($"Duplicate index name '{index.Name}' across schema catalog.", nameof(tables));
            }
        }

        Dictionary<string, int> inDegree = new(list.Count, StringComparer.Ordinal);
        Dictionary<string, List<string>> dependents = new(list.Count, StringComparer.Ordinal);
        foreach (TableDefinition table in list)
        {
            inDegree[table.Name] = 0;
            dependents[table.Name] = [];
        }

        foreach (TableDefinition table in list)
        {
            HashSet<string> uniqueParents = new(StringComparer.Ordinal);
            foreach (ForeignKeyDefinition fk in table.ForeignKeys)
            {
                if (!byName.TryGetValue(fk.ReferencedTable, out TableDefinition? parent))
                {
                    throw new ArgumentException(
                        $"Foreign key on '{table.Name}' references unknown table '{fk.ReferencedTable}'.",
                        nameof(tables));
                }

                if (!fk.ReferencedColumns.SequenceEqual(parent.PrimaryKeyColumns, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"Foreign key on '{table.Name}' must reference '{parent.Name}' primary key ({string.Join(", ", parent.PrimaryKeyColumns)}).",
                        nameof(tables));
                }

                if (string.Equals(fk.ReferencedTable, table.Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Self-referential foreign key on '{table.Name}' prevents acyclic topological table creation.",
                        nameof(tables));
                }

                if (uniqueParents.Add(fk.ReferencedTable))
                {
                    dependents[fk.ReferencedTable].Add(table.Name);
                    inDegree[table.Name]++;
                }
            }
        }

        List<TableDefinition> ordered = new(list.Count);
        PriorityQueue<string, int> ready = new();
        foreach (TableDefinition table in list)
        {
            if (inDegree[table.Name] == 0)
                ready.Enqueue(table.Name, declarationOrder[table.Name]);
        }

        while (ready.Count > 0)
        {
            string current = ready.Dequeue();
            ordered.Add(byName[current]);

            foreach (string child in dependents[current])
            {
                inDegree[child]--;
                if (inDegree[child] == 0)
                    ready.Enqueue(child, declarationOrder[child]);
            }
        }

        if (ordered.Count != list.Count)
            throw new ArgumentException("Schema catalog contains a cyclic foreign key dependency.", nameof(tables));

        return ordered.AsReadOnly();
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SnakeCaseIdentifierRegex();

    private static void ValidateIdentifier(string identifier, string role)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !SnakeCaseIdentifierRegex().IsMatch(identifier))
        {
            throw new ArgumentException(
                $"Invalid {role} '{identifier}'; identifiers must be lowercase snake_case ASCII matching ^[a-z][a-z0-9_]*$.");
        }
    }

    private static void ValidateForbiddenColumnName(string tableName, string columnName)
    {
        if (columnName.EndsWith("_json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Table '{tableName}' column '{columnName}' uses a forbidden *_json suffix; normalize fields into typed columns or child tables.");
        }

        if (string.Equals(columnName, "payload", StringComparison.OrdinalIgnoreCase)
            || columnName.EndsWith("_payload", StringComparison.OrdinalIgnoreCase)
            || columnName.StartsWith("payload_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Table '{tableName}' column '{columnName}' uses a forbidden payload column name.");
        }

        if (string.Equals(columnName, "raw", StringComparison.OrdinalIgnoreCase)
            || columnName.StartsWith("raw_", StringComparison.OrdinalIgnoreCase)
            || columnName.EndsWith("_raw", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Table '{tableName}' column '{columnName}' uses a forbidden raw column name.");
        }
    }

    internal static string? FindNearestName(string candidate, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string name in candidates)
        {
            int distance = LevenshteinDistance(candidate, name);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = name;
            }
        }

        return bestDistance <= Math.Max(3, candidate.Length / 2) ? best : null;
    }

    private static int LevenshteinDistance(string source, string target)
    {
        int[] previous = new int[target.Length + 1];
        int[] current = new int[target.Length + 1];

        for (int j = 0; j <= target.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= target.Length; j++)
            {
                int cost = char.ToUpperInvariant(source[i - 1]) == char.ToUpperInvariant(target[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[target.Length];
    }

    private static IReadOnlyList<TableDefinition> BuildAndValidateCatalog()
    {
        List<TableDefinition> definitions = [];
        AddHarnessAndSourceTables(definitions);
        AddRecordTables(definitions);
        AddContentPartTables(definitions);
        AddToolDefinitionTables(definitions);
        AddToolCallTables(definitions);
        AddAgentInvocationTables(definitions);
        AddSessionTables(definitions);
        AddSessionProjectionTables(definitions);
        AddRunAndExecutionTables(definitions);
        AddRollupFindingAndPredictionTables(definitions);
        AddStoreStateAndSettingTables(definitions);
        AddOperationalAndCatalogTables(definitions);
        return ValidateAndOrderCatalog(definitions);
    }

    private static void AddHarnessAndSourceTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "harness",
            Columns: [NonEmptyText("harness_id")],
            PrimaryKeyColumns: ["harness_id"],
            ForeignKeys: [],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "source",
            Columns:
            [
                NonEmptyText("source_id"),
                NonEmptyText("harness_id"),
                EnumText("source_kind", "'file', 'otlp'"),
                NonEmptyText("locator"),
            ],
            PrimaryKeyColumns: ["source_id"],
            ForeignKeys: [Fk("harness_id", "harness", "harness_id")],
            Indexes:
            [
                UniqueIdx("source_by_harness_kind_locator", "harness_id", "source_kind", "locator"),
                Idx("source_by_harness", "harness_id"),
            ]));

        // Every source that wrote a span, not just the record's owner (record.source_id): a forked transcript
        // and its parent both carry the same spans. Deleting the owning source hands a span another source
        // still claims to that source instead of losing it. No key to record: a claim outlives a rewrite.
        tables.Add(new TableDefinition(
            Name: "record_source_claim",
            Columns: [NonEmptyText("span_id"), NonEmptyText("source_id")],
            PrimaryKeyColumns: ["span_id", "source_id"],
            ForeignKeys: [Fk("source_id", "source", "source_id")],
            Indexes: [Idx("record_source_claim_by_source", "source_id")]));
    }

    private static void AddRecordTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "record",
            Columns:
            [
                NonEmptyText("span_id"),
                NonEmptyText("source_id"),
                NonEmptyText("harness_id"),
                EnumText("op", "'llm.invoke', 'tool.invoke', 'agent.invoke', 'unspecified'"),
                EnumText("kind", "'unspecified', 'internal', 'server', 'client', 'producer', 'consumer'"),
                NonEmptyText("name"),
                NonEmptyText("started_at_utc"),
                EnumText("status", "'unset', 'ok', 'error', 'unspecified'"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys:
            [
                Fk("source_id", "source", "source_id"),
                Fk("harness_id", "harness", "harness_id"),
            ],
            Indexes:
            [
                Idx("record_by_harness_started", "harness_id", "started_at_utc"),
                Idx("record_by_source", "source_id"),
                Idx("record_by_started", "started_at_utc"),
                Idx("record_by_op", "op"),
            ]));

        tables.Add(RecordSingleText(
            "record_session_link",
            "session_key",
            nonEmpty: true,
            Idx("record_session_link_by_session_key", "session_key")));

        tables.Add(RecordSingleText(
            "record_parent",
            "parent_span_id",
            nonEmpty: true,
            Idx("record_parent_by_parent_span", "parent_span_id")));

        tables.Add(RecordSingleText(
            "record_trace",
            "trace_id",
            nonEmpty: true,
            Idx("record_trace_by_trace_id", "trace_id")));

        tables.Add(RecordSingleText(
            "record_model",
            "model_id",
            nonEmpty: true,
            Idx("record_model_by_model_id", "model_id")));

        tables.Add(RecordSingleText(
            "record_response_id",
            "response_id",
            nonEmpty: true,
            Idx("record_response_id_by_response_id", "response_id")));

        tables.Add(RecordSingleText(
            "record_request_id",
            "request_id",
            nonEmpty: true,
            Idx("record_request_id_by_request_id", "request_id")));

        tables.Add(RecordSingleText("record_end", "ended_at_utc", nonEmpty: true));
        tables.Add(RecordSingleNonNegInt("record_reported_duration", "duration_ms"));

        tables.Add(new TableDefinition(
            Name: "record_token_count",
            Columns:
            [
                NonEmptyText("span_id"),
                EnumText("token_class", "'input', 'cache_read', 'cache_write', 'output', 'reasoning'"),
                NonNegInt("token_count"),
            ],
            PrimaryKeyColumns: ["span_id", "token_class"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "record_reported_total",
            Columns:
            [
                NonEmptyText("span_id"),
                EnumText("direction", "'input', 'output'"),
                NonNegInt("token_count"),
            ],
            PrimaryKeyColumns: ["span_id", "direction"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: []));

        tables.Add(SingleChild("record_reported_cost", "span_id", "record", [NonNegReal("amount"), NonEmptyText("currency")]));

        tables.Add(new TableDefinition(
            Name: "record_skill_invocation",
            Columns: [NonEmptyText("span_id"), NonEmptyText("skill_name")],
            PrimaryKeyColumns: ["span_id", "skill_name"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "record_skill_listing",
            Columns: [NonEmptyText("span_id"), PositionCol(), NonEmptyText("skill_name")],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(RecordSingleText(
            "record_agent_instance",
            "agent_instance_id",
            nonEmpty: true,
            Idx("record_agent_instance_by_id", "agent_instance_id")));

        tables.Add(RecordSingleText("record_error", "message", nonEmpty: true));
        tables.Add(RecordSingleNonNegInt("record_edit_failure", "failure_count"));

        tables.Add(SingleChild("record_correction", "span_id", "record", []));
        tables.Add(SingleChild("record_correction_rule", "span_id", "record_correction", [NonEmptyText("correction_rule")]));
        tables.Add(SingleChild("record_declared_context_window", "span_id", "record", [PosInt("declared_context_window")]));
    }

    private static void AddContentPartTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "content_part",
            Columns:
            [
                NonEmptyText("span_id"),
                PositionCol(),
                EnumText(
                    "bucket",
                    "'system_prompt', 'tool_definitions', 'tool_definition', 'instruction_context', 'conversation_history', 'tool_result_content', 'user_prompt', 'user_message', 'reasoning'"),
            ],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [Idx("content_part_by_bucket", "span_id", "bucket")],
            IsRepeatedItem: true));

        tables.Add(ContentPartExtension("content_part_text", TextCol("part_text")));
        tables.Add(ContentPartExtension(
            "content_part_withheld",
            NonNegInt("utf8_bytes"),
            NonNegInt("characters"),
            NonNegInt("measured_tokens")));
        tables.Add(new TableDefinition(
            Name: "content_part_withheld_tokenizer",
            Columns: [NonEmptyText("span_id"), PositionCol(), NonEmptyText("tokenizer"), BoolInt("is_model_own")],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [CompositeFk(["span_id", "position"], "content_part_withheld", ["span_id", "position"])],
            Indexes: [],
            IsRepeatedItem: true));
        tables.Add(ContentPartExtension("content_part_reported_tokens", NonNegInt("token_count")));
        tables.Add(ContentPartExtension("content_part_server", NonEmptyText("server_name")));
        tables.Add(ContentPartExtension("content_part_role", NonEmptyText("role_name")));
        tables.Add(ContentPartExtension("content_part_residency", BoolInt("is_resident")));
        tables.Add(ContentPartExtension("content_part_truncation", NonNegInt("original_utf8_bytes")));
    }

    private static void AddToolDefinitionTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "tool_definition",
            Columns:
            [
                NonEmptyText("span_id"),
                PositionCol(),
                NonEmptyText("tool_name"),
                NonNegInt("schema_tokens"),
            ],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [Idx("tool_definition_by_name", "tool_name")],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "tool_definition_tokenizer",
            Columns: [NonEmptyText("span_id"), PositionCol(), NonEmptyText("tokenizer"), BoolInt("is_model_own")],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [CompositeFk(["span_id", "position"], "tool_definition", ["span_id", "position"])],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "tool_definition_server",
            Columns: [NonEmptyText("span_id"), PositionCol(), NonEmptyText("server_name")],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [CompositeFk(["span_id", "position"], "tool_definition", ["span_id", "position"])],
            Indexes: [],
            IsRepeatedItem: true));
    }

    private static void AddToolCallTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "tool_call",
            Columns:
            [
                NonEmptyText("span_id"),
                TextCol("call_id"),
                NonEmptyText("tool_name"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes:
            [
                Idx("tool_call_by_call_id", "call_id"),
                Idx("tool_call_by_name", "tool_name"),
            ]));

        tables.Add(ToolCallExtension("tool_call_mcp", [NonEmptyText("server_name"), NonEmptyText("server_tool_name")]));
        tables.Add(ToolCallExtension("tool_call_input_size", [NonNegInt("utf8_bytes"), NonNegInt("characters")]));
        tables.Add(ToolCallExtension(
            "tool_call_input_fingerprint",
            [CheckedText("sha256", "length(sha256) = 64")],
            Idx("tool_call_input_fingerprint_by_sha256", "sha256")));
        tables.Add(ToolCallExtension("tool_call_input_text", [TextCol("text")]));
        tables.Add(ToolCallExtension("tool_call_input_truncation", [NonNegInt("original_utf8_bytes")]));
        tables.Add(ToolCallExtension("tool_call_result_size", [NonNegInt("utf8_bytes"), NonNegInt("characters")]));
        tables.Add(ToolCallExtension("tool_call_result_text", [TextCol("text")]));
        tables.Add(ToolCallExtension("tool_call_result_truncation", [NonNegInt("original_utf8_bytes")]));
        tables.Add(ToolCallExtension("tool_call_outcome", [BoolInt("succeeded")]));
        tables.Add(ToolCallExtension("tool_call_error", [NonEmptyText("message")]));
        tables.Add(SingleChild("tool_call_error_code", "span_id", "tool_call_error", [NonEmptyText("code")]));
        tables.Add(ToolCallExtension("tool_call_exit_code", [IntCol("exit_code")]));
    }

    private static void AddAgentInvocationTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "agent_invocation",
            Columns: [NonEmptyText("span_id"), NonEmptyText("agent_name")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [Idx("agent_invocation_by_name", "agent_name")]));

        tables.Add(SingleChild(
            "agent_invocation_spawn",
            "span_id",
            "agent_invocation",
            [NonEmptyText("spawning_record_id")],
            Idx("agent_invocation_spawn_by_record", "spawning_record_id")));

        tables.Add(SingleChild("agent_invocation_outcome", "span_id", "agent_invocation", [BoolInt("succeeded")]));
        tables.Add(SingleChild("agent_invocation_error", "span_id", "agent_invocation", [NonEmptyText("message")]));
    }

    private static void AddSessionTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "session",
            Columns:
            [
                NonEmptyText("session_id"),
                NonEmptyText("harness_id"),
                NonEmptyText("native_session_id"),
            ],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("harness_id", "harness", "harness_id")],
            Indexes:
            [
                Idx("session_by_harness", "harness_id"),
                Idx("session_by_harness_native", "harness_id", "native_session_id"),
            ]));

        tables.Add(SingleChild("session_workspace", "session_id", "session", [NonEmptyText("working_directory")]));
        tables.Add(SingleChild("session_repository", "session_id", "session", [NonEmptyText("repo_name")]));
        tables.Add(SingleChild("session_branch", "session_id", "session", [NonEmptyText("branch_name")]));
        tables.Add(SingleChild("session_title", "session_id", "session", [NonEmptyText("title")]));

        tables.Add(new TableDefinition(
            Name: "session_model_usage",
            Columns:
            [
                NonEmptyText("session_id"),
                NonNegInt("snapshot_position"),
                NonEmptyText("model_id"),
                EnumText("token_class", "'input', 'cache_read', 'cache_write', 'output', 'reasoning', 'total'"),
                NonNegInt("token_count"),
            ],
            PrimaryKeyColumns: ["session_id", "snapshot_position", "model_id", "token_class"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: []));
    }

    private static void AddSessionProjectionTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "session_projection",
            Columns:
            [
                NonEmptyText("session_id"),
                NonEmptyText("label"),
                BoolInt("is_subagent"),
                NonNegInt("span_count"),
                NonEmptyText("started_at_utc"),
                NonEmptyText("ended_at_utc"),
            ],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: [DescIdx("session_projection_by_started_desc", "started_at_utc")]));

        tables.Add(SingleChild(
            "session_projection_parent",
            "session_id",
            "session_projection",
            [NonEmptyText("parent_session_id")],
            Idx("session_projection_parent_by_parent", "parent_session_id")));

        tables.Add(SingleChild("session_projection_agent", "session_id", "session_projection", [NonEmptyText("agent_name")]));
        tables.Add(SingleChild("session_projection_included_in_parent", "session_id", "session_projection", []));

        tables.Add(new TableDefinition(
            Name: "session_request",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("request_span_id"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session_projection", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_turn",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("span_id"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session_projection", "session_id")],
            Indexes: [Idx("session_turn_by_span", "span_id")],
            IsRepeatedItem: true));

        tables.Add(SingleChild(
            "session_context",
            "session_id",
            "session_projection",
            [
                PosInt("context_limit"),
                NonEmptyText("context_limit_source"),
                IntCol("residual_total"),
                RealCol("fresh_jump_factor"),
                RealCol("accumulation_rate"),
            ]));

        tables.Add(SingleChild(
            "session_context_unmeasurable",
            "session_id",
            "session_projection",
            [NonEmptyText("reason")]));

        tables.Add(new TableDefinition(
            Name: "session_context_observed_part",
            Columns: [NonEmptyText("session_id"), NonEmptyText("bucket")],
            PrimaryKeyColumns: ["session_id", "bucket"],
            ForeignKeys: [Fk("session_id", "session_context", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_context_derived_count",
            Columns: [NonEmptyText("session_id"), NonEmptyText("counter"), NonNegInt("count")],
            PrimaryKeyColumns: ["session_id", "counter"],
            ForeignKeys: [Fk("session_id", "session_context", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_context_flagged_turn",
            Columns: [NonEmptyText("session_id"), NonNegInt("turn_index")],
            PrimaryKeyColumns: ["session_id", "turn_index"],
            ForeignKeys: [Fk("session_id", "session_context", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_context_turn",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonNegInt("input_tokens"),
                NonNegInt("bucketed_tokens"),
                IntCol("residual_tokens"),
                NonEmptyText("residual_attribution"),
                NonNegInt("builtin_tool_definition_tokens"),
                NonNegInt("fresh_input"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session_context", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_context_turn_tokenizer",
            Columns: [NonEmptyText("session_id"), PositionCol(), NonEmptyText("tokenizer"), BoolInt("is_model_own")],
            PrimaryKeyColumns: ["session_id", "position", "tokenizer"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_context_turn", ["session_id", "position"])],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(SessionContextTurnExtension("session_context_turn_headroom", IntCol("headroom"), RealCol("pressure")));
        tables.Add(SessionContextTurnExtension("session_context_turn_accumulation", RealCol("rate")));

        tables.Add(new TableDefinition(
            Name: "session_context_turn_bucket",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("bucket"),
                NonNegInt("tokens"),
            ],
            PrimaryKeyColumns: ["session_id", "position", "bucket"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_context_turn", ["session_id", "position"])],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_context_turn_server_definitions",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("server"),
                NonNegInt("tokens"),
            ],
            PrimaryKeyColumns: ["session_id", "position", "server"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_context_turn", ["session_id", "position"])],
            Indexes: []));

        tables.Add(SessionContextTurnExtension(
            "session_context_turn_stripped_instructions",
            NonNegInt("block_count"),
            NonNegInt("tokens")));
        tables.Add(SessionContextTurnExtension(
            "session_context_turn_fresh_jump",
            NonNegInt("previous_fresh_input"),
            RealCol("factor")));
        tables.Add(SessionContextTurnExtension("session_context_turn_non_resident", NonNegInt("tokens")));

        tables.Add(SingleChild(
            "session_schema",
            "session_id",
            "session_projection",
            [NonNegInt("invocation_count"), NonNegInt("turn_count")]));

        tables.Add(SingleChild(
            "session_schema_unmeasurable",
            "session_id",
            "session_projection",
            [NonEmptyText("reason")]));

        tables.Add(new TableDefinition(
            Name: "session_schema_ranked_tool",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("tool_name"),
                NonNegInt("cost_tokens"),
                BoolInt("invoked"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session_schema", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_schema_ranked_tool_server",
            Columns: [NonEmptyText("session_id"), PositionCol(), NonEmptyText("server_name")],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_schema_ranked_tool", ["session_id", "position"])],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_schema_server",
            Columns: [NonEmptyText("session_id"), NonEmptyText("server_name"), NonNegInt("tokens")],
            PrimaryKeyColumns: ["session_id", "server_name"],
            ForeignKeys: [Fk("session_id", "session_schema", "session_id")],
            Indexes: []));

        tables.Add(SingleChild("session_schema_unused", "session_id", "session_schema", [NonNegInt("token_residencies")]));

        tables.Add(new TableDefinition(
            Name: "session_timeline_subagent",
            Columns: [NonEmptyText("session_id"), NonEmptyText("span_id")],
            PrimaryKeyColumns: ["session_id", "span_id"],
            ForeignKeys: [Fk("session_id", "session_projection", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_timeline_auxiliary",
            Columns: [NonEmptyText("session_id"), NonEmptyText("span_id")],
            PrimaryKeyColumns: ["session_id", "span_id"],
            ForeignKeys: [Fk("session_id", "session_projection", "session_id")],
            Indexes: []));
    }

    private static void AddRunAndExecutionTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "run",
            Columns:
            [
                NonEmptyText("run_id"),
                NonEmptyText("harness_id"),
                EnumText("grouping_basis", "'explicit', 'derived'"),
                EnumText("grouping_rule", "'explicit_run_id', 'session_fallback'"),
                NonNegInt("execution_count"),
            ],
            PrimaryKeyColumns: ["run_id"],
            ForeignKeys: [Fk("harness_id", "harness", "harness_id")],
            Indexes: [Idx("run_by_harness", "harness_id")]));

        tables.Add(SingleChild("run_label", "run_id", "run", [NonEmptyText("label")]));
        tables.Add(SingleChild("run_working_directory", "run_id", "run", [NonEmptyText("working_directory")]));
        tables.Add(SingleChild("run_bounds", "run_id", "run", [NonEmptyText("started_at_utc"), NonEmptyText("ended_at_utc")]));

        tables.Add(new TableDefinition(
            Name: "execution",
            Columns:
            [
                NonEmptyText("execution_id"),
                NonEmptyText("run_id"),
                NonEmptyText("harness_id"),
                BoolInt("is_root"),
            ],
            PrimaryKeyColumns: ["execution_id"],
            ForeignKeys:
            [
                Fk("run_id", "run", "run_id"),
                Fk("harness_id", "harness", "harness_id"),
            ],
            Indexes: [Idx("execution_by_run", "run_id")]));

        tables.Add(new TableDefinition(
            Name: "execution_session",
            Columns: [NonEmptyText("execution_id"), NonEmptyText("session_id")],
            PrimaryKeyColumns: ["execution_id"],
            ForeignKeys:
            [
                Fk("execution_id", "execution", "execution_id"),
                Fk("session_id", "session", "session_id"),
            ],
            Indexes: [Idx("execution_session_by_session", "session_id")]));

        tables.Add(new TableDefinition(
            Name: "execution_parent",
            Columns: [NonEmptyText("execution_id"), NonEmptyText("parent_execution_id")],
            PrimaryKeyColumns: ["execution_id"],
            ForeignKeys:
            [
                Fk("execution_id", "execution", "execution_id"),
                Fk("parent_execution_id", "execution", "execution_id"),
            ],
            Indexes: [Idx("execution_parent_by_parent", "parent_execution_id")]));

        tables.Add(SingleChild("execution_agent", "execution_id", "execution", [NonEmptyText("agent_name")]));
        tables.Add(SingleChild("execution_bounds", "execution_id", "execution", [NonEmptyText("started_at_utc"), NonEmptyText("ended_at_utc")]));
    }

    private static void AddRollupFindingAndPredictionTables(List<TableDefinition> tables)
    {
        tables.Add(SingleChild(
            "harness_rollup",
            "harness_id",
            "harness",
            [
                NonNegInt("session_count"),
                NonNegInt("run_count"),
                NonNegInt("execution_count"),
                NonNegInt("unknown_window_sessions"),
                NonNegInt("window_sessions_total"),
            ]));

        tables.Add(new TableDefinition(
            Name: "harness_rollup_metric",
            Columns:
            [
                NonEmptyText("harness_id"),
                EnumText(
                    "metric",
                    "'context_pressure_median', 'context_pressure_p95', 'cache_hit_rate', 'tool_yield', 'delegation_overhead'"),
                RealCol("value"),
                EnumText("basis", "'measured', 'derived'"),
            ],
            PrimaryKeyColumns: ["harness_id", "metric"],
            ForeignKeys: [Fk("harness_id", "harness_rollup", "harness_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "finding",
            Columns:
            [
                NonEmptyText("finding_id"),
                NonEmptyText("detector_id"),
                NonEmptyText("title"),
                NonEmptyText("mechanism"),
                NonEmptyText("confidence"),
                NonEmptyText("recommendation"),
                NonEmptyText("outcome_risk_caveat"),
                RealCol("rank_score"),
            ],
            PrimaryKeyColumns: ["finding_id"],
            ForeignKeys: [],
            Indexes:
            [
                Idx("finding_by_detector", "detector_id"),
                DescIdx("finding_by_rank_score_desc", "rank_score"),
            ]));

        tables.Add(SingleChild("finding_estimated_waste", "finding_id", "finding", [NonNegInt("tokens")]));
        tables.Add(SingleChild("finding_error_bar", "finding_id", "finding", [RealCol("lower"), RealCol("upper")]));

        tables.Add(new TableDefinition(
            Name: "finding_evidence_link",
            Columns:
            [
                NonEmptyText("finding_id"),
                PositionCol(),
                NonEmptyText("span_id"),
                IntCol("turn_index"),
                TextCol("description"),
            ],
            PrimaryKeyColumns: ["finding_id", "position"],
            ForeignKeys: [Fk("finding_id", "finding", "finding_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(SingleChild("finding_run", "finding_id", "finding", [NonEmptyText("run_id")], Idx("finding_run_by_run", "run_id")));
        tables.Add(SingleChild("finding_session", "finding_id", "finding", [NonEmptyText("session_id")], Idx("finding_session_by_session", "session_id")));
        tables.Add(SingleChild("finding_measurement_class", "finding_id", "finding", [NonEmptyText("measurement_class")]));
        tables.Add(SingleChild("finding_confidence_basis", "finding_id", "finding", [NonEmptyText("confidence_basis")]));
        tables.Add(SingleChild("finding_what_would_raise_it", "finding_id", "finding", [NonEmptyText("text")]));
        tables.Add(SingleChild(
            "finding_compaction_hazard",
            "finding_id",
            "finding",
            [PosInt("context_limit"), NonEmptyText("context_limit_source")]));

        tables.Add(new TableDefinition(
            Name: "prediction",
            Columns:
            [
                NonEmptyText("prediction_id"),
                NonEmptyText("finding_id"),
                NonEmptyText("run_id"),
                NonNegInt("predicted_waste_tokens"),
                RealCol("confidence"),
                NonEmptyText("confidence_tier"),
                NonEmptyText("created_at_utc"),
            ],
            PrimaryKeyColumns: ["prediction_id"],
            ForeignKeys: [],
            Indexes:
            [
                Idx("prediction_by_finding", "finding_id"),
                Idx("prediction_by_run", "run_id"),
                Idx("prediction_by_created_at", "created_at_utc"),
            ]));

        tables.Add(SingleChild("prediction_comparison", "prediction_id", "prediction", [NonEmptyText("comparison_run_id")]));
        tables.Add(SingleChild("prediction_observed_delta", "prediction_id", "prediction", [IntCol("tokens")]));
        tables.Add(SingleChild("prediction_calibration", "prediction_id", "prediction", [RealCol("score")]));
    }

    private static void AddStoreStateAndSettingTables(List<TableDefinition> tables)
    {
        tables.Add(SingletonTable("store_state", PosInt("schema_version"), PosInt("detector_version")));
        tables.Add(SingletonTable("store_write_marker", NonNegInt("records_write_seq")));
        tables.Add(SingletonTable("store_clean_stamp", NonEmptyText("last_clean_at_utc")));
        tables.Add(SingletonTable("projection_state", IntCol("projection_schema_version"), NonNegInt("records_marker")));
        tables.Add(SingletonTable("projection_dirty_range", NonNegInt("from_seq"), NonNegInt("to_seq")));
        tables.Add(SingletonTable("content_retention", PosInt("retention_days")));
        tables.Add(SingletonTable("content_purge_mark", NonEmptyText("purged_through_utc")));
        tables.Add(SingletonTable("context_window_catalog_state", NonEmptyText("status")));
        tables.Add(SingletonTable("operator_setting_folder_import", BoolInt("scheduled")));
        tables.Add(SingletonTable("operator_setting_jobs", BoolInt("paused")));
        tables.Add(SingletonTable("operator_setting_refresh_cadence", CheckedInt("minutes", "minutes >= 1 AND minutes <= 1440")));
        tables.Add(SingletonTable("operator_setting_receiver", BoolInt("hosted")));
        tables.Add(SingletonTable("operator_setting_dashboard", CheckedInt("port", "port >= 1 AND port <= 65535")));

        tables.Add(new TableDefinition(
            Name: "projection_dirty_session",
            Columns: [NonEmptyText("session_id")],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_key_alias",
            Columns: [NonEmptyText("alias_key"), NonEmptyText("session_id")],
            PrimaryKeyColumns: ["alias_key"],
            ForeignKeys: [],
            Indexes: [Idx("session_key_alias_by_session", "session_id")]));
    }

    private static void AddOperationalAndCatalogTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "quarantined_span",
            Columns: [NonEmptyText("span_id"), NonEmptyText("reason")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "quarantined_span_namespace",
            Columns: [NonEmptyText("span_id"), NonEmptyText("namespace_name")],
            PrimaryKeyColumns: ["span_id", "namespace_name"],
            ForeignKeys: [Fk("span_id", "quarantined_span", "span_id")],
            Indexes: []));

        tables.Add(SingleChild("quarantined_span_source", "span_id", "quarantined_span", [NonEmptyText("source")]));
        tables.Add(SingleChild("quarantined_span_name", "span_id", "quarantined_span", [NonEmptyText("name")]));
        tables.Add(SingleChild("quarantined_span_timestamp", "span_id", "quarantined_span", [NonEmptyText("timestamp_utc")]));
        tables.Add(SingleChild(
            "quarantined_span_harness",
            "span_id",
            "quarantined_span",
            [NonEmptyText("harness_id")],
            Idx("quarantined_span_harness_by_harness", "harness_id")));

        tables.Add(new TableDefinition(
            Name: "ingest_problem",
            Columns:
            [
                IntCol("problem_id"),
                NonEmptyText("problem_key"),
                NonEmptyText("severity"),
                NonEmptyText("code"),
                NonEmptyText("message"),
            ],
            PrimaryKeyColumns: ["problem_id"],
            ForeignKeys: [],
            Indexes: [UniqueIdx("ingest_problem_by_key", "problem_key")]));

        tables.Add(SingleChild(
            "ingest_problem_span",
            "problem_id",
            "ingest_problem",
            [NonEmptyText("span_id")],
            Idx("ingest_problem_span_by_span", "span_id")));
        tables.Add(SingleChild("ingest_problem_location", "problem_id", "ingest_problem", [NonEmptyText("location")]));
        tables.Add(SingleChild("ingest_problem_session", "problem_id", "ingest_problem", [NonEmptyText("session_id")]));
        tables.Add(SingleChild(
            "ingest_problem_harness",
            "problem_id",
            "ingest_problem",
            [NonEmptyText("harness_id")],
            Idx("ingest_problem_harness_by_harness", "harness_id")));
        tables.Add(SingleChild("ingest_problem_timestamp", "problem_id", "ingest_problem", [NonEmptyText("timestamp_utc")]));
        tables.Add(SingleChild("ingest_problem_stack", "problem_id", "ingest_problem", [NonEmptyText("stack")]));

        tables.Add(new TableDefinition(
            Name: "ingest_log",
            Columns:
            [
                IntCol("ingest_id"),
                NonEmptyText("source"),
                NonNegInt("record_count"),
                NonEmptyText("timestamp_utc"),
            ],
            PrimaryKeyColumns: ["ingest_id"],
            ForeignKeys: [],
            Indexes:
            [
                Idx("ingest_log_by_source", "source"),
                Idx("ingest_log_by_timestamp", "timestamp_utc"),
            ]));

        tables.Add(new TableDefinition(
            Name: "token_cache",
            Columns: [NonEmptyText("hash"), NonNegInt("token_count")],
            PrimaryKeyColumns: ["hash"],
            ForeignKeys: [],
            Indexes: []));

        tables.Add(SingleChild("token_cache_model", "hash", "token_cache", [NonEmptyText("model_id")]));

        tables.Add(new TableDefinition(
            Name: "model_context_window_catalog",
            Columns:
            [
                NonEmptyText("lookup_id"),
                NonEmptyText("canonical_model_id"),
                PosInt("context_window"),
                NonEmptyText("vendor"),
                NonEmptyText("documentation_url"),
                NonEmptyText("retrieved_at_utc"),
            ],
            PrimaryKeyColumns: ["lookup_id"],
            ForeignKeys: [],
            Indexes: [Idx("model_context_window_catalog_by_vendor", "vendor")]));

        tables.Add(SingleChild(
            "model_context_window_source_revision",
            "lookup_id",
            "model_context_window_catalog",
            [NonEmptyText("source_revision")]));

        tables.Add(new TableDefinition(
            Name: "model_context_window_catalog_vendor",
            Columns: [NonEmptyText("vendor"), NonEmptyText("status")],
            PrimaryKeyColumns: ["vendor"],
            ForeignKeys: [],
            Indexes: []));

        tables.Add(SingleChild(
            "model_context_window_catalog_vendor_refresh",
            "vendor",
            "model_context_window_catalog_vendor",
            [NonEmptyText("last_refresh_at_utc")]));
        tables.Add(SingleChild(
            "model_context_window_catalog_vendor_error",
            "vendor",
            "model_context_window_catalog_vendor",
            [NonEmptyText("last_error")]));

        tables.Add(new TableDefinition(
            Name: "metric_point",
            Columns:
            [
                IntCol("point_id"),
                NonEmptyText("point_key"),
                NonEmptyText("name"),
                NonEmptyText("kind"),
            ],
            PrimaryKeyColumns: ["point_id"],
            ForeignKeys: [],
            Indexes:
            [
                UniqueIdx("metric_point_by_key", "point_key"),
                Idx("metric_point_by_name", "name"),
            ]));

        tables.Add(MetricPointChild("metric_point_unit", NonEmptyText("unit")));
        tables.Add(MetricPointChild("metric_point_temporality", IntCol("aggregation_temporality")));
        tables.Add(MetricPointChild("metric_point_time", NonEmptyText("time_unix_nano")));
        tables.Add(MetricPointChild("metric_point_start_time", NonEmptyText("start_time_unix_nano")));
        tables.Add(MetricPointChild("metric_point_timestamp", NonEmptyText("timestamp_utc")));
        tables.Add(MetricPointChild("metric_point_value", RealCol("value")));
        tables.Add(MetricPointChild("metric_point_count", IntCol("point_count")));
        tables.Add(MetricPointChild("metric_point_sum", RealCol("point_sum")));
        tables.Add(MetricPointChild("metric_point_source", NonEmptyText("source")));

        tables.Add(new TableDefinition(
            Name: "source_checkpoint",
            Columns:
            [
                NonEmptyText("harness_id"),
                NonEmptyText("source_key"),
                NonEmptyText("provider_id"),
                NonEmptyText("parser_id"),
                NonEmptyText("parser_contract_version"),
                NonEmptyText("format"),
                TextCol("source_root_label"),
                TextCol("revision_token"),
                NonEmptyText("covered_from_utc"),
                NonEmptyText("covered_through_utc"),
                NonEmptyText("last_attempt_utc"),
                NonEmptyText("last_status"),
                NonNegInt("unit_count"),
                NonNegInt("record_count"),
            ],
            PrimaryKeyColumns: ["harness_id", "source_key"],
            ForeignKeys: [],
            Indexes: [Idx("source_checkpoint_by_harness", "harness_id")]));

        tables.Add(new TableDefinition(
            Name: "source_checkpoint_success",
            Columns:
            [
                NonEmptyText("harness_id"),
                NonEmptyText("source_key"),
                NonEmptyText("last_success_utc"),
            ],
            PrimaryKeyColumns: ["harness_id", "source_key"],
            ForeignKeys: [CompositeFk(["harness_id", "source_key"], "source_checkpoint", ["harness_id", "source_key"])],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "source_checkpoint_error",
            Columns:
            [
                NonEmptyText("harness_id"),
                NonEmptyText("source_key"),
                NonEmptyText("last_error_code"),
            ],
            PrimaryKeyColumns: ["harness_id", "source_key"],
            ForeignKeys: [CompositeFk(["harness_id", "source_key"], "source_checkpoint", ["harness_id", "source_key"])],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "record_provenance",
            Columns:
            [
                NonEmptyText("span_id"),
                NonEmptyText("harness_id"),
                NonEmptyText("source_key"),
                TextCol("source_revision"),
                TextCol("parser_version"),
                NonEmptyText("imported_at_utc"),
                TextCol("location_token"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [Idx("record_provenance_by_source", "harness_id", "source_key")]));

        tables.Add(SingleChild(
            "record_provenance_native_session",
            "span_id",
            "record_provenance",
            [NonEmptyText("native_session_id")],
            Idx("record_provenance_native_session_by_id", "native_session_id")));

        tables.Add(SingleChild(
            "record_provenance_native_record",
            "span_id",
            "record_provenance",
            [NonEmptyText("native_record_id")],
            Idx("record_provenance_native_record_by_id", "native_record_id")));

        tables.Add(new TableDefinition(
            Name: "refresh_run",
            Columns:
            [
                NonEmptyText("refresh_run_id"),
                NonEmptyText("started_at_utc"),
                NonEmptyText("status"),
                IntCol("pid"),
                NonEmptyText("trigger"),
            ],
            PrimaryKeyColumns: ["refresh_run_id"],
            ForeignKeys: [],
            Indexes:
            [
                DescIdx("refresh_run_by_started_at", "started_at_utc"),
                new IndexDefinition(
                    "refresh_run_by_status",
                    [
                        new IndexColumnDefinition("status"),
                        new IndexColumnDefinition("started_at_utc", IndexSortDirection.Descending),
                    ]),
            ]));

        tables.Add(SingleChild("refresh_run_completion", "refresh_run_id", "refresh_run", [NonEmptyText("completed_at_utc")]));
        tables.Add(SingleChild("refresh_run_summary", "refresh_run_id", "refresh_run", [NonEmptyText("summary")]));
        tables.Add(SingleChild("refresh_run_history_window", "refresh_run_id", "refresh_run", [PosInt("history_weeks")]));
    }

    private static ColumnDefinition TextCol(string name) =>
        new(name, SqliteColumnType.SqliteText, IsNotNull: true);

    private static ColumnDefinition NonEmptyText(string name) =>
        new(name, SqliteColumnType.SqliteText, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"length({name}) > 0"));

    private static ColumnDefinition CheckedText(string name, string checkConstraint) =>
        new(name, SqliteColumnType.SqliteText, IsNotNull: true, CheckConstraint: checkConstraint);

    private static ColumnDefinition EnumText(string name, string allowedLiterals) =>
        new(name, SqliteColumnType.SqliteText, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"{name} IN ({allowedLiterals})"));

    private static ColumnDefinition IntCol(string name) =>
        new(name, SqliteColumnType.SqliteInteger, IsNotNull: true);

    private static ColumnDefinition NonNegInt(string name) =>
        new(name, SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"{name} >= 0"));

    private static ColumnDefinition PosInt(string name) =>
        new(name, SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"{name} > 0"));

    private static ColumnDefinition CheckedInt(string name, string checkConstraint) =>
        new(name, SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: checkConstraint);

    private static ColumnDefinition BoolInt(string name) =>
        new(name, SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"{name} IN (0, 1)"));

    private static ColumnDefinition PositionCol() =>
        NonNegInt("position");

    private static ColumnDefinition SingletonIdCol() =>
        new("singleton_id", SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: "singleton_id = 1");

    private static ColumnDefinition RealCol(string name) =>
        new(name, SqliteColumnType.SqliteReal, IsNotNull: true);

    private static ColumnDefinition NonNegReal(string name) =>
        new(name, SqliteColumnType.SqliteReal, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"{name} >= 0.0"));

    private static ForeignKeyDefinition Fk(string column, string referencedTable, string referencedColumn) =>
        new([column], referencedTable, [referencedColumn], ForeignKeyDeleteAction.Cascade);

    private static ForeignKeyDefinition CompositeFk(
        IReadOnlyList<string> columns,
        string referencedTable,
        IReadOnlyList<string> referencedColumns) =>
        new(columns, referencedTable, referencedColumns, ForeignKeyDeleteAction.Cascade);

    private static IndexDefinition Idx(string name, params string[] columns) =>
        new(name, [.. columns.Select(static col => new IndexColumnDefinition(col))], IsUnique: false);

    private static IndexDefinition UniqueIdx(string name, params string[] columns) =>
        new(name, [.. columns.Select(static col => new IndexColumnDefinition(col))], IsUnique: true);

    private static IndexDefinition DescIdx(string name, string column) =>
        new(name, [new IndexColumnDefinition(column, IndexSortDirection.Descending)], IsUnique: false);

    private static TableDefinition SingleChild(
        string tableName,
        string keyColumn,
        string parentTable,
        ColumnDefinition[] valueColumns,
        params IndexDefinition[] indexes)
    {
        ColumnDefinition keyDefinition = parentTable is "ingest_problem" or "metric_point"
            ? IntCol(keyColumn)
            : NonEmptyText(keyColumn);

        return new TableDefinition(
            Name: tableName,
            Columns: [keyDefinition, .. valueColumns],
            PrimaryKeyColumns: [keyColumn],
            ForeignKeys: [Fk(keyColumn, parentTable, keyColumn)],
            Indexes: indexes);
    }

    private static TableDefinition SingletonTable(string tableName, params ColumnDefinition[] valueColumns) =>
        new(
            Name: tableName,
            Columns: [SingletonIdCol(), .. valueColumns],
            PrimaryKeyColumns: ["singleton_id"],
            ForeignKeys: [],
            Indexes: []);

    private static TableDefinition RecordSingleText(string tableName, string columnName, bool nonEmpty, params IndexDefinition[] indexes) =>
        SingleChild(tableName, "span_id", "record", [nonEmpty ? NonEmptyText(columnName) : TextCol(columnName)], indexes);

    private static TableDefinition RecordSingleNonNegInt(string tableName, string columnName) =>
        SingleChild(tableName, "span_id", "record", [NonNegInt(columnName)]);

    private static TableDefinition ContentPartExtension(string tableName, params ColumnDefinition[] valueColumns) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("span_id"), PositionCol(), .. valueColumns],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [CompositeFk(["span_id", "position"], "content_part", ["span_id", "position"])],
            Indexes: [],
            IsRepeatedItem: true);

    private static TableDefinition ToolCallExtension(
        string tableName,
        ColumnDefinition[] valueColumns,
        params IndexDefinition[] indexes) =>
        SingleChild(tableName, "span_id", "tool_call", valueColumns, indexes);

    private static TableDefinition SessionContextTurnExtension(string tableName, params ColumnDefinition[] valueColumns) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("session_id"), PositionCol(), .. valueColumns],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_context_turn", ["session_id", "position"])],
            Indexes: [],
            IsRepeatedItem: true);

    private static TableDefinition MetricPointChild(string tableName, ColumnDefinition valueColumn) =>
        SingleChild(tableName, "point_id", "metric_point", [valueColumn]);
}
