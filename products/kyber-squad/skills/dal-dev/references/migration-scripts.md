---
name: dal-dev/migration-scripts
description: FluentMigrator migration conventions — versioning, up/down scripts, idempotency, rollback strategy.
---

# FluentMigrator Migration Scripts

## Ownership

The `dal-dev` agent authors FluentMigrator scripts. The `sql-database-architect` agent approves the underlying DDL schema before any migration is written. If a migration script diverges from the approved schema (wrong type, missing constraint, dropped index), escalate back to `sql-database-architect` before applying.

---

## Commands

```powershell
fluentmigrator migrate    # apply pending migrations
fluentmigrator rollback   # rollback last migration — only where the host implements Down()
```

---

## Migration Versioning Convention

Use `[Migration(YYYYMMDDHHMMSS)]` timestamp format for version numbers:

```csharp
[Migration(20240115120000)]
public class AddIngestionJobsTable : Migration
{
    public override void Up()
    {
        Create.Table("IngestionJobs")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Status").AsString(50).NotNullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);
    }

    // Optional — Down() is a host decision; see Idempotency Rules below.
    public override void Down()
    {
        Delete.Table("IngestionJobs");
    }
}
```

---

## Idempotency Rules

- Re-run safety for `Up()` follows **<sql-coding-standard>** § Migrations; FluentMigrator
  provides `IfTableDoesNotExist` / `IfIndexDoesNotExist` for the existence check.
- `Down()` is a host decision, per **<data-access-layer-coding-standard>** § Migrations.
- Schema changes approved by `sql-database-architect` (DDL) must match exactly what FluentMigrator applies — column names, data types, constraints, and index declarations must align.

---

## Migration Rules

- One-migration-per-change and never-edit-an-applied-migration follow
  **<data-access-layer-coding-standard>** § Migrations.
- Use `WithDefaultValue(SystemMethods.CurrentUTCDateTime)` for audit timestamp columns, not hardcoded values.
- The non-nullable-column-addition pattern follows **<data-access-layer-coding-standard>** § Migrations.
- Table-reference qualification follows **<sql-coding-standard>** § T-SQL authoring.
- After applying, verify with the SQL database project dacpac that schema state matches expectations.

---

## Conflict Resolution

If a FluentMigrator script conflicts with the dacpac artifact (wrong type, missing constraint, dropped index):

1. Stop — do not apply the migration.
2. Escalate to `sql-database-architect` with the DDL difference.
3. Receive corrected DDL and update the migration before applying.
