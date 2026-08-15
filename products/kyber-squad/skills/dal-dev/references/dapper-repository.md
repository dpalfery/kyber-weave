---
name: dal-dev/dapper-repository
description: Dapper repository pattern — ISqlConnectionFactory, IRepository<T>, parameterized SQL, DI registration.
---

# Dapper Repository Pattern

## CRITICAL: This Project Uses Dapper, NOT Entity Framework

**Do NOT use EF Core patterns.** No `DbContext`, no LINQ-to-SQL, no `DbSet<T>`, no `Include()`, no `SaveChangesAsync()`. All data access uses Dapper with raw parameterized SQL.

---

## Project Structure

```
4-Persistence/MotorcycleRAG.Persistence/Sql/
├── SqlConnectionFactory.cs          # ISqlConnectionFactory implementation
├── ServiceCollectionExtensions.cs   # DI registration
└── Repositories/
    ├── UserRepository.cs
    ├── AuditRepository.cs
    └── ...
```

---

## Connection Management

### Pattern: ISqlConnectionFactory

```csharp
// ALWAYS inject ISqlConnectionFactory — never create SqlConnection directly
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

---

## Query Patterns

```csharp
// Single record
var result = await connection.QueryFirstOrDefaultAsync<T>(sql, new { Id = id });

// Multiple records
var results = await connection.QueryAsync<T>(sql, parameters);

// Insert/Update/Delete
await connection.ExecuteAsync(sql, new { Field1 = value1, Field2 = value2 });

// Upsert (MERGE)
const string sql = @"
    MERGE INTO TableName AS target
    USING (SELECT @Id AS Id) AS source
    ON target.Id = source.Id
    WHEN MATCHED THEN UPDATE SET ...
    WHEN NOT MATCHED THEN INSERT ...;";
await connection.ExecuteAsync(sql, parameters);

// Dynamic/nullable parameters
var parameters = new DynamicParameters();
parameters.Add("@RequiredParam", value);
parameters.Add("@OptionalParam", optionalValue.HasValue ? optionalValue.Value : (object)DBNull.Value);
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

**When to use transactions:** Multi-statement operations that must be atomic; audit logging paired with data changes.

**Transaction rules:** Always pass `transaction` to Dapper methods inside a transaction; always wrap with try/catch + Rollback; never nest transactions.

---

## Interface & Registration Rules

- **Interface location:** `3-Domain/MotorcycleRAG.Contracts/Repositories/` (or `Interfaces/`)
- **Implementation location:** `4-Persistence/MotorcycleRAG.Persistence/Sql/Repositories/`
- **DI registration:** `ServiceCollectionExtensions.AddSqlPersistenceServices()`
  - Connection factory: Singleton
  - Repositories: Scoped

### Error Handling

```csharp
// Wrap DB exceptions with descriptive context
_logger.LogError(ex, "Failed to {Operation} for {Entity} {Id}", operation, entityName, id);
throw new InvalidOperationException($"Failed to {operation} {entityName}", ex);
```

---

## MUST NOT

- Use Entity Framework Core (`DbContext`, `DbSet`, LINQ queries, `SaveChangesAsync`)
- Create `SqlConnection` directly — always use `ISqlConnectionFactory`
- Use string concatenation for SQL queries — always parameterized
- Store connection strings in appsettings, code, or any tracked file
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
