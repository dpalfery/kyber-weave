using System.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The analysis cache over an in-memory checkout: which paths it refuses before it ever touches the database.
/// The in-process SQLite adapter itself is proven by the integration tests.
/// </summary>
public sealed class SqliteAnalysisPersistenceSeamTests
{
    private readonly InMemoryFileSystem _files = new();

    private string WorkingDirectory => Path.Combine(_files.Root, "work");

    private string Root => Path.Combine(WorkingDirectory, "repo");

    private string CacheDirectory => Path.Combine(Root, ".kyber-weave", "cache");

    [Fact]
    public void ACheckoutThatDoesNotIgnoreTheCacheStaysClosedAndWritesNothing()
    {
        _files.CreateDirectory(Path.Combine(Root, ".kyber-weave"));
        _files.WriteAllText(Path.Combine(Root, ".kyber-weave", ".gitignore"), "logs/");
        ScriptedProcessExecutor processes = Scripted(_ => Ok("0\n"));

        SqliteAnalysisPersistence persistence = Open(processes);

        Assert.False(persistence.IsAvailable);
        Assert.False(_files.DirectoryExists(CacheDirectory));
        Assert.Empty(processes.CallsTo("sqlite3"));
    }

    [Fact]
    public void ACacheFolderThatIsALinkIsRefused()
    {
        MakeSafe();
        _files.CreateDirectory(Path.Combine(_files.Root, "elsewhere"));
        _files.CreateDirectoryLink(CacheDirectory, Path.Combine(_files.Root, "elsewhere"));
        ScriptedProcessExecutor processes = Scripted(_ => Ok("0\n"));

        _ = Assert.Throws<InvalidOperationException>(() => Open(processes));

        Assert.Empty(processes.CallsTo("sqlite3"));
    }

    private static ProcessResult Ok(string output) => new(0, output, string.Empty);

    private static ScriptedProcessExecutor Scripted(Func<ProcessStartInfo, ProcessResult> sqlite) =>
        new(call => call.FileName == "git" ? Ok(string.Empty) : sqlite(call));

    private void MakeSafe()
    {
        _files.CreateDirectory(Path.Combine(Root, ".kyber-weave"));
        _files.WriteAllText(Path.Combine(Root, ".kyber-weave", ".gitignore"), "cache/");
    }

    private SqliteAnalysisPersistence Open(ScriptedProcessExecutor processes)
    {
        FakePathResolver paths = new(_files);
        FakeProcessEnvironment environment = new(currentDirectory: WorkingDirectory);
        return new SqliteAnalysisPersistence(
            Root, _files, paths, environment, processes, new AnalysisCacheSafety(_files, paths, environment, processes));
    }
}
