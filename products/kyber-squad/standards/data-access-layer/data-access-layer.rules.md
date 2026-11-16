---
id: standards/data-access-layer/rules
title: "data-access-layer rules"
doc-type: rule
status: draft
technology: data-access-layer
owner: unassigned
last-reviewed: 2026-11-16
---

# data-access-layer rules

## Approved schema

- Never apply a migration to a shared database, and never rewrite a connection string
  against one. A task that needs that is reported as an open item; the task's own tests may
  use whatever database the test standard prescribes.
- Before writing repository or migration code, confirm the approved schema: column names,
  data types, nullability, keys and indexes. A script that would diverge from it is not
  applied; finish the rest and report the divergence with the corrected DDL.
- The shared contract with the schema owner and with application code is one table
  definition: table name, columns, types, nullability and key/index declarations.

## Interfaces

- Deliver `IRepository<T>` implementations that satisfy the interfaces in the Contracts
  project this standard names.
