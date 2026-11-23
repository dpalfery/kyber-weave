using KyberWeave.Core.Arbiter;
using KyberWeave.Data.Hooks;
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
        _store.AppendParent(Home, "child", "parent");
        _store.AppendType(Home, "child", "dev-medium");

        Assert.True(_store.Exists(Home, "child"));
        WorkerSessionEntry entry = Assert.IsType<WorkerSessionEntry>(_store.Read(Home, "child"));
        Assert.Equal("parent", entry.ParentSessionId);
        Assert.Equal("dev-medium", entry.AgentType);
    }

    [Fact]
    public void TheTypeResolvedAfterTheParentIsAppendedRatherThanReplacingTheFile()
    {
        _store.AppendParent(Home, "child", "parent");
        _store.AppendType(Home, "child", "platform-researcher");

        WorkerSessionEntry entry = Assert.IsType<WorkerSessionEntry>(_store.Read(Home, "child"));
        Assert.Equal("parent", entry.ParentSessionId);
        Assert.Equal("platform-researcher", entry.AgentType);
    }

    [Fact]
    public void ACorrectionStandsOverAGuessWhateverOrderTheTwoWereAppendedIn()
    {
        _store.AppendParent(Home, "guessed-first", "parent");
        _store.AppendGuess(Home, "guessed-first", "dev-medium");
        _store.AppendType(Home, "guessed-first", "platform-researcher");

        _store.AppendParent(Home, "corrected-first", "parent");
        _store.AppendType(Home, "corrected-first", "platform-researcher");
        _store.AppendGuess(Home, "corrected-first", "dev-medium");

        Assert.Equal("platform-researcher", _store.Read(Home, "guessed-first")?.AgentType);
        Assert.Equal("platform-researcher", _store.Read(Home, "corrected-first")?.AgentType);
    }

    [Fact]
    public void ABlankTypeIsNeverWrittenOverOneThatAlreadyStands()
    {
        _store.AppendParent(Home, "child", "parent");
        _store.AppendType(Home, "child", "dev-medium");

        _store.AppendGuess(Home, "child", " ");
        _store.AppendType(Home, "child", string.Empty);

        Assert.Equal("dev-medium", _store.Read(Home, "child")?.AgentType);
    }

    [Fact]
    public void AwaitedTypeReturnsATypeAlreadyRecorded()
    {
        _store.AppendParent(Home, "child", "parent");
        _store.AppendType(Home, "child", "dev-medium");

        WorkerSessionEntry? entry = _store.AwaitType(Home, "child", TimeSpan.FromSeconds(3));

        Assert.Equal("dev-medium", entry?.AgentType);
    }

    [Fact]
    public void AwaitedTypeAnswersNullForAChildWhoseTypeNeverArrives()
    {
        _store.AppendParent(Home, "child", "parent");

        WorkerSessionEntry? entry = _store.AwaitType(Home, "child", TimeSpan.FromMilliseconds(10));

        Assert.Null(entry?.AgentType);
    }

    [Fact]
    public void DeletingRemovesOnlyThatChildAndAMissingOneIsNotAnError()
    {
        _store.AppendParent(Home, "gone", "parent");
        _store.AppendType(Home, "gone", "dev-medium");
        _store.AppendParent(Home, "kept", "parent");
        _store.AppendType(Home, "kept", "dev-medium");

        _store.Delete(Home, "gone");
        _store.Delete(Home, "never-existed");

        Assert.False(_store.Exists(Home, "gone"));
        Assert.True(_store.Exists(Home, "kept"));
    }

    [Fact]
    public void DeletingAPendingListLeavesTheChildEntriesAlone()
    {
        _store.AppendParent(Home, "child", "parent");
        _store.AppendPending(Home, "pending-parent", "+dev-medium");

        _store.DeletePending(Home, "pending-parent");

        Assert.Empty(_store.ReadPending(Home, "pending-parent"));
        Assert.True(_store.Exists(Home, "child"));
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
        _store.AppendParent(Home, "old", "parent");
        _store.AppendPending(Home, "old-pending", "+dev-medium");
        _time.Advance(TimeSpan.FromDays(3));
        _store.AppendParent(Home, "recent", "parent");

        _store.Sweep(Home, _time.GetUtcNow(), TimeSpan.FromDays(2));

        Assert.False(_store.Exists(Home, "old"));
        Assert.Empty(_store.ReadPending(Home, "old-pending"));
        Assert.True(_store.Exists(Home, "recent"));
    }

    [Fact]
    public void ASweepOfALedgerThatWasNeverWrittenCreatesNothing()
    {
        string untouched = Path.Combine(Home, "never-written");

        _store.Sweep(untouched, DateTimeOffset.UtcNow, TimeSpan.FromDays(2));

        Assert.False(_files.DirectoryExists(Path.Combine(untouched, ".kyber-weave", "arbiter", "sessions")));
    }
}
