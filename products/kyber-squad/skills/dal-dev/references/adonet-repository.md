---
name: dal-dev/adonet-repository
description: ADO.NET repository pattern — ISqlConnectionFactory, IRepository<T>, parameterized SqlCommand, DI registration.
---

# ADO.NET Repository Pattern

This is procedure. Persistence policy — ADO.NET, not Dapper, not Entity Framework — is the
path declared as **<data-access-layer-coding-standard>**.

The excluded ORMs and the APIs that signal drift toward them are named in
**<data-access-layer-coding-standard>** § Stack.

---

## Connection management

Follow **<data-access-layer-coding-standard>** § Stack for the connection-factory contract.

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

Connection-string sourcing follows **<data-access-layer-coding-standard>** § Hard rules.

---

## Commands

```csharp
command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = name;
command.Parameters.Add("@Optional", SqlDbType.Int).Value = optional ?? (object)DBNull.Value;

int affected = await command.ExecuteNonQueryAsync(cancellationToken);
object? scalar = await command.ExecuteScalarAsync(cancellationToken);
```

Parameter typing for a `MERGE` or other multi-statement batch follows
**<data-access-layer-coding-standard>** § Hard rules.

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

Transaction scope and nesting follow **<data-access-layer-coding-standard>** § Hard rules.
Every command inside the scope takes the same `SqlTransaction`.

---

## Placement and registration

- Interface: `<Solution>.Contracts`
- Implementation and persistence row: `<Solution>.Persistence`
- DI: `ServiceCollectionExtensions.AddSqlPersistenceServices()` — lifetimes follow
  **<data-access-layer-coding-standard>** § Hard rules

Failure handling — structured logging and never swallowing — follows
**<data-access-layer-coding-standard>** § Hard rules.

```csharp
_logger.LogError(ex, "Failed to {Operation} for {Entity} {Id}", operation, entityName, id);
throw new InvalidOperationException($"Failed to {operation} {entityName}.", ex);
```
