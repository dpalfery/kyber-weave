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
/// content parts, tool definitions, tool calls, agent invocations, sessions, and turns.
/// </summary>
/// <remarks>
/// <para>
/// Every column in the catalog is <c>NOT NULL</c> and typed solely as <c>INTEGER</c>, <c>REAL</c>, or
/// <c>TEXT</c>. Optional attributes live in 0..1 child tables linked via <c>ON DELETE CASCADE</c>
/// rather than nullable columns, and ordered collections carry an explicit <c>position</c> integer.
/// </para>
/// <para>
/// Self-referential or cross-entity links whose target span or session can arrive out of order (such
/// as <c>record_parent.parent_span_id</c>, <c>record_session_link.session_key</c>,
/// <c>session_parent.parent_session_id</c>, and <c>agent_invocation_parent_tool_call.parent_tool_span_id</c>)
/// store indexed scalar identifiers without a parent-row foreign key so child-first OTLP batches and
/// pre-projection records never fail insertion under <c>PRAGMA foreign_keys = ON</c>.
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

        string? nearest = FindNearestTableName(tableName);
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

    private static string? FindNearestTableName(string candidate)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string name in CatalogTables.Select(static table => table.Name))
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
        AddSessionTurnTables(definitions);
        return ValidateAndOrderCatalog(definitions);
    }

    private static void AddHarnessAndSourceTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "harness",
            Columns:
            [
                NonEmptyText("harness_id"),
                NonEmptyText("survey_family"),
                NonEmptyText("display_label"),
            ],
            PrimaryKeyColumns: ["harness_id"],
            ForeignKeys: [],
            Indexes:
            [
                Idx("harness_by_family", "survey_family"),
            ]));

        tables.Add(new TableDefinition(
            Name: "source",
            Columns:
            [
                NonEmptyText("source_id"),
                NonEmptyText("harness_id"),
                NonEmptyText("source_key"),
                NonEmptyText("provider_id"),
                NonEmptyText("parser_id"),
                NonEmptyText("parser_contract_version"),
                NonEmptyText("format"),
                NonEmptyText("source_root_label"),
            ],
            PrimaryKeyColumns: ["source_id"],
            ForeignKeys:
            [
                Fk("harness_id", "harness", "harness_id"),
            ],
            Indexes:
            [
                UniqueIdx("source_by_harness_key", "harness_id", "source_key"),
                Idx("source_by_harness", "harness_id"),
            ]));
    }

    private static void AddRecordTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "record",
            Columns:
            [
                NonEmptyText("span_id"),
                NonEmptyText("harness_id"),
                NonEmptyText("source_id"),
                NonEmptyText("name"),
                EnumText("op", "'llm.invoke', 'tool.invoke', 'agent.invoke', 'unspecified'"),
                EnumText("kind", "'unspecified', 'internal', 'server', 'client', 'producer', 'consumer'"),
                NonEmptyText("timestamp_utc"),
                NonNegInt("duration_ms"),
                EnumText("status", "'unset', 'ok', 'error', 'unspecified'"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys:
            [
                Fk("harness_id", "harness", "harness_id"),
                Fk("source_id", "source", "source_id"),
            ],
            Indexes:
            [
                Idx("record_by_harness_time", "harness_id", "timestamp_utc"),
                Idx("record_by_source", "source_id"),
                Idx("record_by_timestamp", "timestamp_utc"),
                Idx("record_by_op", "op"),
            ]));

        tables.Add(RecordSingleText(
            "record_trace",
            "trace_id",
            nonEmpty: true,
            Idx("record_trace_by_trace_id", "trace_id")));

        tables.Add(RecordSingleText(
            "record_parent",
            "parent_span_id",
            nonEmpty: true,
            Idx("record_parent_by_parent_span", "parent_span_id")));

        tables.Add(RecordSingleText(
            "record_session_link",
            "session_key",
            nonEmpty: true,
            Idx("record_session_link_by_session_key", "session_key")));

        tables.Add(new TableDefinition(
            Name: "record_tokens",
            Columns:
            [
                NonEmptyText("span_id"),
                NonNegInt("fresh_input"),
                NonNegInt("cache_read"),
                NonNegInt("cache_creation"),
                NonNegInt("output"),
                NonNegInt("reported_input"),
                NonNegInt("reported_output"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: []));

        tables.Add(RecordSingleInt("record_reasoning_tokens", "reasoning", nonNegative: true));

        tables.Add(new TableDefinition(
            Name: "record_cost",
            Columns:
            [
                NonEmptyText("span_id"),
                EnumText("basis", "'unknown', 'published', 'harness'"),
                EnumText("status", "'priced', 'partial', 'no_rate', 'out_of_scope', 'not_billed'"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: []));

        tables.Add(RecordSingleReal("record_cost_value", "cost_amount"));
        tables.Add(RecordSingleText("record_cost_currency", "currency", nonEmpty: true));

        tables.Add(new TableDefinition(
            Name: "record_cost_by_model",
            Columns:
            [
                NonEmptyText("span_id"),
                PositionCol(),
                NonEmptyText("model_id"),
                RealCol("cost_amount"),
            ],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [UniqueIdx("record_cost_by_model_unique_model", "span_id", "model_id")],
            IsRepeatedItem: true));

        tables.Add(RecordSingleText(
            "record_model",
            "model_id",
            nonEmpty: true,
            Idx("record_model_by_model_id", "model_id")));
        tables.Add(RecordSingleText("record_provider", "provider_id", nonEmpty: true));
        tables.Add(RecordSinglePositiveInt("record_context_window", "context_window"));
        tables.Add(RecordSinglePositiveInt("record_declared_context_window", "declared_context_window"));

        tables.Add(new TableDefinition(
            Name: "record_provenance",
            Columns:
            [
                NonEmptyText("span_id"),
                NonEmptyText("harness_id"),
                NonEmptyText("source_id"),
                NonEmptyText("native_record_id"),
                TextCol("source_revision"),
                TextCol("parser_contract_version"),
                NonEmptyText("imported_at_utc"),
                TextCol("location_token"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys:
            [
                Fk("span_id", "record", "span_id"),
                Fk("harness_id", "harness", "harness_id"),
                Fk("source_id", "source", "source_id"),
            ],
            Indexes:
            [
                Idx("record_provenance_by_source", "harness_id", "source_id"),
                Idx("record_provenance_by_native", "harness_id", "native_record_id"),
            ]));

        tables.Add(new TableDefinition(
            Name: "record_provenance_native_session",
            Columns: [NonEmptyText("span_id"), NonEmptyText("native_session_id")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record_provenance", "span_id")],
            Indexes: [Idx("record_provenance_native_session_by_id", "native_session_id")]));

        tables.Add(new TableDefinition(
            Name: "record_provenance_digest",
            Columns: [NonEmptyText("span_id"), NonEmptyText("record_digest")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record_provenance", "span_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "record_provenance_parser",
            Columns: [NonEmptyText("span_id"), NonEmptyText("format"), NonEmptyText("parser_id")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record_provenance", "span_id")],
            Indexes: []));

        tables.Add(RecordSingleText(
            "record_deduplication",
            "deduplication_key",
            nonEmpty: true,
            Idx("record_deduplication_by_key", "deduplication_key")));
        tables.Add(RecordSingleText("record_turn_identity", "turn_id", nonEmpty: true));
        tables.Add(RecordSingleText("record_native_identity", "record_identity", nonEmpty: true));
        tables.Add(RecordSingleText("record_initiator", "initiator", nonEmpty: true));
        tables.Add(RecordSingleText("record_session_label", "session_label", nonEmpty: true));
        tables.Add(RecordSingleText("record_termination", "termination_reason", nonEmpty: true));
        tables.Add(RecordSingleInt("record_exit_code", "exit_code", nonNegative: false));
        tables.Add(RecordSingleBool("record_correction", "is_correction"));

        tables.Add(new TableDefinition(
            Name: "record_correction_rule",
            Columns: [NonEmptyText("span_id"), NonEmptyText("correction_rule")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record_correction", "span_id")],
            Indexes: []));

        tables.Add(RecordSingleInt("record_active_duration", "active_duration_ms", nonNegative: true));
        tables.Add(RecordSingleInt("record_web_search_requests", "request_count", nonNegative: true));

        tables.Add(new TableDefinition(
            Name: "record_measurability",
            Columns:
            [
                NonEmptyText("span_id"),
                PositionCol(),
                NonEmptyText("metric_key"),
                EnumText("availability", "'measured', 'derived', 'not_measurable'"),
            ],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [UniqueIdx("record_measurability_by_metric", "span_id", "metric_key")],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "record_measurability_reason",
            Columns: [NonEmptyText("span_id"), PositionCol(), NonEmptyText("reason")],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [CompositeFk(["span_id", "position"], "record_measurability", ["span_id", "position"])],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "record_content_bucket",
            Columns:
            [
                NonEmptyText("span_id"),
                PositionCol(),
                EnumText(
                    "bucket_key",
                    "'system_prompt', 'tool_definitions', 'tool_definition', 'instruction_context', 'conversation_history', 'tool_result_content', 'user_prompt'"),
                TextCol("content_text"),
            ],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [UniqueIdx("record_content_bucket_by_key", "span_id", "bucket_key")],
            IsRepeatedItem: true));

        tables.Add(RecordRepeatedText("record_skill_invoked", "skill_name", nonEmpty: true));
        tables.Add(RecordSingleBool("record_skill_listing", "is_initial"));

        tables.Add(new TableDefinition(
            Name: "record_skill_listed",
            Columns: [NonEmptyText("span_id"), PositionCol(), NonEmptyText("skill_name")],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [Fk("span_id", "record_skill_listing", "span_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "record_skill_listed_description",
            Columns: [NonEmptyText("span_id"), PositionCol(), TextCol("description_text")],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [CompositeFk(["span_id", "position"], "record_skill_listed", ["span_id", "position"])],
            Indexes: []));

        tables.Add(RecordRepeatedText("record_bash_command", "command_text", nonEmpty: false));
        tables.Add(RecordRepeatedText("record_tool_sequence_entry", "tool_name", nonEmpty: true));
        tables.Add(RecordRepeatedText("record_offered_tool", "tool_name", nonEmpty: true));
        tables.Add(RecordSingleText("record_repo", "repo_name", nonEmpty: true));
        tables.Add(RecordSingleText("record_branch", "branch_name", nonEmpty: true));
        tables.Add(RecordSingleText("record_agent_attribute", "agent_name", nonEmpty: true));
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
                    "part_kind",
                    "'system_prompt', 'tool_definitions', 'tool_definition', 'instruction_context', 'conversation_history', 'tool_result_content', 'user_prompt'"),
                TextCol("part_text"),
            ],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [Idx("content_part_by_kind", "span_id", "part_kind")],
            IsRepeatedItem: true));

        tables.Add(ContentPartExtension("content_part_tokens", NonNegInt("token_count")));
        tables.Add(ContentPartExtension("content_part_server", NonEmptyText("server_name")));
        tables.Add(ContentPartExtension("content_part_role", NonEmptyText("role_name")));
        tables.Add(ContentPartExtension("content_part_resident", BoolInt("is_resident")));
        tables.Add(ContentPartExtension("content_part_truncated", BoolInt("is_truncated")));
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
                TextCol("definition_text"),
                BoolInt("is_resident"),
            ],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [Idx("tool_definition_by_name", "tool_name")],
            IsRepeatedItem: true));

        tables.Add(ToolDefinitionExtension("tool_definition_server", NonEmptyText("server_name")));
        tables.Add(ToolDefinitionExtension("tool_definition_tokens", NonNegInt("token_count")));
        tables.Add(ToolDefinitionExtension("tool_definition_description", TextCol("description_text")));
    }

    private static void AddToolCallTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "tool_call",
            Columns:
            [
                NonEmptyText("span_id"),
                PositionCol(),
                TextCol("call_id"),
                NonEmptyText("tool_name"),
                EnumText("status", "'unset', 'ok', 'error'"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes:
            [
                Idx("tool_call_by_call_id", "call_id"),
                Idx("tool_call_by_name", "tool_name"),
                Idx("tool_call_by_status", "status"),
            ],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "tool_call_parent",
            Columns: [NonEmptyText("span_id"), NonEmptyText("parent_span_id")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "tool_call", "span_id")],
            Indexes: [Idx("tool_call_parent_by_parent", "parent_span_id")]));

        tables.Add(new TableDefinition(
            Name: "tool_call_arguments",
            Columns:
            [
                NonEmptyText("span_id"),
                TextCol("arguments_text"),
                NonNegInt("arguments_bytes"),
                BoolInt("is_truncated"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "tool_call", "span_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "tool_call_arguments_hash",
            Columns: [NonEmptyText("span_id"), NonEmptyText("arguments_hash")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "tool_call", "span_id")],
            Indexes: [Idx("tool_call_arguments_hash_by_hash", "arguments_hash")]));

        tables.Add(new TableDefinition(
            Name: "tool_call_result",
            Columns:
            [
                NonEmptyText("span_id"),
                TextCol("result_text"),
                NonNegInt("result_bytes"),
                BoolInt("is_error"),
                BoolInt("is_truncated"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "tool_call", "span_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "tool_call_timing",
            Columns:
            [
                NonEmptyText("span_id"),
                NonEmptyText("started_at_utc"),
                NonNegInt("duration_ms"),
                EnumText("duration_source", "'measured', 'derived'"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "tool_call", "span_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "tool_call_completed_at",
            Columns: [NonEmptyText("span_id"), NonEmptyText("completed_at_utc")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "tool_call", "span_id")],
            Indexes: []));
    }

    private static void AddAgentInvocationTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "agent_invocation",
            Columns:
            [
                NonEmptyText("span_id"),
                PositionCol(),
                NonEmptyText("agent_name"),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [Idx("agent_invocation_by_name", "agent_name")],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "agent_invocation_agent_id",
            Columns: [NonEmptyText("span_id"), NonEmptyText("native_agent_id")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "agent_invocation", "span_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "agent_invocation_parent_tool_call",
            Columns: [NonEmptyText("span_id"), NonEmptyText("parent_tool_span_id")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "agent_invocation", "span_id")],
            Indexes: [Idx("agent_invocation_by_parent_tool", "parent_tool_span_id")]));

        tables.Add(new TableDefinition(
            Name: "agent_invocation_target_session",
            Columns: [NonEmptyText("span_id"), NonEmptyText("target_session_key")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "agent_invocation", "span_id")],
            Indexes: [Idx("agent_invocation_by_target_session", "target_session_key")]));

        tables.Add(new TableDefinition(
            Name: "agent_invocation_description",
            Columns: [NonEmptyText("span_id"), TextCol("description_text")],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "agent_invocation", "span_id")],
            Indexes: []));
    }

    private static void AddSessionTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "session",
            Columns:
            [
                NonEmptyText("session_id"),
                NonEmptyText("harness_id"),
                BoolInt("is_subagent"),
                NonNegInt("span_count"),
                NonNegInt("turn_count"),
                NonNegInt("request_count"),
                NonNegInt("duration_ms"),
                NonNegInt("problems_count"),
                EnumText("sync_status", "'pending', 'synced', 'settled', 'error'"),
            ],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("harness_id", "harness", "harness_id")],
            Indexes: [Idx("session_by_harness", "harness_id")]));

        tables.Add(SessionSingleText("session_label", "label", nonEmpty: true));

        tables.Add(new TableDefinition(
            Name: "session_parent",
            Columns:
            [
                NonEmptyText("session_id"),
                NonEmptyText("parent_session_id"),
                BoolInt("included_in_parent"),
            ],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: [Idx("session_parent_by_parent", "parent_session_id")]));

        tables.Add(SessionSingleText("session_agent", "agent_name", nonEmpty: true));
        tables.Add(SessionSingleText("session_repository", "repo_name", nonEmpty: true));
        tables.Add(SessionSingleText("session_branch", "branch_name", nonEmpty: true));

        tables.Add(new TableDefinition(
            Name: "session_started",
            Columns: [NonEmptyText("session_id"), NonEmptyText("started_at_utc")],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes:
            [
                new IndexDefinition(
                    "session_started_by_started_desc",
                    [new IndexColumnDefinition("started_at_utc", IndexSortDirection.Descending)]),
            ]));

        tables.Add(SessionSingleText("session_ended", "ended_at_utc", nonEmpty: true));
        tables.Add(SessionSingleText("session_sync_state", "synced_at_utc", nonEmpty: true));
        tables.Add(SessionSingleText("session_settled", "settled_at_utc", nonEmpty: true));

        tables.Add(new TableDefinition(
            Name: "session_transcript",
            Columns:
            [
                NonEmptyText("session_id"),
                NonEmptyText("transcript_path"),
                NonNegInt("transcript_offset"),
            ],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: []));

        tables.Add(SessionSingleNonNegInt("session_total_input", "total_input"));
        tables.Add(SessionSingleNonNegInt("session_total_output", "total_output"));
        tables.Add(SessionSingleNonNegInt("session_total_cache_read", "total_cache_read"));
        tables.Add(SessionSingleNonNegInt("session_total_cache_creation", "total_cache_creation"));

        tables.Add(new TableDefinition(
            Name: "session_cache_hit_ratio",
            Columns:
            [
                NonEmptyText("session_id"),
                new ColumnDefinition("cache_hit_ratio", SqliteColumnType.SqliteReal, IsNotNull: true, CheckConstraint: "cache_hit_ratio >= 0.0"),
            ],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: []));

        tables.Add(SessionSingleNonNegInt("session_cache_creation_coverage", "turns_with_cache_creation"));
        tables.Add(SessionSingleNonNegInt("session_tool_call_count", "tool_call_count"));

        tables.Add(new TableDefinition(
            Name: "session_cost",
            Columns:
            [
                NonEmptyText("session_id"),
                EnumText("basis", "'unknown', 'published', 'harness'"),
                EnumText("status", "'priced', 'partial', 'no_rate', 'out_of_scope', 'not_billed'"),
            ],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_cost_amount",
            Columns: [NonEmptyText("session_id"), RealCol("cost_amount"), NonEmptyText("currency")],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session_cost", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_cost_basis_mismatch",
            Columns: [NonEmptyText("session_id"), NonEmptyText("mismatch_code"), NonEmptyText("mismatch_message")],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_cost_basis_mismatch_basis",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                EnumText("basis", "'unknown', 'published', 'harness'"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session_cost_basis_mismatch", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_cost_basis_mismatch_total",
            Columns: [NonEmptyText("session_id"), PositionCol(), RealCol("basis_total")],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_cost_basis_mismatch_basis", ["session_id", "position"])],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_auxiliary_cost",
            Columns:
            [
                NonEmptyText("session_id"),
                EnumText("basis", "'unknown', 'published', 'harness'"),
                EnumText("status", "'priced', 'partial', 'no_rate', 'out_of_scope', 'not_billed'"),
            ],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_auxiliary_cost_amount",
            Columns: [NonEmptyText("session_id"), RealCol("cost_amount"), NonEmptyText("currency")],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session_auxiliary_cost", "session_id")],
            Indexes: []));

        tables.Add(SessionRepeatedText("session_model", "model_id"));
        tables.Add(SessionRepeatedText("session_tool_invoked", "tool_name"));
        tables.Add(SessionRepeatedText("session_tool_offered", "tool_name"));

        tables.Add(new TableDefinition(
            Name: "session_context_summary",
            Columns:
            [
                NonEmptyText("session_id"),
                BoolInt("is_measurable"),
                new ColumnDefinition("context_limit", SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: "context_limit > 0"),
                NonNegInt("analyzed_turn_count"),
                IntCol("residual_total"),
                BoolInt("derived_counts"),
                RealCol("fresh_jump_factor"),
                RealCol("accumulation_rate"),
            ],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_context_limit_source",
            Columns: [NonEmptyText("session_id"), NonEmptyText("limit_source")],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session_context_summary", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_context_unmeasurable_reason",
            Columns: [NonEmptyText("session_id"), NonEmptyText("reason")],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session_context_summary", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_context_observed_part",
            Columns: [NonEmptyText("session_id"), PositionCol(), NonEmptyText("part_kind")],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session_context_summary", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_context_flagged_turn",
            Columns: [NonEmptyText("session_id"), PositionCol(), NonNegInt("turn_index")],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session_context_summary", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_tool_summary",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("tool_name"),
                NonNegInt("schema_tokens"),
                NonNegInt("invocations"),
                NonNegInt("turns_resident"),
                NonNegInt("errors"),
                NonNegInt("total_schema_cost"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_tool_summary_server",
            Columns: [NonEmptyText("session_id"), PositionCol(), NonEmptyText("server_name")],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_tool_summary", ["session_id", "position"])],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_mcp_server",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("server_name"),
                BoolInt("is_mcp"),
                NonNegInt("tool_count"),
                NonNegInt("schema_tokens"),
                NonNegInt("invocations"),
                NonNegInt("unused_tools"),
                NonNegInt("unused_cost"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_schema_analysis",
            Columns:
            [
                NonEmptyText("session_id"),
                BoolInt("is_measurable"),
                NonNegInt("invocation_count"),
                NonNegInt("turns_analyzed"),
            ],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_schema_unmeasurable_reason",
            Columns: [NonEmptyText("session_id"), NonEmptyText("reason")],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session_schema_analysis", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_schema_unused_range",
            Columns: [NonEmptyText("session_id"), RealCol("lower_cost"), RealCol("upper_cost")],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session_schema_analysis", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_schema_unused_range_currency",
            Columns: [NonEmptyText("session_id"), NonEmptyText("currency")],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session_schema_unused_range", "session_id")],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_request",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("request_span_id"),
                NonEmptyText("timestamp_utc"),
                NonNegInt("turn_count"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_request_model",
            Columns: [NonEmptyText("session_id"), PositionCol(), NonEmptyText("model_id")],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_request", ["session_id", "position"])],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_request_prompt",
            Columns: [NonEmptyText("session_id"), PositionCol(), TextCol("prompt_preview")],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_request", ["session_id", "position"])],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_reconciliation",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("request_span_id"),
                IntCol("root_input"),
                IntCol("sum_chat_input"),
                BoolInt("input_match"),
                IntCol("root_output"),
                IntCol("sum_chat_output"),
                BoolInt("output_match"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_subagent",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("subagent_span_id"),
                NonEmptyText("subagent_name"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: [],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_subagent_parent",
            Columns: [NonEmptyText("session_id"), PositionCol(), NonEmptyText("parent_span_id")],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_subagent", ["session_id", "position"])],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_measurability",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("metric_key"),
                EnumText("availability", "'measured', 'derived', 'not_measurable'"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: [UniqueIdx("session_measurability_by_metric", "session_id", "metric_key")],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_measurability_reason",
            Columns: [NonEmptyText("session_id"), PositionCol(), NonEmptyText("reason")],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_measurability", ["session_id", "position"])],
            Indexes: []));
    }

    private static void AddSessionTurnTables(List<TableDefinition> tables)
    {
        tables.Add(new TableDefinition(
            Name: "session_turn",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonEmptyText("timestamp_utc"),
                NonNegInt("input_tokens"),
                NonNegInt("output_tokens"),
                NonNegInt("fresh_input"),
                NonNegInt("cache_read"),
                NonNegInt("cache_creation"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: [Idx("session_turn_by_session", "session_id", "position")],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_turn_span",
            Columns: [NonEmptyText("session_id"), PositionCol(), NonEmptyText("span_id")],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_turn", ["session_id", "position"])],
            Indexes: [Idx("session_turn_span_by_span", "span_id")]));

        tables.Add(SessionTurnExtension("session_turn_model", NonEmptyText("model_id")));
        tables.Add(SessionTurnExtension("session_turn_reasoning", NonNegInt("reasoning_tokens")));

        tables.Add(new TableDefinition(
            Name: "session_turn_pressure",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonNegInt("builtin_tool_definition_tokens"),
                NonNegInt("stripped_instruction_count"),
                NonNegInt("stripped_instruction_tokens"),
                NonNegInt("bucketed_tokens"),
                IntCol("residual_tokens"),
                NonEmptyText("residual_attribution"),
                IntCol("headroom"),
                RealCol("pressure"),
                IntCol("accumulation_rate"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_turn", ["session_id", "position"])],
            Indexes: []));

        tables.Add(new TableDefinition(
            Name: "session_turn_bucket",
            Columns:
            [
                NonEmptyText("session_id"),
                new ColumnDefinition("turn_position", SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: "turn_position >= 0"),
                PositionCol(),
                NonEmptyText("bucket_key"),
                NonNegInt("token_count"),
            ],
            PrimaryKeyColumns: ["session_id", "turn_position", "position"],
            ForeignKeys: [CompositeFk(["session_id", "turn_position"], "session_turn", ["session_id", "position"])],
            Indexes: [UniqueIdx("session_turn_bucket_by_key", "session_id", "turn_position", "bucket_key")],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_turn_tool_server",
            Columns:
            [
                NonEmptyText("session_id"),
                new ColumnDefinition("turn_position", SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: "turn_position >= 0"),
                PositionCol(),
                NonEmptyText("server_name"),
                NonNegInt("token_count"),
            ],
            PrimaryKeyColumns: ["session_id", "turn_position", "position"],
            ForeignKeys: [CompositeFk(["session_id", "turn_position"], "session_turn", ["session_id", "position"])],
            Indexes: [UniqueIdx("session_turn_tool_server_by_server", "session_id", "turn_position", "server_name")],
            IsRepeatedItem: true));

        tables.Add(new TableDefinition(
            Name: "session_turn_fresh_jump",
            Columns:
            [
                NonEmptyText("session_id"),
                PositionCol(),
                NonNegInt("previous_fresh_input"),
                RealCol("jump_factor"),
            ],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_turn", ["session_id", "position"])],
            Indexes: []));

        tables.Add(SessionTurnExtension("session_turn_non_resident_tools", NonNegInt("non_resident_count")));
    }

    private static ColumnDefinition TextCol(string name) =>
        new(name, SqliteColumnType.SqliteText, IsNotNull: true);

    private static ColumnDefinition NonEmptyText(string name) =>
        new(name, SqliteColumnType.SqliteText, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"length({name}) > 0"));

    private static ColumnDefinition EnumText(string name, string allowedLiterals) =>
        new(name, SqliteColumnType.SqliteText, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"{name} IN ({allowedLiterals})"));

    private static ColumnDefinition IntCol(string name) =>
        new(name, SqliteColumnType.SqliteInteger, IsNotNull: true);

    private static ColumnDefinition NonNegInt(string name) =>
        new(name, SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"{name} >= 0"));

    private static ColumnDefinition BoolInt(string name) =>
        new(name, SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"{name} IN (0, 1)"));

    private static ColumnDefinition PositionCol() =>
        NonNegInt("position");

    private static ColumnDefinition RealCol(string name) =>
        new(name, SqliteColumnType.SqliteReal, IsNotNull: true);

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

    private static TableDefinition RecordSingleText(string tableName, string columnName, bool nonEmpty, params IndexDefinition[] indexes) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("span_id"), nonEmpty ? NonEmptyText(columnName) : TextCol(columnName)],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: indexes);

    private static TableDefinition RecordSingleInt(string tableName, string columnName, bool nonNegative) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("span_id"), nonNegative ? NonNegInt(columnName) : IntCol(columnName)],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: []);

    private static TableDefinition RecordSinglePositiveInt(string tableName, string columnName) =>
        new(
            Name: tableName,
            Columns:
            [
                NonEmptyText("span_id"),
                new ColumnDefinition(columnName, SqliteColumnType.SqliteInteger, IsNotNull: true, CheckConstraint: string.Create(CultureInfo.InvariantCulture, $"{columnName} > 0")),
            ],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: []);

    private static TableDefinition RecordSingleReal(string tableName, string columnName) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("span_id"), RealCol(columnName)],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: []);

    private static TableDefinition RecordSingleBool(string tableName, string columnName) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("span_id"), BoolInt(columnName)],
            PrimaryKeyColumns: ["span_id"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: []);

    private static TableDefinition RecordRepeatedText(string tableName, string columnName, bool nonEmpty) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("span_id"), PositionCol(), nonEmpty ? NonEmptyText(columnName) : TextCol(columnName)],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [Fk("span_id", "record", "span_id")],
            Indexes: [],
            IsRepeatedItem: true);

    private static TableDefinition ContentPartExtension(string tableName, ColumnDefinition valueColumn) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("span_id"), PositionCol(), valueColumn],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [CompositeFk(["span_id", "position"], "content_part", ["span_id", "position"])],
            Indexes: []);

    private static TableDefinition ToolDefinitionExtension(string tableName, ColumnDefinition valueColumn) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("span_id"), PositionCol(), valueColumn],
            PrimaryKeyColumns: ["span_id", "position"],
            ForeignKeys: [CompositeFk(["span_id", "position"], "tool_definition", ["span_id", "position"])],
            Indexes: []);

    private static TableDefinition SessionSingleText(string tableName, string columnName, bool nonEmpty) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("session_id"), nonEmpty ? NonEmptyText(columnName) : TextCol(columnName)],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: []);

    private static TableDefinition SessionSingleNonNegInt(string tableName, string columnName) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("session_id"), NonNegInt(columnName)],
            PrimaryKeyColumns: ["session_id"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: []);

    private static TableDefinition SessionRepeatedText(string tableName, string columnName) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("session_id"), PositionCol(), NonEmptyText(columnName)],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [Fk("session_id", "session", "session_id")],
            Indexes: [],
            IsRepeatedItem: true);

    private static TableDefinition SessionTurnExtension(string tableName, ColumnDefinition valueColumn) =>
        new(
            Name: tableName,
            Columns: [NonEmptyText("session_id"), PositionCol(), valueColumn],
            PrimaryKeyColumns: ["session_id", "position"],
            ForeignKeys: [CompositeFk(["session_id", "position"], "session_turn", ["session_id", "position"])],
            Indexes: []);
}
