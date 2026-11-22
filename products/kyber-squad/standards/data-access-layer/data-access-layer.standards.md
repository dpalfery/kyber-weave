---
id: standards/data-access-layer
title: "Data access layer coding standard"
doc-type: coding-standard
status: draft
technology: data-access-layer
builds-on: csharp
owner: unassigned
last-reviewed: 2026-11-22
---

# Data access layer coding standard

How persistence code is written in this repository. Agents and skills resolve this
document as `<data-access-layer-coding-standard>`. Language-level C# is in the path
declared as **<csharp-coding-standard>**; SQL shape is in **<sql-server-coding-standard>**.
Those three must not disagree. Where they would, this document yields to them.


When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal, but a worker never stops for it: it uses the draft as the
best available guidance and names the draft standard it relied on in one line of its report.

> Template. Set `owner` to a row in `catalog.md`, replace `<Solution>` with the host's
> root namespace, review the decisions below, and promote `status` to `current`.

## Naming and layout

### Stack

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

### Placement

| Kind | Where |
|---|---|
| Repository interface | `<Solution>.Contracts` |
| Repository implementation | `<Solution>.Persistence` |
| Persistence row | `<Solution>.Persistence` (private) |
| FluentMigrator scripts | `<Solution>.Persistence` (or the host's migrations project) |

Application and API code consumes `IRepository<T>`. It does not open connections or
write SQL.

## Coding standards

### Hard rules

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

### Commands and parameters

- Add each parameter with an explicit type and, for strings, a length:
  `command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = name;`.
- Map an absent value to `DBNull.Value` explicitly (`optional ?? (object)DBNull.Value`).
- Keep the SQL text in a `const` string beside the method that runs it. A `MERGE` or other
  multi-statement batch types every parameter the same way.
- Pass the `CancellationToken` to every `...Async` call (open, execute, read, commit,
  rollback).
- Open the connection as late as possible and release it as early as possible: one
  `await using` connection per repository method, not one held across calls.
- A lookup that finds nothing returns `null` (or an empty collection), not an exception.

### Transactions

- A command created without the scope's `SqlTransaction` fails or runs outside the scope.
- Commit on success and roll back in a `catch`, then rethrow so the stack is kept.

### Failure handling

- Log the failure with structured properties (operation, entity, id) and rethrow, wrapping
  the original exception as the inner exception when the caller needs a domain-level type.
  Do not swallow database exceptions.

## Testing

_Nothing specific yet._

## Commands

### Migrations

- One migration per schema change: each schema change ships as its own versioned
  `[Migration(yyyyMMddHHmmss)]` class.
- Never edit an applied migration; add a new one instead. A migration already applied in any
  environment is immutable — a later edit to it there would not run at all.
- For a non-nullable column added to an existing table, add a default first, then remove the
  default in a separate migration, so rows written before the change still satisfy `NOT NULL`.
- Use `WithDefaultValue(SystemMethods.CurrentUTCDateTime)` for audit timestamp columns, never a
  hardcoded date.
- Make `Up()` safe to re-run with FluentMigrator's `IfTableDoesNotExist` /
  `IfIndexDoesNotExist` / `IfColumnDoesNotExist` guards, per **<sql-server-coding-standard>**.
- Schema-qualify every table reference in migration SQL and in FluentMigrator calls
  (`WithSchema` / `InSchema`).
- After applying a migration, verify the result against the schema project's `.dacpac`.
- Down() is a host decision: implementing it is optional, and a production correction is
  always a forward migration, per **<sql-server-coding-standard>**.

## Security and data handling

_Nothing specific yet._

## Done checklist

### Completion

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
