---
id: standards/sql-server
title: "SQL Server coding standard"
doc-type: coding-standard
status: draft
technology: sql-server
owner: unassigned
last-reviewed: 2026-11-16
---

# SQL Server coding standard

How SQL Server and Azure SQL schema is designed and delivered in this repository. Agents
resolve this document as `<sql-server-coding-standard>`.

## Naming and layout

- User procedures do not take the `sp_` prefix; it is reserved for system procedures
  (rule SR0016).
- `text`, `ntext` and `image` are deprecated; use `varchar(max)`, `nvarchar(max)` and
  `varbinary(max)`.
- Table and column names are lower snake case; keys and constraints carry the table name.
- Schema source lives where the delivery path below puts it, not as ad-hoc scripts.

## Coding standards

- `SCOPE_IDENTITY()`, not `@@IDENTITY`, returns the identity this scope inserted
  (rule SR0008).
- A `PRIMARY KEY` creates a supporting unique index, clustered by default; choose the
  clustered key deliberately.
- Wrapping a column in a function in `WHERE` or `JOIN` makes the predicate non-sargable.
  Inspect the plan and the existing indexes before tuning.
- Normalise to at least third normal form unless the standard records a measured reason not
  to.

## Testing

- Test each migration against a disposable database built from the schema source.
- Assert the constraint, default and nullability the change adds, not only that the script
  runs.

## Commands

- Schema changes reach an environment through the standard's delivery path: the SDK-style
  SQL database project that produces a dacpac, or the approved DDL contract a migration
  implements.
- Preview and approve before production. Never run un-reviewed DDL by hand.

## Security and data handling

- Never build T-SQL by concatenating unvalidated input. Parameterise through
  `sp_executesql` with typed parameters; validate input by type, length, format and range.
- Review every `EXEC`, `EXECUTE` and `sp_executesql` for injection risk; avoid dynamic SQL
  when a static, parameterised statement or stored procedure will do.
- Apply least privilege: grant the minimum permission, map Entra or Windows groups to roles,
  and do not hand out `sysadmin` by default.
- Prefer Microsoft Entra ID or Windows authentication over SQL authentication; keep SQL
  authentication disabled unless there is a justified need.

## Done checklist

- The target engine is named: SQL Server version, Azure SQL Database, Managed Instance or
  Fabric.
- The schema source, not a hand-run script, holds the change.
- The change was tested against a disposable database.
- The build of the database project, its test command and its format check were run on the
  change.
