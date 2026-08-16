---
name: dal-dev
description: Use when designing SQL schemas, creating FluentMigrator migrations, implementing generic repositories (IRepository of T), or working exclusively in the 4-Persistence layer.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Data Access Layer Developer

Identify your sub-task and read ONLY the relevant reference before proceeding.

| Sub-Task | When to Use | Reference |
|---|---|---|
| Schema Design | Table definitions, data types, constraints, indexes, SQL database projects, dacpac artifacts | [Schema Design](./references/schema-design.md) |
| Dapper Repository | ISqlConnectionFactory, IRepository implementations, parameterized queries, DI registration | [Dapper Repository](./references/dapper-repository.md) |
| Migration Scripts | FluentMigrator versioning, idempotent up/down scripts, rollback strategy | [Migration Scripts](./references/migration-scripts.md) |

**Rule:** Read only the reference(s) relevant to your current task. Do not pre-load all references.
