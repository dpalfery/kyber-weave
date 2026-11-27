---
id: standards/sqlite/rules
title: "sqlite rules"
doc-type: rule
status: current
technology: sqlite
owner: dpalfery
last-reviewed: 2026-11-26
---

# sqlite rules

## The engine

- SQL in this repository is SQLite. KyberDash's own store reaches it through
  `Microsoft.Data.Sqlite.Core` over the system SQLite (`SQLitePCLRaw.provider.sqlite3`), and the
  harness SQLite readers use the same client in read-only mode. Do not spawn the `sqlite3` CLI
  per query, and do not add the bundled `SQLitePCLRaw.lib.e_sqlite3` native package.
- `canon.db` is the KyberDash product database. The docs analysis cache is a local
  cache and is not a source of canon history.
- `.codegraph/codegraph.db` belongs to the CodeGraph tool. Leave it alone.

## Schema changes

- A change to the canon schema edits `RelationalSchemaCatalog` and bumps
  `SqliteRelationalStore.SchemaVersion` in the same change.
- An older `canon.db` is deleted unread, including its `-wal` and `-shm` files, and
  history is re-ingested. There is no `ALTER TABLE` migration and no backup copy.
- A database newer than `SchemaVersion` is refused and left in place.
- `CREATE TABLE IF NOT EXISTS` does not change a table that already exists. It is not
  a migration.

## Values

- A canon column does not hold a JSON document. A missing row means the value was not
  reported, and it is not stored as zero.
- **KW-STD-SQL-004 to -007 apply to new code now.** The remediation's first slice gives
  `BaseSqliteRepository` a parameterized command path and the engine its SQLite package.
  Until that slice lands, the existing `TextExpr` call sites stay as debt
  (`docs/todo/csharp-standards-remediation.md`). New code does not add to them, and it adds no
  new CLI database.
- **KW-STD-SQL-004 Parameterized.** Every value enters SQL as a named `$parameter` on the
  command. `TextExpr` and blob literals are retired; existing ones are remediation debt
  (`docs/todo/csharp-standards-remediation.md`).
- **KW-STD-SQL-005 Named statements.** Every query and DML statement is a named `const string`
  in one query catalogue per store, the stored-procedure equivalent SQLite lacks. No query or DML
  is assembled inside a method beyond a generated `IN` list of parameters. DDL is the exception:
  `SchemaDdlGenerator` emits `CREATE TABLE` and `CREATE INDEX` from `RelationalSchemaCatalog`,
  and that generator is the only sanctioned DDL writer.
- **KW-STD-SQL-006 Named fields.** Read a row by column name (`GetOrdinal`) into a typed record,
  never by position and never by re-parsing text output.
- **KW-STD-SQL-007 In process.** Every database the product owns, the engine's caches included, is
  opened through `Microsoft.Data.Sqlite.Core` over the system SQLite. The `sqlite3` CLI is kept
  only for reading CodeGraph's index.
