using System.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The analysis cache over an in-memory checkout: which paths it refuses before it ever touches the database.
/// The in-process SQLite adapter itself, its bounded busy retry included, is proven by the integration tests
/// (SqliteAnalysisPersistenceIntegrationTests). The branch that leaves the cache closed when the native SQLite
/// provider cannot load needs a host without the library, so no automated test covers it.
/// </summary>
public sealed class SqliteAnalysisPersistenceSeamTests
{
    private readonly InMemoryFileSystem _files = new();

    private string WorkingDirectory => Path.Combine(_files.Root, "work");

    private string Root => Path.Combine(WorkingDirectory, "repo");

    private string CacheDirectory => Path.Combine(Root, ".kyber-weave", "cache");

    [Fact]
    public async Task ACheckoutThatDoesNotIgnoreTheCacheStaysClosedAndWritesNothing()
    {
        _files.CreateDirectory(Path.Combine(Root, ".kyber-weave"));
        _files.WriteAllText(Path.Combine(Root, ".kyber-weave", ".gitignore"), "logs/");
        ScriptedProcessExecutor processes = Scripted(_ => Ok("0\n"));
        SqliteAnalysisPersistenceFactory factory = Factory(processes);

        IAnalysisPersistence persistence = await factory.OpenAsync(Root);

        Assert.False(persistence.IsAvailable);
        Assert.False(_files.DirectoryExists(CacheDirectory));
    }

    [Fact]
    public async Task ACacheFolderThatIsALinkIsRefused()
    {
        MakeSafe();
        _files.CreateDirectory(Path.Combine(_files.Root, "elsewhere"));
        _files.CreateDirectoryLink(CacheDirectory, Path.Combine(_files.Root, "elsewhere"));
        ScriptedProcessExecutor processes = Scripted(_ => Ok("0\n"));
        SqliteAnalysisPersistenceFactory factory = Factory(processes);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.OpenAsync(Root));
    }

    [Fact]
    public async Task OpenAsyncLeavesAnUnsafeCheckoutClosedWithoutBlocking()
    {
        _files.CreateDirectory(Path.Combine(Root, ".kyber-weave"));
        _files.WriteAllText(Path.Combine(Root, ".kyber-weave", ".gitignore"), "logs/");
        ScriptedProcessRunner processes = new();
        FakePathResolver paths = new(_files);
        FakeProcessEnvironment environment = new(currentDirectory: WorkingDirectory);
        AnalysisCacheSafety safety = new(_files, paths, environment, processes);
        SqliteAnalysisPersistenceFactory factory = new(_files, paths, environment, processes, safety);

        Assert.False(await factory.IsCacheSafeAsync(Root));
        IAnalysisPersistence persistence = await factory.OpenAsync(Root);

        Assert.False(persistence.IsAvailable);
        Assert.False(_files.DirectoryExists(CacheDirectory));
        Assert.Empty(processes.Calls);
    }

    private static ProcessResult Ok(string output) => new(0, output, string.Empty);

    private static ScriptedProcessExecutor Scripted(Func<ProcessStartInfo, ProcessResult> sqlite) =>
        new(call => call.FileName == "git" ? Ok(string.Empty) : sqlite(call));

    private void MakeSafe()
    {
        _files.CreateDirectory(Path.Combine(Root, ".kyber-weave"));
        _files.WriteAllText(Path.Combine(Root, ".kyber-weave", ".gitignore"), "cache/");
    }

    private SqliteAnalysisPersistenceFactory Factory(ScriptedProcessExecutor processes)
    {
        FakePathResolver paths = new(_files);
        FakeProcessEnvironment environment = new(currentDirectory: WorkingDirectory);
        return new SqliteAnalysisPersistenceFactory(
            _files, paths, environment, processes, new AnalysisCacheSafety(_files, paths, environment, processes));
    }
}
