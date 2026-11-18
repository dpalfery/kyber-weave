---
id: standards/sql-server/skills
title: "sql-server skills"
doc-type: skill
status: draft
technology: sql-server
owner: unassigned
last-reviewed: 2026-11-16
---

# sql-server skills

## Ground every recommendation in Microsoft Learn

- Confirm version-specific behaviour, deprecation and syntax against Microsoft Learn and
  cite the page you used.
- Prefer `microsoft_docs_search` to find the page and `microsoft_docs_fetch` for detail.
- Establish the target engine first: SQL Server 2016, 2019, 2022 or 2025, Azure SQL
  Database, Azure SQL Managed Instance, or Fabric.

## Inspect before you change

- For an existing database, use the `mssql` tools to read the schema, indexes and
  constraints before proposing an edit. Never assume structure you can verify.

## Reference index

- Security best practices: https://learn.microsoft.com/sql/relational-databases/security/sql-server-security-best-practices
- SQL injection: https://learn.microsoft.com/sql/relational-databases/security/sql-injection
- CREATE PROCEDURE: https://learn.microsoft.com/sql/t-sql/statements/create-procedure-transact-sql
- T-SQL naming issues (SR0016): https://learn.microsoft.com/sql/tools/sql-database-projects/concepts/sql-code-analysis/t-sql-naming-issues
- T-SQL design issues (SR0001/SR0008): https://learn.microsoft.com/sql/tools/sql-database-projects/concepts/sql-code-analysis/t-sql-design-issues
- Index design guide: https://learn.microsoft.com/sql/relational-databases/sql-server-index-design-guide
- SQL database projects: https://learn.microsoft.com/sql/tools/sql-database-projects/sql-database-projects
- SqlPackage: https://learn.microsoft.com/sql/tools/sqlpackage/sqlpackage
