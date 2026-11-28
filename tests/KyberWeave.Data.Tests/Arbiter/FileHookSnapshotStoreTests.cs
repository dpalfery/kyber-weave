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
    public async Task AnUpdateWritesTheDocumentTheMutationReturns()
    {
        await _store.UpdateAsync(Directory, DocumentName, _ => "[1]", CancellationToken.None);

        Assert.True(_store.Exists(Directory, DocumentName));
        Assert.Equal("[1]", _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public async Task AnUpdateIsGivenTheCurrentTextAndReplacesIt()
    {
        await _store.UpdateAsync(Directory, DocumentName, _ => "[1]", CancellationToken.None);
        string? seen = null;

        await _store.UpdateAsync(Directory, DocumentName, current =>
        {
            seen = current;
            return "[1,2]";
        }, CancellationToken.None);

        Assert.Equal("[1]", seen);
        Assert.Equal("[1,2]", _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public async Task AMutationThatReturnsNullLeavesTheDocumentAlone()
    {
        await _store.UpdateAsync(Directory, DocumentName, _ => "[1]", CancellationToken.None);

        await _store.UpdateAsync(Directory, DocumentName, _ => null, CancellationToken.None);

        Assert.Equal("[1]", _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public async Task AnUpdateThatReturnsNullOnAMissingDocumentCreatesNone()
    {
        await _store.UpdateAsync(Directory, DocumentName, _ => null, CancellationToken.None);

        Assert.False(_store.Exists(Directory, DocumentName));
    }

    [Fact]
    public async Task AnUpdateLeavesNoTemporaryFileBehind()
    {
        await _store.UpdateAsync(Directory, DocumentName, _ => "[1]", CancellationToken.None);

        Assert.Empty(_files.EnumerateFiles(Directory, "*.tmp", recursive: false));
    }

    [Fact]
    public async Task AnUpdateSweepsDebrisFromAnInterruptedWrite()
    {
        _files.CreateDirectory(Directory);
        string debris = Path.Combine(Directory, ".snapshots.abandoned.tmp");
        _files.WriteAllText(debris, "partial");

        await _store.UpdateAsync(Directory, DocumentName, _ => "[1]", CancellationToken.None);

        Assert.False(_files.FileExists(debris));
    }

    [Fact]
    public async Task AMoveThatFailsLeavesThePreviousWholeDocumentAndNoTemporary()
    {
        await _store.UpdateAsync(Directory, DocumentName, _ => "[1]", CancellationToken.None);
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.MoveFile));

        await _store.UpdateAsync(Directory, DocumentName, _ => "[1,2]", CancellationToken.None);

        Assert.Equal("[1]", _store.ReadText(Directory, DocumentName));
        Assert.Empty(_files.EnumerateFiles(Directory, "*.tmp", recursive: false));
    }

    [Fact]
    public async Task AReadThatFailsReportsAnEmptyDocument()
    {
        await _store.UpdateAsync(Directory, DocumentName, _ => "[1]", CancellationToken.None);
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText));

        Assert.Equal(string.Empty, _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public async Task ALockHeldElsewhereIsWaitedForAndTheUpdateThenSucceeds()
    {
        _files.CreateDirectory(Directory);
        Stream held = _files.OpenExclusive(LockPath);

        await RunUpdateAdvancingTimeAsync(afterParks: 3, release: held.Dispose, mutate: _ => "[1]");

        Assert.Equal("[1]", _store.ReadText(Directory, DocumentName));
    }

    [Fact]
    public async Task ALockThatStaysHeldPastTheWaitWindowGivesUpWithoutWriting()
    {
        _files.CreateDirectory(Directory);
        using Stream held = _files.OpenExclusive(LockPath);
        bool mutated = false;

        await RunUpdateAdvancingTimeAsync(afterParks: int.MaxValue, release: () => { }, mutate: _ =>
        {
            mutated = true;
            return "[1]";
        });

        Assert.False(mutated);
        Assert.False(_store.Exists(Directory, DocumentName));
    }

    [Fact]
    public async Task AFailureWhileWritingIsAbsorbed()
    {
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.CreateDirectory));

        await _store.UpdateAsync(Directory, DocumentName, _ => "[1]", CancellationToken.None);

        Assert.False(_store.Exists(Directory, DocumentName));
    }

    private Task RunUpdateAdvancingTimeAsync(int afterParks, Action release, Func<string, string?> mutate) =>
        _time.RunAdvancingOnParksAsync(
            async () =>
            {
                await _store.UpdateAsync(Directory, DocumentName, mutate, CancellationToken.None);
                return true;
            },
            TimeSpan.FromMilliseconds(50),
            afterParks,
            release);
}
