---
name: sql-server-reference
description: "Reference for SQL Server and Azure SQL T-SQL work: establish the target engine version, ground version-specific behaviour and syntax in Microsoft Learn with a cited page, inspect the existing schema, indexes and constraints before proposing a change, and use the Microsoft Learn reference index. Load before writing or changing SQL Server schema, T-SQL, stored procedures or index design."
---

# SQL Server reference

## Ground every recommendation in Microsoft Learn

- Confirm version-specific behaviour, deprecation and syntax against Microsoft Learn and
  cite the page you used.
- Prefer `microsoft_docs_search` to find the page and `microsoft_docs_fetch` for detail.
- Establish the target engine first: SQL Server 2016, 2019, 2022 or 2025, Azure SQL
  Database, Azure SQL Managed Instance, or Fabric.

## Inspect before you change

- For an existing database, use the `mssql` tools to read the schema, indexes and
  constraints before proposing an edit. Never assume structure you can verify.

## Hand-off

- Publish the approved table-definition block (column names, data types, nullability, key and
  index declarations) as the contract that data-access and application code consume.
- A schema need arrives described in the task; record the design in the plan, then get it
  approved before publishing the block.

## Reference index

- Security best practices: https://learn.microsoft.com/sql/relational-databases/security/sql-server-security-best-practices
- SQL injection: https://learn.microsoft.com/sql/relational-databases/security/sql-injection
- CREATE PROCEDURE: https://learn.microsoft.com/sql/t-sql/statements/create-procedure-transact-sql
- T-SQL naming issues (SR0016): https://learn.microsoft.com/sql/tools/sql-database-projects/concepts/sql-code-analysis/t-sql-naming-issues
- T-SQL design issues (SR0001/SR0008): https://learn.microsoft.com/sql/tools/sql-database-projects/concepts/sql-code-analysis/t-sql-design-issues
- Index design guide: https://learn.microsoft.com/sql/relational-databases/sql-server-index-design-guide
- SQL database projects: https://learn.microsoft.com/sql/tools/sql-database-projects/sql-database-projects
- SqlPackage: https://learn.microsoft.com/sql/tools/sqlpackage/sqlpackage
