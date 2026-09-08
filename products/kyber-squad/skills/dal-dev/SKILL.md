---
name: dal-dev
description: Use when implementing ADO.NET repositories, IRepository of T, ISqlConnectionFactory, FluentMigrator migrations, or persistence DI registration. Do not use for schema design or application/domain logic.
license: MIT
---

# Data Access Layer Developer

Identify your sub-task and read ONLY the relevant reference before proceeding.

| Sub-Task | When to Use | Reference |
|---|---|---|
| Schema contract | Consuming approved table definitions, types, constraints, and dacpac alignment | [Schema Design](./references/schema-design.md) |
| ADO.NET repository | ISqlConnectionFactory, IRepository implementations, parameterized SqlCommand, DI registration | [ADO.NET Repository](./references/adonet-repository.md) |
| Migration scripts | FluentMigrator versioning, idempotent up/down scripts, rollback strategy | [Migration Scripts](./references/migration-scripts.md) |

**Rule:** Read only the reference(s) relevant to your current task. Do not pre-load all references.
