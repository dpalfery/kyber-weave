using System;
using System.Collections.Generic;
using System.IO;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Repositories;
using KyberDash.Core.Resilience;
using Microsoft.Data.Sqlite;
using Xunit;
using SqlStatement = KyberDash.Core.Repositories.SqlStatement;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Verifies the parameterized command path on <see cref="BaseSqliteRepository"/>: hostile text
/// round-trips as data, a null parameter stores SQL NULL, and a failed batch rolls back the
/// statements that preceded it.
/// </summary>
public sealed class BaseSqliteRepositoryParameterTests : IDisposable
{
    private readonly string directory;
    private readonly ParameterTestRepository repository;

    public BaseSqliteRepositoryParameterTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "kyberdash-parameters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        repository = new ParameterTestRepository(Path.Combine(directory, "store.db"), new SqliteCircuitBreaker());
        repository.Execute([SqlStatement.Of("CREATE TABLE x (id INTEGER PRIMARY KEY, value TEXT);")]);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExecuteStatementsAndQuery_RoundTripHostileTextUnchanged()
    {
        const string hostile = "quote ' and \"double\"\nnewline; semicolon\n'; DROP TABLE x; --";

        repository.Execute(
        [
            InsertStatement(1, hostile),
        ]);

        IReadOnlyList<string?> values = repository.SelectValues();

        Assert.Single(values);
        Assert.Equal(hostile, values[0]);
    }

    [Fact]
    public void ExecuteStatements_NullParameterStoresSqlNull()
    {
        repository.Execute(
        [
            InsertStatement(1, null),
        ]);

        IReadOnlyList<string?> values = repository.SelectValues();
        long nullCount = repository.SelectNullCount();

        Assert.Single(values);
        Assert.Null(values[0]);
        Assert.Equal(1, nullCount);
    }

    [Fact]
    public void ExecuteStatements_WhenSecondStatementFails_RollsBackFirst()
    {
        Assert.Throws<SqliteExecutionException>(() => repository.Execute(
        [
            InsertStatement(1, "first"),
            SqlStatement.Of("INSERT INTO missing_table (id) VALUES (1);"),
        ]));

        Assert.Equal(0, repository.SelectRowCount());
    }

    private static SqlStatement InsertStatement(long id, string? value) =>
        new(
            "INSERT INTO x (id, value) VALUES ($id, $value);",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["$id"] = id,
                ["$value"] = value,
            });

    private sealed class ParameterTestRepository : BaseSqliteRepository
    {
        public ParameterTestRepository(string databasePath, ICircuitBreaker circuitBreaker)
            : base(databasePath, circuitBreaker)
        {
        }

        public void Execute(IReadOnlyList<SqlStatement> statements) => ExecuteStatements(statements);

        public IReadOnlyList<string?> SelectValues() =>
            Query(
                "SELECT value FROM x ORDER BY id;",
                new Dictionary<string, object?>(StringComparer.Ordinal),
                reader =>
                {
                    int ordinal = reader.GetOrdinal("value");
                    return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
                });

        public long SelectNullCount() =>
            SelectScalar("null_count", "SELECT COUNT(*) AS null_count FROM x WHERE value IS NULL;");

        public long SelectRowCount() =>
            SelectScalar("row_count", "SELECT COUNT(*) AS row_count FROM x;");

        private long SelectScalar(string columnName, string sql) =>
            Query(
                sql,
                new Dictionary<string, object?>(StringComparer.Ordinal),
                reader => reader.GetInt64(reader.GetOrdinal(columnName)))[0];
    }
}
