using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The snapshot documents, written under a lock in an in-memory file system and a controlled clock.</summary>
public sealed class FileHookSnapshotStoreTests : IDisposable
{
    private const string DocumentName = "snapshots.json";

    private static readonly string Directory = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "arbiter"));
    private static readonly string LockPath = Path.Combine(Directory, ".snapshots.lock");

    private readonly ParkObservingTimeProvider _time = new();
    private readonly InMemoryFileSystem _files;
    private readonly FileHookSnapshotStore _store;

    public FileHookSnapshotStoreTests()
    {
        _files = new InMemoryFileSystem(_time);
        _store = new FileHookSnapshotStore(_files, _time, new FakeIdGenerator());
    }

    public void Dispose()
    {
        _time.Dispose();
    }

    [Fact]
    public void ADocumentThatWasNeverWrittenDoesNotExistAndReadsEmpty()
    {
        Assert.False(_store.Exists(Directory, DocumentName));
        Assert.Equal(string.Empty, _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public void AnUpdateWritesTheDocumentTheMutationReturns()
    {
        _store.Update(Directory, DocumentName, _ => "[1]");

        Assert.True(_store.Exists(Directory, DocumentName));
        Assert.Equal("[1]", _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public void AnUpdateIsGivenTheCurrentTextAndReplacesIt()
    {
        _store.Update(Directory, DocumentName, _ => "[1]");
        string? seen = null;

        _store.Update(Directory, DocumentName, current =>
        {
            seen = current;
            return "[1,2]";
        });

        Assert.Equal("[1]", seen);
        Assert.Equal("[1,2]", _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public void AMutationThatReturnsNullLeavesTheDocumentAlone()
    {
        _store.Update(Directory, DocumentName, _ => "[1]");

        _store.Update(Directory, DocumentName, _ => null);

        Assert.Equal("[1]", _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public void AnUpdateThatReturnsNullOnAMissingDocumentCreatesNone()
    {
        _store.Update(Directory, DocumentName, _ => null);

        Assert.False(_store.Exists(Directory, DocumentName));
    }

    [Fact]
    public void AnUpdateLeavesNoTemporaryFileBehind()
    {
        _store.Update(Directory, DocumentName, _ => "[1]");

        Assert.Empty(_files.EnumerateFiles(Directory, "*.tmp", recursive: false));
    }

    [Fact]
    public void AnUpdateSweepsDebrisFromAnInterruptedWrite()
    {
        _files.CreateDirectory(Directory);
        string debris = Path.Combine(Directory, ".snapshots.abandoned.tmp");
        _files.WriteAllText(debris, "partial");

        _store.Update(Directory, DocumentName, _ => "[1]");

        Assert.False(_files.FileExists(debris));
    }

    [Fact]
    public void AMoveThatFailsLeavesThePreviousWholeDocumentAndNoTemporary()
    {
        _store.Update(Directory, DocumentName, _ => "[1]");
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.MoveFile));

        _store.Update(Directory, DocumentName, _ => "[1,2]");

        Assert.Equal("[1]", _store.ReadText(Directory, DocumentName));
        Assert.Empty(_files.EnumerateFiles(Directory, "*.tmp", recursive: false));
    }

    [Fact]
    public void AReadThatFailsReportsAnEmptyDocument()
    {
        _store.Update(Directory, DocumentName, _ => "[1]");
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText));

        Assert.Equal(string.Empty, _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public void ALockHeldElsewhereIsWaitedForAndTheUpdateThenSucceeds()
    {
        _files.CreateDirectory(Directory);
        Stream held = _files.OpenExclusive(LockPath);

        RunUpdateAdvancingTime(afterParks: 3, release: held.Dispose, mutate: _ => "[1]");

        Assert.Equal("[1]", _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public void ALockThatStaysHeldPastTheWaitWindowGivesUpWithoutWriting()
    {
        _files.CreateDirectory(Directory);
        using Stream held = _files.OpenExclusive(LockPath);
        bool mutated = false;

        RunUpdateAdvancingTime(afterParks: int.MaxValue, release: () => { }, mutate: _ =>
        {
            mutated = true;
            return "[1]";
        });

        Assert.False(mutated);
        Assert.False(_store.Exists(Directory, DocumentName));
    }

    [Fact]
    public void AFailureWhileWritingIsAbsorbed()
    {
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.CreateDirectory));

        _store.Update(Directory, DocumentName, _ => "[1]");

        Assert.False(_store.Exists(Directory, DocumentName));
    }

    private void RunUpdateAdvancingTime(int afterParks, Action release, Func<string, string?> mutate) =>
        _ = _time.RunAdvancingOnParks(
            () =>
            {
                _store.Update(Directory, DocumentName, mutate);
                return true;
            },
            TimeSpan.FromMilliseconds(50),
            afterParks,
            release);
}
