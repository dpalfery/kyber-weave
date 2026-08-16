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
- **Never** use `xp_cmdshell`. Use SQLCLR or an external process instead.
- Apply **least privilege**: grant minimum permissions; map Entra groups → SQL Server roles → minimal object permissions.
- Prefer **Microsoft Entra ID / Kerberos authentication** over SQL authentication.
- Never hardcode credentials, connection strings, or secrets — use managed identities and encrypted configuration.

---

## T-SQL Authoring Rules

- **Schema-qualify every object reference** (`dbo.Customer`, `Sales.uspGetOrder`).
- Put **`SET NOCOUNT ON;`** as the first statement in stored-procedure bodies.
- **Never `SELECT *`** in stored procedures, views, or table-valued functions. List columns explicitly.
- **Do not prefix user stored procedures with `sp_`** — use `usp_` or no prefix.
- Use **`SCOPE_IDENTITY()`**, not `@@IDENTITY`.
- Make scripts **idempotent**: use `CREATE OR ALTER` for modules and `DROP ... IF EXISTS` patterns.
- Keep transactions **explicit and short** to minimize lock duration.
- Write **sargable predicates**: don't wrap functions around columns in `WHERE` / `JOIN` — defeats indexes.

---

## Schema & Data Type Decisions

- Normalize to **3NF** by default. Denormalize only as a documented performance decision.
- Choose the **narrowest correct data type**: `int`/`bigint` for keys, `decimal`/`numeric` for money, `datetime2` over `datetime`, `bit` for booleans.
- Use `nvarchar` for Unicode text. Avoid deprecated `text`, `ntext`, `image`.
- **Every table must have a clustered index — avoid heaps.**
- Ideal clustered key: **narrow, unique, ever-increasing, immutable, non-nullable, fixed-width** — typically `int`/`bigint` IDENTITY or SEQUENCE-backed.
- Avoid `uniqueidentifier` as clustered key (16 bytes, not ever-increasing) unless sequentially generated.
- Enforce integrity with constraints (`PRIMARY KEY`, `FOREIGN KEY`, `UNIQUE`, `CHECK`, `NOT NULL`, `DEFAULT`) rather than application logic.

---

## Indexing Rules

- Order multi-column index keys: equality/join columns first, then remaining columns from most distinct to least.
- Use **`INCLUDE`** clause to cover queries with non-key columns. Don't include `nvarchar(max)` / `xml`.
- Before adding an index, check for overlapping indexes. Extend one over creating near-duplicates.
- For large tables, build/rebuild with **`ONLINE`** option and consider row/page data compression.
- Avoid over-indexing — every index has write and storage cost.

---

## Source Control & Deployment

- Treat **schema as code**. Single source of truth: **SDK-style SQL database project** (`Microsoft.Build.Sql`), not live database state.
- Build with `dotnet build` → produces **`.dacpac`** artifact. Run SQL code analysis during build.
- Deploy with **SqlPackage `Publish`** (or `azure/sql-action`). Deployment is diff-based and idempotent.
- Before production deployment, generate a change preview with SqlPackage **`Script`** or **`DeployReport`** and require human approval.
- Pass connection strings via secrets; prefer Entra/managed identity.
- Never run un-reviewed DDL by hand against production.

---

## Data Layer Boundary with dotnet-dev

- This agent owns schema design end-to-end: table definitions, data types, constraints, clustered key strategy, indexes, and the SQL database project producing the dacpac.
- `dotnet-dev` owns all C# code: ADO.NET repositories, Dapper queries, FluentMigrator migration scripts, and the connection factory.
- When `dotnet-dev` needs a schema change, they describe the access need → you design the schema → return the approved DDL as the explicit contract artifact.
- If a FluentMigrator script from `dotnet-dev` diverges from the approved schema, flag the conflict and provide corrected DDL.

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
