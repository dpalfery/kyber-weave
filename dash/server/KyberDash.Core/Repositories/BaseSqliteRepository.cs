using System.Globalization;
using System.Text;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Resilience;
using Microsoft.Data.Sqlite;

namespace KyberDash.Core.Repositories;

/// <summary>A parameterized SQLite statement and the named parameters bound to it.</summary>
/// <param name="Sql">SQL text whose values are supplied as <c>$name</c> parameters.</param>
/// <param name="Parameters">Parameter values keyed by their <c>$name</c> marker.</param>
public record SqlStatement(string Sql, IReadOnlyDictionary<string, object?> Parameters)
{
    /// <summary>Creates a statement that binds no parameters.</summary>
    /// <param name="sql">SQL text to execute.</param>
    public static SqlStatement Of(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return new SqlStatement(sql, new Dictionary<string, object?>(StringComparer.Ordinal));
    }
}

/// <summary>
/// Base SQLite repository wrapping in-process SQLite execution with busy retries and a Polly
/// <see cref="ICircuitBreaker"/> guard so persistent store failures fail fast instead of
/// hanging the daemon.
/// </summary>
public abstract class BaseSqliteRepository
{

    private const int SqliteBusyResultCode = 5;
    private const int SqliteLockedResultCode = 6;

    private readonly int busyTimeoutMilliseconds;
    private readonly int busyAttempts;
    private readonly TimeSpan batchTimeout;

    /// <summary>Filesystem path to the SQLite database managed by this repository.</summary>
    public string DatabasePath { get; }

    /// <summary>Circuit breaker guarding database invocations.</summary>
    protected ICircuitBreaker CircuitBreaker { get; }

    /// <summary>Ensures the system sqlite3 dynamic library provider is registered once.</summary>
    protected static void EnsureSqliteProviderInitialized() => SqliteProvider.EnsureInitialized();

    /// <summary>Initializes a new <see cref="BaseSqliteRepository"/>.</summary>
    protected BaseSqliteRepository(
        string databasePath,
        ICircuitBreaker circuitBreaker,
        TimeSpan? batchTimeout = null,
        int busyTimeoutMilliseconds = 5000,
        int busyAttempts = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(circuitBreaker);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(busyTimeoutMilliseconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(busyAttempts, 0);

        DatabasePath = Path.GetFullPath(databasePath);
        CircuitBreaker = circuitBreaker;
        this.batchTimeout = batchTimeout ?? TimeSpan.FromSeconds(15);
        this.busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        this.busyAttempts = busyAttempts;
    }

    /// <summary>Executes a single SQLite batch run without modifying circuit-breaker state directly.</summary>
    protected virtual string ExecuteSqliteCore(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        EnsureSqliteProviderInitialized();

        string cleanedSql = StripCliDotCommands(sql);
        if (string.IsNullOrWhiteSpace(cleanedSql))
            return string.Empty;

        string connectionString = BuildConnectionString();

        for (int attempt = 1; attempt <= busyAttempts; attempt++)
        {
            try
            {
                using SqliteConnection connection = new(connectionString);
                connection.Open();

                using SqliteCommand command = CreateCommand(connection, cleanedSql);
                command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(batchTimeout.TotalSeconds));

                using SqliteDataReader reader = command.ExecuteReader();
                StringBuilder sb = new();
                bool hasMore = true;
                while (hasMore)
                {
                    while (reader.Read())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            if (i > 0)
                                sb.Append('\t');
                            if (!reader.IsDBNull(i))
                            {
                                object value = reader.GetValue(i);
                                if (value is byte[] bytes)
                                    sb.Append(Convert.ToHexString(bytes));
                                else
                                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                            }
                        }
                        sb.Append('\n');
                    }
                    hasMore = reader.NextResult();
                }

                return sb.ToString();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusyResultCode or SqliteLockedResultCode)
            {
                if (attempt < busyAttempts)
                {
                    Thread.Sleep(50 * attempt);
                    continue;
                }
                throw new SqliteExecutionException($"sqlite3 error {ex.SqliteErrorCode}: {ex.Message}", ex.SqliteErrorCode, ex.Message);
            }
            catch (SqliteException ex)
            {
                throw new SqliteExecutionException($"sqlite3 error {ex.SqliteErrorCode}: {ex.Message}", ex.SqliteErrorCode, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SqliteExecutionException($"Database access error: {ex.Message}", ex);
            }
        }

        throw new SqliteExecutionException("The canonical store operation did not complete.");
    }

    /// <summary>Executes parameterized statements in one immediate transaction through the circuit breaker.</summary>
    /// <param name="statements">Statements to run, each binding its values by <c>$name</c> parameter.</param>
    protected void ExecuteStatements(IReadOnlyList<SqlStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        CircuitBreaker.Execute(() => ExecuteStatementsCore(statements));
    }

    /// <summary>Executes a parameterized query through the circuit breaker and maps every row.</summary>
    /// <typeparam name="T">Mapped row type.</typeparam>
    /// <param name="sql">SQL text whose values are supplied as <c>$name</c> parameters.</param>
    /// <param name="parameters">Parameter values keyed by their <c>$name</c> marker.</param>
    /// <param name="map">Maps one row; columns are read by name with <see cref="SqliteDataReader.GetOrdinal(string)"/>.</param>
    protected IReadOnlyList<T> Query<T>(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        Func<SqliteDataReader, T> map)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(map);
        return CircuitBreaker.Execute(() => QueryCore(sql, parameters, map));
    }

    private void ExecuteStatementsCore(IReadOnlyList<SqlStatement> statements)
    {
        EnsureSqliteProviderInitialized();
        ExecuteWithRetry(BuildConnectionString(), connection =>
        {
            ExecuteInTransaction(connection, () =>
            {
                foreach (SqlStatement statement in statements)
                {
                    ExecuteCommand(connection, statement.Sql, statement.Parameters);
                }
            });
            return true;
        });
    }

    private IReadOnlyList<T> QueryCore<T>(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        Func<SqliteDataReader, T> map)
    {
        EnsureSqliteProviderInitialized();
        return ExecuteWithRetry(BuildConnectionString(), connection =>
        {
            using SqliteCommand command = CreateCommand(connection, sql);
            command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(batchTimeout.TotalSeconds));
            BindParameters(command, parameters);

            using SqliteDataReader reader = command.ExecuteReader();
            List<T> rows = [];
            while (reader.Read())
            {
                rows.Add(map(reader));
            }
            return (IReadOnlyList<T>)rows;
        });
    }

    private string BuildConnectionString() =>
        new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeoutMilliseconds / 1000.0)),
        }.ToString();

    private T ExecuteWithRetry<T>(string connectionString, Func<SqliteConnection, T> operation)
    {
        for (int attempt = 1; attempt <= busyAttempts; attempt++)
        {
            try
            {
                using SqliteConnection connection = new(connectionString);
                connection.Open();
                return operation(connection);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusyResultCode or SqliteLockedResultCode)
            {
                if (attempt < busyAttempts)
                {
                    Thread.Sleep(50 * attempt);
                    continue;
                }
                throw new SqliteExecutionException($"sqlite3 error {ex.SqliteErrorCode}: {ex.Message}", ex.SqliteErrorCode, ex.Message);
            }
            catch (SqliteException ex)
            {
                throw new SqliteExecutionException($"sqlite3 error {ex.SqliteErrorCode}: {ex.Message}", ex.SqliteErrorCode, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SqliteExecutionException($"Database access error: {ex.Message}", ex);
            }
        }

        throw new SqliteExecutionException("The canonical store operation did not complete.");
    }

    private void ExecuteInTransaction(SqliteConnection connection, Action body)
    {
        ExecuteCommand(connection, "PRAGMA foreign_keys = ON;", null);
        ExecuteCommand(connection, "BEGIN IMMEDIATE;", null);
        bool committed = false;
        try
        {
            body();
            ExecuteCommand(connection, "COMMIT;", null);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                Rollback(connection);
            }
        }
    }

    private void Rollback(SqliteConnection connection)
    {
        try
        {
            ExecuteCommand(connection, "ROLLBACK;", null);
        }
        catch (SqliteException)
        {
            // The original failure is the one that matters; the connection discards a failed rollback.
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Internal engine executes parameterized and literal-escaped SQL scripts.")]
    private static SqliteCommand CreateCommand(SqliteConnection connection, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
        return command;
    }

    private void ExecuteCommand(
        SqliteConnection connection,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        using SqliteCommand command = CreateCommand(connection, sql);
        command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(batchTimeout.TotalSeconds));
        BindParameters(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void BindParameters(
        SqliteCommand command,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (KeyValuePair<string, object?> parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        }
    }

    private static string StripCliDotCommands(string sql)
    {
        string[] lines = sql.Split('\n');
        StringBuilder sb = new();
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('.'))
                continue;
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Decodes hex-encoded UTF-8 bytes returned by SQLite's hexadecimal text projection.</summary>
    protected static string DecodeHexText(string hex) =>
        Encoding.UTF8.GetString(Convert.FromHexString(hex));
}
