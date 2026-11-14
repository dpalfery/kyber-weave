---
id: standards/data-access-layer
title: "Data access layer coding standard"
doc-type: coding-standard
status: draft
technology: data-access-layer
owner: unassigned
last-reviewed: 2026-08-16
---

# Data access layer coding standard

How persistence code is written in this repository. Agents and skills resolve this
document as `<data-access-layer-coding-standard>`. Language-level C# is in the path
declared as **<csharp-coding-standard>**; SQL shape is in **<sql-coding-standard>**.
Those three must not disagree. Where they would, this document yields to them.

## Authority & status

When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal, but a worker never stops for it: it uses the draft as the
best available guidance and names the draft standard it relied on in one line of its report.

> Template. Set `owner` to a row in `catalog.md`, replace `<Solution>` with the host's
> root namespace, review the decisions below, and promote `status` to `current`.

## Stack

- **Data access:** parameterized ADO.NET (`Microsoft.Data.SqlClient`). Dapper and Entity
  Framework are out of scope — no `QueryAsync`, no `DbContext`, no `DbSet`, no
  `SaveChangesAsync`.
- **Connections:** `ISqlConnectionFactory` creates and opens connections. Never
  `new SqlConnection(...)` at a call site.
- **Migrations:** FluentMigrator, versioned `[Migration(yyyyMMddHHmmss)]`. Schema design
  is not authored here — it arrives as an approved table-definition contract.
- **Abstraction:** `IRepository<T>` in `<Solution>.Contracts`; implementations private to
  `<Solution>.Persistence`. Persistence rows stay in Persistence and are mapped at the
  adapter boundary.

## Hard rules

- Parameterized `SqlCommand` / `SqlParameter` only. String concatenation into SQL is an
  injection, including in a migration.
- List columns explicitly. `SELECT *` is a contract with tomorrow's schema.
- Dispose connections, commands, and readers with `using` / `await using`.
- Multi-statement work that must be atomic runs in one transaction, with an explicit
  rollback path. Do not nest transactions.
- Connection strings and credentials come from configuration / Key Vault / managed
  identity, as declared by the **Configuration Policy** registry property — never a
  literal, never a committed file, never a log line.
- Register the factory as Singleton and repositories as Scoped in
  `ServiceCollectionExtensions.AddSqlPersistenceServices()` (or the host's equivalent).
- Structured logging on failures: operation, entity, id. Do not swallow database
  exceptions.

## Placement

| Kind | Where |
|---|---|
| Repository interface | `<Solution>.Contracts` |
| Repository implementation | `<Solution>.Persistence` |
| Persistence row | `<Solution>.Persistence` (private) |
| FluentMigrator scripts | `<Solution>.Persistence` (or the host's migrations project) |

Application and API code consumes `IRepository<T>`. It does not open connections or
write SQL.

## Migrations

- One migration per schema change: each schema change ships as its own versioned
  `[Migration(yyyyMMddHHmmss)]` class.
- Never edit an applied migration; add a new one instead. A migration already applied in any
  environment is immutable — a later edit to it there would not run at all.
- For a non-nullable column added to an existing table, add a default first, then remove the
  default in a separate migration, so rows written before the change still satisfy `NOT NULL`.
- Down() is a host decision: implementing it is optional, and a production correction is
  always a forward migration, per **<sql-coding-standard>**.

## Completion

```bash
dotnet build -c Release                                 # the build
dotnet test --filter "FullyQualifiedName~<TestClass>"   # the tests covering your change
dotnet format --verify-no-changes                       # the formatter check
fluentmigrator migrate
fluentmigrator rollback
```

`dotnet build` is the build gate: it compiles the migration assembly, so a mistake fails at the
compiler rather than in review. The `dotnet test`
line is filtered to the classes covering your change; the unfiltered run is the gate the review
council and CI run, and a dev agent never runs it. `dotnet format --verify-no-changes` is the
format check and prints nothing when the tree is formatted.

`fluentmigrator rollback` only runs where the host has implemented `Down()` for the migration
being rolled back. Where `Down()` is not implemented, correct forward with a new migration
instead, per § Migrations.
