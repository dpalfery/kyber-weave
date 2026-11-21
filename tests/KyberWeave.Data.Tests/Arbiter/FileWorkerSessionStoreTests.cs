using KyberWeave.Core.Arbiter;
using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The Factory session ledger beside the worker budget files, kept in an in-memory file system.</summary>
public sealed class FileWorkerSessionStoreTests
{
    private static readonly string Home = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "home"));

    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryFileSystem _files;
    private readonly FileWorkerSessionStore _store;

    public FileWorkerSessionStoreTests()
    {
        _files = new InMemoryFileSystem(_time);
        _store = new FileWorkerSessionStore(_files);
        _store.EnsureDirectory(Home);
    }

    [Fact]
    public void ASessionThatWasNeverRecordedDoesNotExistAndReadsNull()
    {
        Assert.False(_store.Exists(Home, "child"));
        Assert.Null(_store.Read(Home, "child"));
    }

    [Fact]
    public void ARecordedChildKeepsItsParentAndType()
    {
        _store.Write(Home, "child", new WorkerSessionEntry("parent", "dev-medium"));

        Assert.True(_store.Exists(Home, "child"));
        WorkerSessionEntry entry = Assert.IsType<WorkerSessionEntry>(_store.Read(Home, "child"));
        Assert.Equal("parent", entry.ParentSessionId);
        Assert.Equal("dev-medium", entry.AgentType);
    }

    [Fact]
    public void TheTypeResolvedAfterTheParentReplacesTheUnresolvedEntry()
    {
        _store.Write(Home, "child", new WorkerSessionEntry("parent", null));
        _store.Write(Home, "child", new WorkerSessionEntry("parent", "platform-researcher"));

        WorkerSessionEntry entry = Assert.IsType<WorkerSessionEntry>(_store.Read(Home, "child"));
        Assert.Equal("platform-researcher", entry.AgentType);
    }

    [Fact]
    public void AwaitedTypeReturnsATypeAlreadyRecorded()
    {
        _store.Write(Home, "child", new WorkerSessionEntry("parent", "dev-medium"));

        WorkerSessionEntry? entry = _store.AwaitType(Home, "child", TimeSpan.FromSeconds(3));

        Assert.Equal("dev-medium", entry?.AgentType);
    }

    [Fact]
    public void AwaitedTypeAnswersNullForAChildWhoseTypeNeverArrives()
    {
        _store.Write(Home, "child", new WorkerSessionEntry("parent", null));

        WorkerSessionEntry? entry = _store.AwaitType(Home, "child", TimeSpan.FromMilliseconds(10));

        Assert.Null(entry?.AgentType);
    }

    [Fact]
    public void DeletingRemovesOnlyThatChildAndAMissingOneIsNotAnError()
    {
        _store.Write(Home, "gone", new WorkerSessionEntry("parent", "dev-medium"));
        _store.Write(Home, "kept", new WorkerSessionEntry("parent", "dev-medium"));

        _store.Delete(Home, "gone");
        _store.Delete(Home, "never-existed");

        Assert.False(_store.Exists(Home, "gone"));
        Assert.True(_store.Exists(Home, "kept"));
    }

    [Fact]
    public void PendingTypesAppendInOrder()
    {
        _store.AppendPending(Home, "parent", "+dev-medium");
        _store.AppendPending(Home, "parent", "+platform-researcher");

        Assert.Equal(["+dev-medium", "+platform-researcher"], _store.ReadPending(Home, "parent"));
    }

    [Fact]
    public void SweepingRemovesSessionsOlderThanTheRetentionAndKeepsTheRest()
    {
        _store.Write(Home, "old", new WorkerSessionEntry("parent", "dev-medium"));
        _store.AppendPending(Home, "old-pending", "+dev-medium");
        _time.Advance(TimeSpan.FromDays(3));
        _store.Write(Home, "recent", new WorkerSessionEntry("parent", "dev-medium"));

        _store.Sweep(Home, _time.GetUtcNow(), TimeSpan.FromDays(2));

        Assert.False(_store.Exists(Home, "old"));
        Assert.Empty(_store.ReadPending(Home, "old-pending"));
        Assert.True(_store.Exists(Home, "recent"));
    }
}
