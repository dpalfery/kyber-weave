using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests;

/// <summary>
/// Minimal sqlite fixture backing the adapter parity test.
/// </summary>
internal sealed class CodeGraphFixtureDb : IDisposable
{
    public string DatabasePath { get; }

    public CodeGraphFixtureDb()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kw-codegraph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        DatabasePath = Path.Combine(dir, "codegraph.db");
        RunSqlite(
            "CREATE TABLE nodes (id TEXT, kind TEXT, name TEXT, qualified_name TEXT, file_path TEXT, language TEXT, start_line INTEGER); " +
            "CREATE TABLE edges (source TEXT, target TEXT, kind TEXT);");
    }

    public void IndexSymbol(string name, string filePath, int startLine) =>
        RunSqlite(
            $"INSERT INTO nodes VALUES ('id-{name}', 'class', '{name}', '{name}', '{filePath}', 'csharp', {startLine});");

    public void IndexRoute(string route) =>
        RunSqlite(
            $"INSERT INTO nodes VALUES ('route-{route}', 'route', '{route}', '{route}', '', 'csharp', 0);");

    public void IndexFile(string filePath) =>
        RunSqlite(
            $"INSERT INTO nodes VALUES ('file-{filePath.GetHashCode()}', 'import', 'file', 'file', '{filePath}', 'csharp', 0);");

    public void IndexEdge(string sourceId, string targetId, string kind) =>
        RunSqlite($"INSERT INTO edges VALUES ('{sourceId}', '{targetId}', '{kind}');");

    private void RunSqlite(string sql)
    {
        ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo("sqlite3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(DatabasePath);
        startInfo.ArgumentList.Add(sql);

        using Process process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start sqlite3.");

        // Drain before waiting: WaitForExit with undrained redirected pipes deadlocks as
        // soon as the child writes more than the pipe buffer holds.
        ProcessResult result = KyberWeave.Core.Processes.ProcessRunner.ReadToEnd(process);
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
