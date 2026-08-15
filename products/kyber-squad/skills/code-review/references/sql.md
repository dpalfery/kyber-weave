# SQL Code Review Best Practices

## Security (SQL Injection & Access Control)
- **Parameterized Queries:** Ensure dynamic SQL is written with parameters or prepared statements rather than string concatenation of user inputs.
- **Least Privilege:** Check that the queries or scripts assume minimal privileges (e.g., not using root/sa users for routine queries).
- **Data Exposure:** Avoid `SELECT *`, especially if sensitive columns exist. Encourage selecting only necessary columns. Ensure sensitive information is handled appropriately (encryption, hashing, masking).

## Query Performance & Efficiency
- **Indexes:** Check if the queries are hitting indexes or likely to cause full scans. Missing or redundant indexes should be flagged. Query plans should be optimal (no unnecessary sorting or scanning).
- **Query Structure & Joins:**
  - Use explicit JOINs (with `ON` clauses) instead of comma joins.
  - Check for large or correlated subqueries that could be refactored into efficient JOINs.
  - Confirm appropriate use of set-based operations; avoid iterative processing in SQL.
- **Anti-patterns:** Flag N+1 query issues, unnecessary use of `SELECT DISTINCT` to mask duplicates, or heavy use of scalar UDFs (user-defined functions) in `SELECT` or `WHERE` clauses.

## Maintainability & Style
- **Conventions:** Use consistent naming conventions for schemas, tables, columns (snake_case or PascalCase).
- **Readability:** Use formatted SQL for readability (uppercase keywords, proper indentation). Follow any existing SQL linters.
- **Migrations:** If reviewing database migration scripts, ensure they are idempotent where possible.

## Business Logic & Correctness
- **JOIN Conditions:** Validate JOIN conditions are correct to avoid accidental cross joins.
- **Filtering logic:** Confirm `WHERE` clauses implement the correct filtering logic and handle corner cases like `NULL` values as intended.
- **Calculations:** Verify the correctness of data transformations (e.g., window functions or subqueries).
- **Transactions:** Ensure statements forming a logical unit of work are handled in a transaction with appropriate commit/rollback logic.
