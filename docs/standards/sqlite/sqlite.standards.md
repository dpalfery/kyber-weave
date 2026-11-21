---
id: standards/sqlite
title: SQLite coding standard
doc-type: coding-standard
status: current
technology: sqlite
owner: dpalfery
last-reviewed: 2026-11-20
---

# SQLite coding standard

How SQL is written in this repository. Agents and skills resolve this document as
`<sqlite-coding-standard>` in the repository root [`AGENTS.md`](../../../AGENTS.md).

The database engine is SQLite. There is no SQL Server database and no `sql-server`
standard in this repository. A statement written for another dialect (`NVARCHAR`,
`IDENTITY`, `GO`, `ALTER TABLE ... ADD COLUMN` as a migration) does not belong here.

This records the decisions this repository has made. It is not a summary of SQLite's
own documentation.

## Naming and layout

### Which files are SQLite

Two SQLite databases belong to this repository.

- **KyberDash canon store.** `canon.db`, resolved by `CanonDbPath.Resolve`: an explicit
  path, then the `KYBER_CANON_DB` environment variable, then `~/.kyberdash/canon.db`.
  This is the product database. The migration rules below apply to it.
- **Docs analysis cache.** `cache/docs-analysis.sqlite3`, written by
  `SqliteAnalysisPersistence`. It is a local cache at `PRAGMA user_version` 1. A cache
  newer than that version is refused. It follows its own version check, and the
  delete-and-reingest rule below applies to `canon.db`.

`.codegraph/codegraph.db` belongs to the CodeGraph tool. Leave it alone: do not open it,
query it, migrate it, copy it, or describe its schema.

### Where the canon schema lives

`RelationalSchemaCatalog` is the canon schema. `SchemaDdlGenerator` emits the DDL from
that catalog, in parent-before-child order. A hand-written `CREATE TABLE` or
`ALTER TABLE` for `canon.db` is a finding: the catalog and the generator are the only
writers.

The store is opened through the system `sqlite3` CLI (`BaseSqliteRepository`,
`ExecuteSqliteCore`). `Microsoft.Data.Sqlite` and `SQLitePCLRaw` are not dependencies.
The native library `SQLitePCLRaw.lib.e_sqlite3` carries advisory GHSA-2m69-gcr7-jv3q,
which is why the CLI is the boundary ([ADR 0084](../../adr/0084-kyberdash-csharp-engine.md)).

## Coding standards

### A canon schema change deletes the old database

`canon.db` is not migrated in place. [ADR 0090](../../adr/0090-kyberdash-relational-store.md)
decision 7 is the rule: an older database is deleted unread and history is re-ingested
from harness files.

When a change adds, removes, or retypes a column, table, index, or constraint:

1. Change `RelationalSchemaCatalog`.
2. Bump `SqliteRelationalStore.SchemaVersion` in the same change. The current value is
   25. `DetectorVersion` (currently 5) is a separate stamp for detector logic and is not
   a schema migration.
3. Do not write an `ALTER TABLE`, a copy of the old file, or a backup. The owner rejected
   migrating or copying the previous database.

On open, `SqliteRelationalStore` does the following.

- A `schema_version` lower than `SchemaVersion` deletes `canon.db`, `canon.db-wal`, and
  `canon.db-shm` without reading a data row. Operator settings reset to defaults.
  History that exists only in the old file (OTLP spans with no harness file, and
  unevidenced harnesses such as `zcode`) is lost, and that loss is logged.
- A `schema_version` of 0 or less, or newer than `SchemaVersion`, throws. The file is
  not deleted. A newer file means this build is too old.
- `CREATE TABLE IF NOT EXISTS` and `CREATE INDEX IF NOT EXISTS` reapply the current
  catalog onto a database that is already this version. They do not change a table that
  already exists. A catalog change that does not bump `SchemaVersion` leaves every
  existing `canon.db` on the old shape.
- `store_state` is inserted once (`INSERT OR IGNORE`). A later open updates
  `detector_version` when it differs. It does not update `schema_version` on a row that
  is already there. The schema version moves because the old file was deleted.

### What a canon column holds

No canon column holds a JSON document ([ADR 0090](../../adr/0090-kyberdash-relational-store.md)
decision 2). A missing child row means the value was not reported. Writing `0`, an empty
string, or `NULL` to stand in for a value the harness did not send is a finding: absent
and measured zero must stay distinguishable.

Generated foreign keys are `ON DELETE CASCADE`. The open script sets
`PRAGMA foreign_keys = ON`, `journal_mode = WAL`, `synchronous = NORMAL`, and
`journal_size_limit` to `SqliteRelationalStore.WalSizeLimitBytes` (32 MiB).

### Binding values

Text that did not come from the statement itself is bound with `TextExpr` (a
`CAST(X'…' AS TEXT)` hex literal) or, in the analysis cache, a SQLite `BLOB` literal.
Interpolating caller text into SQL is a finding, including text that looks like a
number or an identifier. Integers the store itself formatted with `CultureInfo.InvariantCulture`
may be inlined. Reads of text columns come back as `hex(CAST(column AS BLOB))`.

## Testing

A unit test does not open `canon.db` and does not start `sqlite3` for the canon store.
Schema and version behaviour run through `RelationalSchemaCatalog` and the in-memory
file-system seam. `RelationalSchemaCatalogTests` checks the catalog and the generated
DDL. `SchemaVersionTests` checks version stamping and the refusal of a newer, invalid,
or older store. A test that needs a real `sqlite3` process belongs in the integration
project, as the [test standard](../test/test.standards.md) describes.

## Commands

There is no SQL formatter gate for the canon store: the SQL is generated C#, and the C#
format gate covers the files that emit it. Finish a canon schema change with the classes
that name the catalog and the version:

```bash
dotnet test dash/server.tests/KyberDash.Domain.Tests/KyberDash.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~KyberDash.Domain.Tests.Schema.RelationalSchemaCatalogTests"
dotnet test dash/server.tests/KyberDash.Data.Tests/KyberDash.Data.Tests.csproj -c Release --filter "FullyQualifiedName~KyberDash.Data.Tests.Repositories.SchemaVersionTests"
```

Read the reported test count. A filter that matches nothing exits 0 and verified nothing.
`dotnet format` on the changed C# files is the format check, as the
[C# standard](../csharp/csharp.standards.md#done-checklist) states. Do not run the full
suite.

## Security and data handling

`KYBER_CANON_DB` and an explicit path are the documented overrides of the canon location.
They are used as the database file path. Do not add a second copy of the database beside
them: the rebuild deletes the file it opened, and a leftover copy would be a backup the
owner rejected.

A value from a harness, a request, or a file is treated as data. It enters SQL only
through `TextExpr` or a blob literal. A path from a caller is not concatenated into a
`sqlite3` argument list.

The analysis cache is local to the machine. It is not a source of canon history, and it
is not copied into `canon.db`. `.codegraph/codegraph.db` stays with the CodeGraph tool.

## Done checklist

- The catalog, `SchemaVersion`, and this standard agree. A bump of the version updates
  the number named above in the same change.
- No `ALTER TABLE` and no backup of `canon.db` were added.
- `RelationalSchemaCatalogTests` and `SchemaVersionTests` passed, and the run reported
  a non-zero test count.
- The changed C# files pass `dotnet format`.
- A change to the migration rule updates
  [ADR 0090](../../adr/0090-kyberdash-relational-store.md) in the same change, then
  `docs validate . --merge-ready` and `docs drift .`.
