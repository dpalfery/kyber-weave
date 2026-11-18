using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The analysis cache over an in-memory checkout and a scripted <c>sqlite3</c>: when it opens, where it refuses to,
/// and how it retries. The real executable is proven by the integration tests.
/// </summary>
public sealed class SqliteAnalysisPersistenceSeamTests
{
    private readonly InMemoryFileSystem _files = new();

    private string WorkingDirectory => Path.Combine(_files.Root, "work");

    private string Root => Path.Combine(WorkingDirectory, "repo");

    private string CacheDirectory => Path.Combine(Root, ".kyber-weave", "cache");

    [Fact]
    public void ASafeCheckoutWithSqliteOpensTheCacheAndCreatesItsFolder()
    {
        MakeSafe();
        ScriptedProcessExecutor processes = Scripted(_ => Ok("0\n"));

        SqliteAnalysisPersistence persistence = Open(processes);

        Assert.True(persistence.IsAvailable);
        Assert.Equal(Path.Combine(CacheDirectory, "docs-analysis.sqlite3"), persistence.DatabasePath);
        Assert.True(_files.DirectoryExists(CacheDirectory));
        Assert.Equal("--version", processes.CallsTo("sqlite3").First().ArgumentList[0]);
        Assert.Contains(processes.Inputs, input => input.StartsWith(".timeout 250\n", StringComparison.Ordinal));
    }

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

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(TimeoutException))]
    public void ASqliteThatCannotBeStartedLeavesTheCacheClosed(Type failure)
    {
        MakeSafe();
        ScriptedProcessExecutor processes = Scripted(_ => throw (Exception)Activator.CreateInstance(failure)!);

        SqliteAnalysisPersistence persistence = Open(processes);

        Assert.False(persistence.IsAvailable);
        Assert.False(_files.DirectoryExists(CacheDirectory));
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

    [Fact]
    public void ABusyDatabaseIsRetriedAndTheCallThatFollowsSucceeds()
    {
        MakeSafe();
        int batches = 0;
        ScriptedProcessExecutor processes = Scripted(call =>
            call.ArgumentList.Contains("-bail") && batches++ == 0 ? new ProcessResult(5, "", "database is locked") : Ok("0\n"));

        SqliteAnalysisPersistence persistence = Open(processes);

        Assert.True(persistence.IsAvailable);
        Assert.True(batches >= 2);
    }

    [Fact]
    public void ADatabaseThatStaysBusyFailsAfterThreeBoundedAttempts()
    {
        MakeSafe();
        ScriptedProcessExecutor processes = Scripted(call =>
            call.ArgumentList.Contains("-bail") ? new ProcessResult(5, "", "database is locked") : Ok("0\n"));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => Open(processes));

        Assert.Contains("3 bounded attempts", failure.Message, StringComparison.Ordinal);
        Assert.Equal(3, processes.CallsTo("sqlite3").Count(call => call.ArgumentList.Contains("-bail")));
    }

    [Fact]
    public void ABatchThatOutlastsItsLimitFailsWithTheTimeoutAsItsCause()
    {
        MakeSafe();
        ScriptedProcessExecutor processes = Scripted(call =>
            call.ArgumentList.Contains("-bail") ? throw new TimeoutException() : Ok("0\n"));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => Open(processes));

        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.Equal(TimeSpan.FromSeconds(120), processes.Timeouts[^1]);
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
