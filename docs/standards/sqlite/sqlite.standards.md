---
id: standards/sqlite
title: SQLite coding standard
doc-type: coding-standard
status: current
technology: sqlite
owner: dpalfery
last-reviewed: 2026-11-26
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

The store is opened through in-process ADO.NET (`Microsoft.Data.Sqlite.Core`,
`BaseSqliteRepository`). `SQLitePCLRaw.provider.sqlite3` links against the system-provided
dynamic library (`/usr/lib/libsqlite3.dylib` on macOS / system SQLite on Linux and Windows),
keeping the vulnerable bundled binary `SQLitePCLRaw.lib.e_sqlite3` (advisory GHSA-2m69-gcr7-jv3q)
out of the dependency graph while avoiding the multi-process spawning overhead of the CLI.

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
- `store_state` is inserted once (`INSERT OR IGNORE`). A later open leaves that row
  untouched, so opening a store never changes its `detector_version`; only a full
  maintenance projection pass restamps `detector_version`. It does not update
  `schema_version` on a row that is already there. The schema version moves because the old file was deleted.

### What a canon column holds

No canon column holds a JSON document ([ADR 0090](../../adr/0090-kyberdash-relational-store.md)
decision 2). A missing child row means the value was not reported. Writing `0`, an empty
string, or `NULL` to stand in for a value the harness did not send is a finding: absent
and measured zero must stay distinguishable.

Generated foreign keys are `ON DELETE CASCADE`. The open script sets
`PRAGMA foreign_keys = ON`, `journal_mode = WAL`, `synchronous = NORMAL`, and
`journal_size_limit` to `SqliteRelationalStore.WalSizeLimitBytes` (32 MiB).

### Binding values

Every value that did not come from the statement itself is a named parameter on the command
(KW-STD-SQL-004), every statement is a named constant in the store's query catalogue
(KW-STD-SQL-005), and every row is read by column name into a typed record (KW-STD-SQL-006).
Parameters are the standard defence against injection and let SQLite reuse a prepared statement;
the older `TextExpr` (`CAST(X'…' AS TEXT)`) and `BLOB` hex literals date from when statements went
to the `sqlite3` CLI, which has no parameters. Interpolating caller text into SQL is a finding,
including text that looks like a number or an identifier. The existing `TextExpr` call sites,
the `hex(CAST(column AS BLOB))` reads and the analysis cache's CLI calls are debt that
[the remediation plan](../../todo/csharp-standards-remediation.md) removes; new code
does not add to them.

### Store reads are shaped to their consumer

The register of reads, with the page or pass each one answers, is
[the read-model register](../../dash/architecture.md#read-path-invariants-and-the-read-model-register).
The three rules below are what a new read is checked against, in the review and in the test that
ships with it.

**A store read is shaped to its consumer** (KW-STD-SQL-001). It returns the columns, rows and
scope the one calling page or pass consumes. A read whose result the caller then filters,
subsets or half-discards in C# is a defect: the query carries the shape, not the caller. A
whole-corpus scan is maintenance work, so it lives behind a maintenance-named method —
`AllSessionRecordsForMaintenance()` in
[`SqliteRelationalStore.Reader.cs`](../../../dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs)
is the worked example — and no request path calls one:
`ArchitectureBoundaryTests.RequestPathsCannotReachMaintenanceReads` fails on a new caller. Three
kinds of read are sanctioned rather than defects: those maintenance reads; the section-scoped
content reads — the shown requests page's prompt previews, the inspected span's consent-allowed
prompt, and the opened stored table's page — each pinned to the screen that shows it; and the
content-free stored-fields inventory, which reads no content at all.

**Content text is read only where it is consumed** (KW-STD-SQL-002). The content columns —
`content_part_text`, tool-call input text and tool-call result text — are selected by exactly
three paths, each scoped: the content view, by `(session_key, span, position)` through
`TurnContentParts`; the section-scoped reads — `RequestPromptPreviews` for the shown requests
page, `SpanPromptText` for the inspected span, `StoredFieldsRows` for the opened table's page;
and the projection rebuild, for the session being rebuilt, which derives persisted figures from
the text. A fourth path that selects one of them is a defect.

**A read's cost is proportional to its input, not to the corpus** (KW-STD-SQL-003). An
incremental pass reads only what changed: per-dirty-key reads for the work it does, and the
attribution pairs of `SessionAttributions()` to decide a skip, never the store to decide it. A
list endpoint's store-query count is bounded regardless of row count — the sessions page reads
one header page plus one keyed figures read for the page (`SessionHeaderPage` then
`SessionListFigures(keys)`), never a read per row. An expensive shared refresh on
the request path is single-flight or it moves off that path: `ProjectIncrementalForReads()`
defers a session over its record budget and reports it in `deferredSessions`, while
`ProjectIncremental()` is the maintenance entry that never defers.

Why the rules exist: on a 579 MB store whose largest session held 485 MB of captured text, the
sessions list materialized 1,048,607 bytes of content before the reads were shaped and zero
afterwards. `AnalyticsReadPathTests.EveryOtherPathLeavesContentUnread` sweeps 29 request paths and
fails on the first materialized content byte. The assertions that keep a new path in this shape
are `KW-STD-TEST-001` and `KW-STD-TEST-002` in the
[test standard](../test/test.standards.md#read-path-tests-assert-the-shape-of-the-reads).

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
as a named parameter. A path from a caller is not concatenated into a
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
- A new or narrowed read is shaped to its consumer, selects content text only in the three
  sanctioned paths, and its test asserts the reads the path issues
  (KW-STD-SQL-001 to KW-STD-SQL-003, KW-STD-TEST-001).
- A change to the migration rule updates
  [ADR 0090](../../adr/0090-kyberdash-relational-store.md) in the same change, then
  `docs validate . --merge-ready` and `docs drift .`.
