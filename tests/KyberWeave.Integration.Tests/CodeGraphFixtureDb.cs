using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Minimal sqlite fixture backing the adapter parity test.
/// </summary>
internal sealed class CodeGraphFixtureDb : IDisposable
{
    public string DatabasePath { get; }

    /// <summary>Creates the database file and its schema.</summary>
    public static async Task<CodeGraphFixtureDb> CreateAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kw-codegraph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        CodeGraphFixtureDb fixture = new(Path.Combine(dir, "codegraph.db"));
        await fixture.RunSqliteAsync(
            "CREATE TABLE nodes (id TEXT, kind TEXT, name TEXT, qualified_name TEXT, file_path TEXT, language TEXT, start_line INTEGER, end_line INTEGER); " +
            "CREATE TABLE edges (source TEXT, target TEXT, kind TEXT);");
        return fixture;
    }

    private CodeGraphFixtureDb(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public async Task IndexSymbol(string name, string filePath, int startLine, int endLine = 0) =>
        await RunSqliteAsync(
            $"INSERT INTO nodes VALUES ('id-{name}', 'class', '{name}', '{name}', '{filePath}', 'csharp', {startLine}, {endLine});");

    public async Task IndexMethod(string name, string filePath, int startLine, int endLine) =>
        await RunSqliteAsync(
            $"INSERT INTO nodes VALUES ('id-{name}-{startLine}', 'method', '{name}', '{name}', '{filePath}', 'csharp', {startLine}, {endLine});");

    public async Task IndexRoute(string route) =>
        await RunSqliteAsync(
            $"INSERT INTO nodes VALUES ('route-{route}', 'route', '{route}', '{route}', '', 'csharp', 0, 0);");

    public async Task IndexFile(string filePath) =>
        await RunSqliteAsync(
            $"INSERT INTO nodes VALUES ('file-{filePath.GetHashCode(StringComparison.Ordinal)}', 'import', 'file', 'file', '{filePath}', 'csharp', 0, 0);");

    public async Task IndexEdge(string sourceId, string targetId, string kind) =>
        await RunSqliteAsync($"INSERT INTO edges VALUES ('{sourceId}', '{targetId}', '{kind}');");

    private async Task RunSqliteAsync(string sql)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("sqlite3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(DatabasePath);
        startInfo.ArgumentList.Add(sql);

        // ProcessRunner drains both pipes before waiting, and kills the tree if sqlite3 has
        // not exited within the limit. A bare WaitForExit has neither property.
        ProcessResult result = await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.StandardError);
    }

    public void Dispose()
    {
        string? dir = Path.GetDirectoryName(DatabasePath);
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
