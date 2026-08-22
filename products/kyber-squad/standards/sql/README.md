---
id: standards/sql
title: sql coding standard
doc-type: coding-standard
status: draft
technology: sql
owner: unassigned
last-reviewed: 2026-08-16
---

# sql coding standard

How SQL is written in this repository. Agents and skills resolve this document as
`<sql-coding-standard>`.

## Authority & status

When this standard is in `status: current`, what it says here outranks whatever defaults a
portable agent shipped with. While in `status: draft`, it serves as a non-authoritative
template/proposal and does NOT override portable agent defaults until reviewed and promoted
to `current`.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`.

## Parameters, always

Every value that came from outside the query is a parameter. String concatenation into SQL is
an injection, including in a migration, including in a script that "only an admin runs",
including when the value is an integer today.

Dynamic identifiers — a table or column name chosen at runtime — cannot be parameterized, so
they are validated against an allow-list, never interpolated from input.

## Queries

- **Explicit `JOIN ... ON`.** Comma joins hide the condition, and a missing one is a cross
  join nobody notices until production.
- **Name the columns.** `SELECT *` couples the caller to column order and leaks whatever gets
  added later.
- **Set-based, not row-by-row.** A cursor or a loop that issues one statement per row is the
  first thing to look at when something is slow.
- **`DISTINCT` is a smell.** It usually means a join is duplicating rows; fix the join.
- No scalar user-defined functions in `WHERE` or `SELECT` over large sets — they defeat the
  optimizer.

## Correctness at the edges

`NULL` is not a value and does not compare like one. State what a `WHERE` clause should do
with missing data, and use `IS NULL` / `IS NOT NULL` / `COALESCE` deliberately rather than
discovering the behaviour in a report.

Statements forming one unit of work run in one transaction, with an explicit rollback path.

## Indexes

A query added with a new access pattern comes with the index that serves it, or with a stated
reason it does not need one. Check the plan rather than guessing; an index that is never used
still costs every write.

## Naming and layout

- One convention for schemas, tables and columns — `snake_case` unless the platform's own
  conventions say otherwise — chosen once and applied everywhere.
- Keywords uppercase, one clause per line, joins and conditions indented consistently. A
  formatter settles this; the point is that diffs stay readable.
- Singular or plural table names is a coin flip. Pick one here and stop re-deciding it.

## Migrations

Migrations are forward-only and idempotent where the platform allows it. A migration that
cannot be re-run safely says so at the top, along with what to do if it half-applied.

Destructive changes — dropping a column, narrowing a type — ship separately from the code that
stops using them, so a rollback does not lose data.

## Least privilege

Application accounts get the permissions the application uses, and no more. Nothing routine
runs as `sa`, `root`, or the schema owner.
