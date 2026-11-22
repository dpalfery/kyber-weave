---
id: standards/sql-server
title: "SQL Server coding standard"
doc-type: coding-standard
status: draft
technology: sql-server
owner: unassigned
last-reviewed: 2026-11-22
---

# SQL Server coding standard

How SQL Server and Azure SQL schema is designed and delivered in this repository. Agents
resolve this document as `<sql-server-coding-standard>`. The `sql` template this folder replaces is
retired: its rules live here, in the six headings a technology folder requires.

## Naming and layout

### Schema and types

- **Third normal form by default.** Denormalize only as a deliberate, documented performance
  decision.
- **The narrowest correct type.** `int` / `bigint` for keys, `decimal` for exact and monetary
  values, `date` / `time` / `datetime2` rather than `datetime`, `bit` for booleans, `nvarchar`
  for Unicode text. Avoid the deprecated text, ntext, and image types; use `varchar(max)`,
  `nvarchar(max)`, or `varbinary(max)` instead.
- **Every table has a clustered index.** The clustered key is narrow, unique, ever-increasing,
  immutable, non-nullable, and fixed-width — an `IDENTITY` or `SEQUENCE` column. Not a
  `uniqueidentifier` unless its values are generated sequentially. When the primary key does
  not fit that shape, declare it nonclustered and cluster elsewhere.
- Integrity is enforced with constraints — `PRIMARY KEY`, `FOREIGN KEY`, `UNIQUE`, `CHECK`,
  `NOT NULL`, `DEFAULT` — not by application logic alone.

### Naming and layout

- One convention for schemas, tables and columns — `lower_snake_case` — chosen once and applied
  everywhere. Keys and constraints carry the table name.
- User procedures do not take the `sp_` prefix; it is reserved for system procedures (rule
  SR0016).
- Keywords uppercase, one clause per line, joins and conditions indented consistently. A
  formatter settles this; the point is that diffs stay readable.
- Singular or plural table names is a coin flip. Pick one here and stop re-deciding it.
- Schema source lives where § Commands puts it, not as ad-hoc scripts.

## Coding standards

### Queries

- **Explicit `JOIN ... ON`.** Comma joins hide the condition, and a missing one is a cross
  join nobody notices until production.
- **Name the columns.** `SELECT *` couples the caller to column order and leaks whatever gets
  added later.
- **Set-based, not row-by-row.** A cursor or a loop that issues one statement per row is the
  first thing to look at when something is slow.
- **`DISTINCT` is a smell.** It usually means a join is duplicating rows; fix the join.
- No scalar user-defined functions in `WHERE` or `SELECT` over large sets — they defeat the
  optimizer.
- Write sargable predicates: do not wrap functions around columns in WHERE or JOIN clauses —
  transform the input side instead, or the optimizer cannot use an index.

### T-SQL authoring

- **Schema-qualify every object reference** (`dbo.customer`, `sales.usp_get_order`).
- User procedures take a `usp_` prefix or none; `sp_` stays reserved for system procedures.
- **`SET NOCOUNT ON;`** is the first statement in a stored-procedure body.
- Modules are created with `CREATE OR ALTER` and dropped with `DROP ... IF EXISTS`, so a
  script can run twice.
- Transactions are explicit (`BEGIN TRANSACTION` / `COMMIT`) and short.
- Narrow results as early as possible: only the rows and columns the caller needs.
- Use SCOPE_IDENTITY() rather than @@IDENTITY, to read back the identity a statement just
  inserted — @@IDENTITY can return a trigger's insert instead of the caller's (rule SR0008).

### Correctness at the edges

`NULL` is not a value and does not compare like one. State what a `WHERE` clause should do
with missing data, and use `IS NULL` / `IS NOT NULL` / `COALESCE` deliberately rather than
discovering the behaviour in a report.

Statements forming one unit of work run in one transaction, with an explicit rollback path.

### Indexes

A query added with a new access pattern comes with the index that serves it, or with a stated
reason it does not need one. Check the plan rather than guessing; an index that is never used
still costs every write.

- Key columns in order of use: equality and join columns first, then the rest from most to
  least distinct.
- Cover with `INCLUDE` rather than widening the key, and never include `(n)varchar(max)` or
  `xml`.
- Check for an existing or overlapping index first, and extend it rather than adding a
  near-duplicate. A missing-index suggestion is a lead, not a decision.
- Build or rebuild large indexes `ONLINE` where the edition supports it, and consider row or
  page compression.

## Testing

- Test each migration against a disposable database built from the schema source.
- Assert the constraint, default and nullability the change adds, not only that the script
  runs.

## Commands

### Delivery

The schema is code. Its source of truth is an SDK-style SQL database project
(`Microsoft.Build.Sql`), not whatever a database currently holds.

- `dotnet build` produces the `.dacpac`, with SQL code analysis on, so the rules above are
  checked by the build.
- Deploy with SqlPackage `Publish` (or `azure/sql-action`, which wraps it). Build once, and
  deploy the same artifact to every environment.
- Before production, generate a preview with SqlPackage `Script` or `DeployReport` and have a
  human approve it.
- Pipelines use a standalone SqlPackage installed as a `dotnet` tool, and connect with a
  managed or Entra identity rather than a password.

A repository whose schema changes ship as migration scripts instead replaces this section and
keeps the one below.

### Migrations

Migrations are forward-only and idempotent where the platform allows it. A migration that
cannot be re-run safely says so at the top, along with what to do if it half-applied.

Destructive changes — dropping a column, narrowing a type — ship separately from the code that
stops using them, so a rollback does not lose data.

### Completion

The schema is code, so it is built, tested and formatted like code:

```bash
dotnet build -c Release                                 # the build: the .dacpac, with SQL code analysis on
dotnet test --filter "FullyQualifiedName~<TestClass>"   # the tests covering your change
sqlfluff lint <path> --dialect tsql                     # the format check, where the repository uses it
```

`dotnet build` is the build gate: SQL code analysis runs inside it, so a naming or type rule the
project declares fails here rather than in review. The formatter is the one § Naming and layout
names for this repository — `sqlfluff lint` (or `sqlfluff fix` for the pass that rewrites) where it
adopts that tool, another formatter otherwise — and it is the format check: run it and commit what
it produces. The `dotnet test` line is the integration tests covering the change, run against a
real SQL Server instance; the unfiltered suite belongs to the review council and CI. A change to a
migration or a deploy script is also exercised against a throwaway database, because the build
proves the schema compiles and not that the change applies.

## Security and data handling

### Parameters, always

Every value that came from outside the query is a parameter. String concatenation into SQL is
an injection — including in a migration, including in a script that "only an admin runs",
including when the value is an integer today. Parameterise through `sp_executesql` with typed
parameters; validate input by type, length, format and range.

Dynamic identifiers — a table or column name chosen at runtime — cannot be parameterized, so
they are validated against an allow-list, never interpolated from input. Review every `EXEC`,
`EXECUTE` and `sp_executesql` for injection risk; avoid dynamic SQL when a static, parameterised
statement or stored procedure will do.

### Least privilege

Application accounts get the permissions the application uses, and no more. Nothing routine
runs as `sa`, `root`, or the schema owner. Map Entra or Windows groups to roles, and do not
hand out `sysadmin` by default. Never enable `xp_cmdshell`: it runs operating-system commands
from SQL.

Prefer Microsoft Entra ID or Kerberos authentication over SQL authentication for application and
pipeline connections; keep SQL authentication disabled unless there is a justified need.

Sensitive columns are encrypted, hashed, or masked, and a query selects only the columns it
needs rather than whatever `*` expands to.

## Done checklist

- The target engine is named: SQL Server version, Azure SQL Database, Managed Instance or
  Fabric.
- The schema source, not a hand-run script, holds the change.
- The change was tested against a disposable database.
- The build of the database project, its test command and its format check were run on the
  change.
