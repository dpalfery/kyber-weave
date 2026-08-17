---
name: dapper-sql
description: Dapper / SQL Server persistence patterns (repositories, parameterized SQL, connection factory).
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Dapper / SQL Server Persistence Patterns

**Trigger:**
This skill MUST be loaded whenever working on:
- Repository implementations in `4-Persistence/MotorcycleRAG.Persistence/Sql/`.
- SQL queries, stored procedures, or database operations.
- Data access layer changes or new repository creation.
- Any file referencing `Dapper`, `SqlConnection`, or `ISqlConnectionFactory`.

---

## CRITICAL: This Project Uses Dapper, NOT Entity Framework

**Do NOT use EF Core patterns.** No `DbContext`, no LINQ-to-SQL, no `DbSet<T>`, no migrations via EF, no `Include()`, no `SaveChangesAsync()`. All data access uses Dapper with raw parameterized SQL.

---

## Project Structure

```
4-Persistence/MotorcycleRAG.Persistence/Sql/
├── SqlConnectionFactory.cs          # ISqlConnectionFactory implementation
├── ServiceCollectionExtensions.cs   # DI registration
└── Repositories/
    ├── UserRepository.cs
    ├── AuditRepository.cs
    ├── UsageRepository.cs
    ├── PlanRepository.cs
    ├── WebSourceRepository.cs
    ├── WebScrapeRunRepository.cs
    ├── ToolConfigurationRepository.cs
    └── ToolConfigurationAuditRepository.cs
```

## Connection Management

### Pattern: ISqlConnectionFactory
```csharp
// ALWAYS inject ISqlConnectionFactory, never create SqlConnection directly
private readonly ISqlConnectionFactory _connectionFactory;

public MyRepository(ISqlConnectionFactory connectionFactory)
{
    _connectionFactory = connectionFactory;
}

// ALWAYS use 'using' with CreateOpenConnectionAsync
using var connection = await _connectionFactory.CreateOpenConnectionAsync();
```

### Connection String Rules
- Retrieved via the standard `IConfiguration` / Azure App Configuration + Key Vault process — never via environment variables. See the policy declared as **Configuration Policy** in the root `AGENTS.md` registry.
- **NEVER** hardcode connection strings
- **NEVER** embed credentials — Azure AD / Managed Identity authentication is enforced
- Factory validates authentication method before creating connections

---

## Query Patterns

### Single Record Retrieval
```csharp
var result = await connection.QueryFirstOrDefaultAsync<T>(sql, new { Id = id });
```

### Multiple Record Retrieval
```csharp
var results = await connection.QueryAsync<T>(sql, parameters);
```

### Single Record with Identity
```csharp
var id = await connection.QuerySingleAsync<int>(sql, parameters);
```

### Insert / Update / Delete
```csharp
await connection.ExecuteAsync(sql, new { Field1 = value1, Field2 = value2 });
```

### Stored Procedures
```csharp
var results = await connection.QueryAsync<T>(
    "EXEC sp_ProcedureName @Param1, @Param2",
    new { Param1 = value1, Param2 = value2 });
```

### Upsert (MERGE)
```csharp
const string sql = @"
    MERGE INTO TableName AS target
    USING (SELECT @Id AS Id) AS source
    ON target.Id = source.Id
    WHEN MATCHED THEN UPDATE SET ...
    WHEN NOT MATCHED THEN INSERT ...;";
await connection.ExecuteAsync(sql, parameters);
```

### Dynamic Parameters (for optional/nullable fields)
```csharp
var parameters = new DynamicParameters();
parameters.Add("@RequiredParam", value);
if (optionalValue.HasValue)
    parameters.Add("@OptionalParam", optionalValue.Value);
else
    parameters.Add("@OptionalParam", DBNull.Value);
```

---

## Transaction Management

```csharp
using var connection = await _connectionFactory.CreateOpenConnectionAsync();
using var transaction = connection.BeginTransaction();
try
{
    await connection.ExecuteAsync(sql1, param1, transaction);
    await connection.ExecuteAsync(sql2, param2, transaction);
    transaction.Commit();
}
catch
{
    transaction.Rollback();
    throw;
}
```

### When to Use Transactions
- Multi-statement operations that must be atomic
- Audit logging paired with data changes
- Any operation where partial completion would leave inconsistent state

### Transaction Rules
- **ALWAYS** pass `transaction` parameter to Dapper methods inside a transaction scope
- **ALWAYS** wrap in try/catch with explicit Rollback
- **NEVER** nest transactions

---

## Repository Pattern

### Interface Location
- Interfaces defined in `3-Domain/MotorcycleRAG.Contracts/Repositories/` (or `Interfaces/`)
- Implementations in `4-Persistence/MotorcycleRAG.Persistence/Sql/Repositories/`

### DI Registration
- Registered in `ServiceCollectionExtensions.AddSqlPersistenceServices()`
- Connection factory: Singleton
- Repositories: Scoped (default) or as specified

### Error Handling
- Wrap database exceptions with `InvalidOperationException` and descriptive messages
- Use structured logging with operation context: `_logger.LogError(ex, "Failed to {Operation} for {Entity} {Id}", ...)`

---

## MUST NOT
- Use Entity Framework Core (`DbContext`, `DbSet`, LINQ queries, `SaveChangesAsync`)
- Create `SqlConnection` directly — always use `ISqlConnectionFactory`
- Use string concatenation for SQL queries — always parameterized
- Store connection strings in appsettings, code, or any file
- Use `SELECT *` — always list explicit columns
- Catch and swallow database exceptions silently

## MUST DO
- Use `ISqlConnectionFactory` for all connection creation
- Use parameterized queries (anonymous objects or `DynamicParameters`)
- Dispose connections with `using` statements
- Use transactions for multi-statement atomic operations
- Follow existing repository pattern (interface in Contracts, implementation in Persistence)
- Register new repositories in `ServiceCollectionExtensions.AddSqlPersistenceServices()`
- Include structured logging for all database operations
- Handle `NULL` values explicitly with `?? DBNull.Value` pattern
