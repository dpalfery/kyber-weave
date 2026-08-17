---
name: dal-dev/adonet-repository
description: ADO.NET repository pattern — ISqlConnectionFactory, IRepository<T>, parameterized SqlCommand, DI registration.
---

# ADO.NET Repository Pattern

This is procedure. Persistence policy — ADO.NET, not Dapper, not Entity Framework — is the
path declared as **<data-access-layer-coding-standard>**.

Do not use EF Core (`DbContext`, `DbSet<T>`, `Include()`, `SaveChangesAsync()`) or Dapper
(`QueryAsync`, `ExecuteAsync`, `DynamicParameters`).

---

## Connection management

Always inject `ISqlConnectionFactory`. Never `new SqlConnection(...)`.

```csharp
public sealed class ExampleRepository : IExampleRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ILogger<ExampleRepository> _logger;

    public ExampleRepository(
        ISqlConnectionFactory connectionFactory,
        ILogger<ExampleRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<ExampleRow?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, Name
            FROM dbo.Example
            WHERE Id = @Id;
            """;

        await using SqlConnection connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
        await using SqlCommand command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExampleRow(
            reader.GetInt32(0),
            reader.GetString(1));
    }
}
```

Connection strings come from configuration / Key Vault / managed identity, as declared by
the **Configuration Policy** registry property. Never a literal, never a committed file.

---

## Commands

```csharp
command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = name;
command.Parameters.Add("@Optional", SqlDbType.Int).Value = optional ?? (object)DBNull.Value;

int affected = await command.ExecuteNonQueryAsync(cancellationToken);
object? scalar = await command.ExecuteScalarAsync(cancellationToken);
```

For a `MERGE` or other multi-statement batch, keep the SQL in a constant and pass every
value as a typed parameter. Do not interpolate.

---

## Transactions

```csharp
await using SqlConnection connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);
await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
try
{
    await using SqlCommand first = new SqlCommand(sql1, connection, transaction);
    first.Parameters.Add("@Id", SqlDbType.Int).Value = id;
    await first.ExecuteNonQueryAsync(cancellationToken);

    await using SqlCommand second = new SqlCommand(sql2, connection, transaction);
    second.Parameters.Add("@Id", SqlDbType.Int).Value = id;
    await second.ExecuteNonQueryAsync(cancellationToken);

    await transaction.CommitAsync(cancellationToken);
}
catch
{
    await transaction.RollbackAsync(cancellationToken);
    throw;
}
```

Use a transaction for multi-statement work that must be atomic, including an audit write
paired with a data change. Do not nest transactions. Every command inside the scope takes
the same `SqlTransaction`.

---

## Placement and registration

- Interface: `<Solution>.Contracts`
- Implementation and persistence row: `<Solution>.Persistence`
- DI: `ServiceCollectionExtensions.AddSqlPersistenceServices()` — factory Singleton,
  repositories Scoped

On failure, log with structured properties and wrap with context. Do not swallow.

```csharp
_logger.LogError(ex, "Failed to {Operation} for {Entity} {Id}", operation, entityName, id);
throw new InvalidOperationException($"Failed to {operation} {entityName}.", ex);
```
