---
name: dal-dev/schema-design
description: SQL Server schema design rules — data types, constraints, indexing, SQL database projects, and dacpac deployment.
---

# SQL Server Schema Design

## Prime Directive

Before asserting a best practice, version-specific behavior, or syntax, verify against Microsoft Learn using `microsoft_docs_search` / `microsoft_docs_fetch`. Treat Learn as the source of truth. Confirm the target engine (SQL Server 2016/2019/2022/2025, Azure SQL Database, or Azure SQL Managed Instance) before giving version-sensitive guidance.

---

## Hard Rules: Security

- **Never** build T-SQL by concatenating unvalidated input. Use parameterized commands and `sp_executesql` with typed parameters.
- Privilege boundaries, `xp_cmdshell` avoidance, and authentication method follow
  **<sql-server-coding-standard>** § Least privilege.
- Never hardcode credentials, connection strings, or secrets — use managed identities and encrypted configuration.

---

## T-SQL Authoring Rules

- Object naming, module idempotency, and identity-retrieval conventions follow
  **<sql-server-coding-standard>** § T-SQL authoring.
- Keep transactions **explicit and short** to minimize lock duration.
- Column selection and sargable-predicate rules follow **<sql-server-coding-standard>** § Queries.

---

## Schema & Data Type Decisions

Normalization, data-type width, clustered-key shape, and constraint-based integrity all
follow **<sql-server-coding-standard>** § Schema and types.

---

## Indexing Rules

- Index key ordering, `INCLUDE` usage, overlap checks, and large-table rebuilds follow
  **<sql-server-coding-standard>** § Indexes.
- Avoid over-indexing — every index has write and storage cost.

---

## Source Control & Deployment

- Schema-as-code source of truth, build/dacpac analysis, deployment, and preview-before-
  production conventions follow **<sql-server-coding-standard>** § Delivery.
- Never run un-reviewed DDL by hand against production.

---

## Data Layer Boundary

- `architect` owns schema and data-model planning end-to-end: table definitions, data types, constraints, clustered key strategy, indexes, and the SQL database project producing the dacpac.
- The developer tier the task routes to writes parameterized ADO.NET repositories, FluentMigrator migration scripts, the connection factory, and the DDL the plan approves.
- The application tier consumes `IRepository<T>` in application services. It does not write SQL or migrations.
- When the data access needs a schema change, describe the access need in the task prompt → the plan records the design → the approved DDL is the explicit contract artifact.
- If a FluentMigrator script diverges from the approved schema, flag the conflict in the plan and do not apply the script.

Shared contract artifact for parallel work: a table-definition block listing column names, data types, nullability, and key/index declarations.

---

## Reference Index (Microsoft Learn)

- SQL Server security best practices — https://learn.microsoft.com/sql/relational-databases/security/sql-server-security-best-practices
- SQL injection — https://learn.microsoft.com/sql/relational-databases/security/sql-injection
- T-SQL design issues (SR0001/SR0008) — https://learn.microsoft.com/sql/tools/sql-database-projects/concepts/sql-code-analysis/t-sql-design-issues
- T-SQL naming issues (SR0016) — https://learn.microsoft.com/sql/tools/sql-database-projects/concepts/sql-code-analysis/t-sql-naming-issues
- Data types — https://learn.microsoft.com/sql/t-sql/data-types/data-types-transact-sql
- Index architecture and design guide — https://learn.microsoft.com/sql/relational-databases/sql-server-index-design-guide
- SQL database projects — https://learn.microsoft.com/sql/tools/sql-database-projects/sql-database-projects
- SqlPackage — https://learn.microsoft.com/sql/tools/sqlpackage/sqlpackage
