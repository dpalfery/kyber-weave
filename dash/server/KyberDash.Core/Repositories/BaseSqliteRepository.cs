using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Processes;
using KyberDash.Core.Resilience;

namespace KyberDash.Core.Repositories;

/// <summary>
/// Base SQLite repository wrapping <c>sqlite3</c> CLI execution with busy retries and a Polly
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

    /// <summary>Circuit breaker guarding <c>sqlite3</c> invocations.</summary>
    protected ICircuitBreaker CircuitBreaker { get; }

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
    protected virtual string ExecuteSqliteCore(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        string boundedInput = $".timeout {busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}\n{sql}";

        for (int attempt = 1; attempt <= busyAttempts; attempt++)
        {
            ProcessStartInfo startInfo = CreateStartInfo();
            startInfo.ArgumentList.Add("-batch");
            startInfo.ArgumentList.Add("-bail");
            startInfo.ArgumentList.Add(DatabasePath);

            ProcessResult result;
            try
            {
                result = ProcessRunner.Run(startInfo, boundedInput, batchTimeout);
            }
            catch (Win32Exception exception)
            {
                throw new SqliteExecutionException("The 'sqlite3' executable is unavailable.", exception);
            }
            catch (TimeoutException exception)
            {
                throw new SqliteExecutionException(
                    "The canonical store did not answer within the time limit, so the sqlite3 batch was stopped.",
                    exception);
            }

            if (result.ExitCode == 0)
                return result.StandardOutput;

            if (IsBusy(result) && attempt < busyAttempts)
                continue;

            string combinedError = (result.StandardOutput + "\n" + result.StandardError).Trim();
            throw new SqliteExecutionException(
                $"sqlite3 exited with code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}: {combinedError}",
                result.ExitCode,
                combinedError);
        }

        throw new SqliteExecutionException("The canonical store operation did not complete.");
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
    protected static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Splits stdout into raw non-empty lines preserving tab delimiters.</summary>
    protected static IEnumerable<string> TabLines(string output) =>
        output.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

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
