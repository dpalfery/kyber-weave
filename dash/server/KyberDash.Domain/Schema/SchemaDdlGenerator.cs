using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KyberDash.Domain.Schema;

/// <summary>
/// Generates deterministic SQLite DDL from <see cref="RelationalSchemaCatalog"/> definitions.
/// </summary>
public static class SchemaDdlGenerator
{
    /// <summary>
    /// Generates the complete relational schema DDL for <see cref="RelationalSchemaCatalog.Tables"/>
    /// in deterministic topological parent-before-child order.
    /// </summary>
    /// <returns>LF-normalized SQLite DDL terminated by a trailing newline.</returns>
    public static string GenerateSchemaDdl() =>
        GenerateSchemaDdl(RelationalSchemaCatalog.Tables);

    /// <summary>
    /// Validates and generates deterministic SQLite DDL for <paramref name="tables"/> in
    /// topological parent-before-child order.
    /// </summary>
    /// <param name="tables">Table definitions to validate and emit.</param>
    /// <returns>LF-normalized SQLite DDL terminated by a trailing newline.</returns>
    /// <exception cref="ArgumentException">Thrown when any table fails relational schema validation.</exception>
    public static string GenerateSchemaDdl(IEnumerable<TableDefinition> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        IReadOnlyList<TableDefinition> ordered = RelationalSchemaCatalog.ValidateAndOrderCatalog(tables);
        StringBuilder builder = new();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
                builder.Append('\n');

            AppendTableDdl(builder, ordered[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Validates and generates deterministic SQLite DDL for a single <paramref name="table"/>,
    /// including its <c>CREATE TABLE</c> statement and secondary indexes.
    /// </summary>
    /// <param name="table">Table definition to validate and emit.</param>
    /// <returns>LF-normalized SQLite DDL terminated by a trailing newline.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="table"/> violates any relational schema invariant.</exception>
    public static string GenerateTableDdl(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        RelationalSchemaCatalog.ValidateTable(table);

        StringBuilder builder = new();
        AppendTableDdl(builder, table);
        return builder.ToString();
    }

    private static void AppendTableDdl(StringBuilder builder, TableDefinition table)
    {
        List<string> clauses = new(table.Columns.Count + 1 + table.ForeignKeys.Count + (table.CheckConstraints?.Count ?? 0));

        foreach (ColumnDefinition column in table.Columns)
        {
            string sqliteType = FormatSqliteType(column.Type);
            string line = string.IsNullOrWhiteSpace(column.CheckConstraint)
                ? $"  {column.Name} {sqliteType} NOT NULL"
                : $"  {column.Name} {sqliteType} NOT NULL CHECK ({column.CheckConstraint})";
            clauses.Add(line);
        }

        clauses.Add($"  PRIMARY KEY ({string.Join(", ", table.PrimaryKeyColumns)})");

        foreach (ForeignKeyDefinition fk in table.ForeignKeys)
        {
            clauses.Add(
                $"  FOREIGN KEY ({string.Join(", ", fk.Columns)}) REFERENCES {fk.ReferencedTable} ({string.Join(", ", fk.ReferencedColumns)}) ON DELETE CASCADE");
        }

        if (table.CheckConstraints is not null)
        {
            foreach (string check in table.CheckConstraints)
                clauses.Add($"  CHECK ({check})");
        }

        builder.Append("CREATE TABLE IF NOT EXISTS ").Append(table.Name).Append(" (\n");
        for (int i = 0; i < clauses.Count; i++)
        {
            builder.Append(clauses[i]);
            builder.Append(i < clauses.Count - 1 ? ",\n" : "\n");
        }

        builder.Append(");\n");

        foreach (IndexDefinition index in table.Indexes)
        {
            string keyword = index.IsUnique ? "CREATE UNIQUE INDEX IF NOT EXISTS" : "CREATE INDEX IF NOT EXISTS";
            IEnumerable<string> columns = index.Columns.Select(static col =>
                col.Direction == IndexSortDirection.Descending ? $"{col.Name} DESC" : col.Name);
            builder.Append(keyword)
                .Append(' ')
                .Append(index.Name)
                .Append(" ON ")
                .Append(table.Name)
                .Append(" (")
                .Append(string.Join(", ", columns))
                .Append(");\n");
        }
    }

    private static string FormatSqliteType(SqliteColumnType type) =>
        type switch
        {
            SqliteColumnType.SqliteInteger => "INTEGER",
            SqliteColumnType.SqliteReal => "REAL",
            SqliteColumnType.SqliteText => "TEXT",
            _ => throw new ArgumentException($"Unsupported SQLite column type '{type}'.", nameof(type)),
        };
}
