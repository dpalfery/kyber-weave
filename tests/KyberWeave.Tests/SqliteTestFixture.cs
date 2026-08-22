using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Shared test fixtures and process helpers for sqlite3-backed parity tests.
/// </summary>
internal static class SqliteTestFixture
{
    public static TempDirectory SafeRepository()
    {
        TempDirectory repository = new();
        string stateDirectory = Path.Combine(repository.Path, ".kyber-weave");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(stateDirectory, ".gitignore"), "cache/\n");
        return repository;
    }

    public static void RequireSqlite(string skipMessage = "sqlite3 is unavailable; SQLite parity was not run.")
    {
        ProcessStartInfo startInfo = SqliteStartInfo();
        startInfo.ArgumentList.Add("--version");
        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);
            if (result.ExitCode != 0)
            {
                throw SkipException.ForSkip(skipMessage);
            }
        }
        catch (Win32Exception)
        {
            throw SkipException.ForSkip(skipMessage);
        }
    }

    public static string QuerySqlite(string databasePath, string sql)
    {
        ProcessResult result = RunSqlite(databasePath, sql);
        Assert.Equal(0, result.ExitCode);
        return result.StandardOutput;
    }

    public static ProcessResult RunSqlite(string databasePath, string sql)
    {
        ProcessStartInfo startInfo = SqliteStartInfo();
        startInfo.ArgumentList.Add("-batch");
        startInfo.ArgumentList.Add("-bail");
        startInfo.ArgumentList.Add(databasePath);
        return ProcessRunner.Run(startInfo, sql);
    }

    private static ProcessStartInfo SqliteStartInfo() => new("sqlite3")
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
}
