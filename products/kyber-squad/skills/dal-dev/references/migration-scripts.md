---
name: dal-dev/migration-scripts
description: FluentMigrator migration conventions — versioning, up/down scripts, idempotency, rollback strategy.
---

# FluentMigrator Migration Scripts

## Ownership

The `dotnet-dev` agent authors FluentMigrator scripts. The `dal-dev` / `sql-database-architect` agent approves the underlying DDL schema before any migration is written. If a migration script diverges from the approved schema (wrong type, missing constraint, dropped index), escalate back to `dal-dev` before writing code.

---

## Commands

```powershell
fluentmigrator migrate    # apply pending migrations
fluentmigrator rollback   # rollback last migration
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

    public override void Down()
    {
        Delete.Table("IngestionJobs");
    }
}
```

---

## Idempotency Rules

- Every `Up()` must be safe to re-run: check for existence before creating (`IfTableDoesNotExist`, `IfIndexDoesNotExist`).
- Every migration **must** implement `Down()` for rollback capability.
- Schema changes approved by `dal-dev` (DDL) must match exactly what FluentMigrator applies — column names, data types, constraints, and index declarations must align.

---

## Migration Rules

- **One migration per schema change** — don't bundle unrelated changes.
- Never modify an already-applied migration. Create a new one to amend.
- Use `WithDefaultValue(SystemMethods.CurrentUTCDateTime)` for audit timestamp columns, not hardcoded values.
- For non-nullable column additions to existing tables: add with a default first, then remove the default in a separate migration if required.
- Schema-qualify all table references (`dbo.TableName`).
- After applying, verify with the SQL database project dacpac that schema state matches expectations.

---

## Conflict Resolution

If a FluentMigrator script conflicts with the dacpac artifact (wrong type, missing constraint, dropped index):

1. Stop — do not apply the migration.
2. Escalate to `dal-dev` / `sql-database-architect` with the DDL difference.
3. Receive corrected DDL and update the migration before applying.
