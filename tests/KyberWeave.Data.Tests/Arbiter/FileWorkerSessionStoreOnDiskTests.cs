using KyberWeave.Core.Arbiter;
using KyberWeave.Core.FileSystem;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Hooks.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The Factory session ledger on a real directory, where two hook processes append to the same child at once.</summary>
public sealed class FileWorkerSessionStoreOnDiskTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FileWorkerSessionStore _store = new(new PhysicalFileSystem());
    private readonly string _home;

    public FileWorkerSessionStoreOnDiskTests()
    {
        _home = Path.Combine(_temp.Path, "home");
        _store.EnsureDirectory(_home);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task TwoThreadsAppendingToTheSameChildLoseNoLineAndTheTypeSurvives()
    {
        const int Rounds = 40;
        using Barrier together = new(2);

        Task writer = Task.Run(() =>
        {
            together.SignalAndWait();
            for (int round = 0; round < Rounds; round++)
            {
                _store.AppendParent(_home, "child", "parent");
            }
        });
        Task types = Task.Run(() =>
        {
            together.SignalAndWait();
            for (int round = 0; round < Rounds; round++)
            {
                _store.AppendType(_home, "child", round % 2 == 0 ? "dev-medium" : "platform-researcher");
            }
        });
        await Task.WhenAll(writer, types);

        string path = WorkerSessionPaths.ChildPathOf(_home, "child");
        string[] lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);

        // Every append landed whole: a writer replacing the file, or a half-written line, would lose one.
        Assert.Equal(Rounds * 2, lines.Length);
        Assert.All(lines, line => Assert.Contains(' ', line));

        WorkerSessionEntry entry = Assert.IsType<WorkerSessionEntry>(_store.Read(_home, "child"));
        Assert.Equal("parent", entry.ParentSessionId);
        Assert.True(entry.AgentType is "dev-medium" or "platform-researcher", entry.AgentType);
    }

    [Fact]
    public void ALaterCorrectionStandsOverEveryTypeAppendedBeforeIt()
    {
        _store.AppendParent(_home, "child", "parent");
        _store.AppendGuess(_home, "child", "dev-medium");
        _store.AppendType(_home, "child", "platform-researcher");

        Assert.Equal("platform-researcher", _store.Read(_home, "child")?.AgentType);
    }
}
