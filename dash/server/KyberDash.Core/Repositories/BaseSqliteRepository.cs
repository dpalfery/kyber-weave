using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Processes;
using KyberDash.Core.Resilience;
using Microsoft.Data.Sqlite;

namespace KyberDash.Core.Repositories;

/// <summary>
/// Base SQLite repository wrapping in-process SQLite execution with busy retries and a Polly
/// <see cref="ICircuitBreaker"/> guard so persistent store failures fail fast instead of
/// hanging the daemon.
/// </summary>
public abstract partial class BaseSqliteRepository
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

    [GeneratedRegex(@"\blocked\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LockedWordRegex();

    [GeneratedRegex(@"\bbusy\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BusyWordRegex();

    [GeneratedRegex(@"SQLITE_(?:BUSY|LOCKED)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqliteBusyOrLockedRegex();

    /// <summary>Executes a SQLite batch script through the circuit breaker and returns stdout.</summary>
    protected string ExecuteSqlite(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return CircuitBreaker.Execute(() => ExecuteSqliteCore(sql));
    }

    /// <summary>Executes a single SQLite batch run without modifying circuit-breaker state directly.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Internal engine executes parameterized and literal-escaped SQL scripts.")]
    protected virtual string ExecuteSqliteCore(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        EnsureSqliteProviderInitialized();

        string cleanedSql = StripCliDotCommands(sql);
        if (string.IsNullOrWhiteSpace(cleanedSql))
            return string.Empty;

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeoutMilliseconds / 1000.0)),
        }.ToString();

        for (int attempt = 1; attempt <= busyAttempts; attempt++)
        {
            try
            {
                using SqliteConnection connection = new(connectionString);
                connection.Open();

                using SqliteCommand command = connection.CreateCommand();
                command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(batchTimeout.TotalSeconds));
                command.CommandText = cleanedSql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli

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

    /// <summary>Encodes a UTF-8 string as an injection-proof SQLite <c>CAST(X'...' AS TEXT)</c> literal.</summary>
    protected static string TextExpr(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"CAST(X'{Convert.ToHexString(Encoding.UTF8.GetBytes(value))}' AS TEXT)";
    }

    /// <summary>Encodes a nullable string as either <c>NULL</c> or <see cref="TextExpr"/>.</summary>
    protected static string NullableTextExpr(string? value) =>
        value is null ? "NULL" : TextExpr(value);

    /// <summary>Encodes raw bytes as a SQLite hexadecimal blob literal.</summary>
    protected static string BlobExpr(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return $"X'{Convert.ToHexString(bytes)}'";
    }

    /// <summary>Decodes hex-encoded UTF-8 bytes returned by <c>hex(CAST(... AS BLOB))</c>.</summary>
    protected static string DecodeHexText(string hex) =>
        Encoding.UTF8.GetString(Convert.FromHexString(hex));

    /// <summary>Splits stdout into trimmed non-empty lines.</summary>
    protected static IEnumerable<string> Lines(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Splits stdout into raw non-empty lines preserving tab delimiters.</summary>
    protected static IEnumerable<string> TabLines(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static ProcessStartInfo CreateStartInfo() =>
        new("sqlite3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

    private static bool IsBusy(ProcessResult result)
    {
        string output = result.StandardOutput + "\n" + result.StandardError;
        if (output.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
            || output.Contains("database is busy", StringComparison.OrdinalIgnoreCase)
            || LockedWordRegex().IsMatch(output)
            || BusyWordRegex().IsMatch(output)
            || SqliteBusyOrLockedRegex().IsMatch(output))
        {
            return true;
        }

        return result.ExitCode == SqliteBusyResultCode || result.ExitCode == SqliteLockedResultCode;
    }
}
