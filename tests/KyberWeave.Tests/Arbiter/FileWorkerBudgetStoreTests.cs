using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The worker budget files, kept in an in-memory file system and swept against a controlled clock.</summary>
public sealed class FileWorkerBudgetStoreTests
{
    private static readonly string Home = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "home"));

    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryFileSystem _files;
    private readonly FileWorkerBudgetStore _store;

    public FileWorkerBudgetStoreTests()
    {
        _files = new InMemoryFileSystem(_time);
        _store = new FileWorkerBudgetStore(_files);
        _store.EnsureDirectory(Home);
    }

    [Fact]
    public void ABudgetThatWasNeverCreatedDoesNotExistAndReadsEmpty()
    {
        Assert.False(_store.Exists(Home, "worker"));
        Assert.Empty(_store.ReadLines(Home, "worker"));
    }

    [Fact]
    public void ACreatedBudgetExistsAndReadsItsHeader()
    {
        _store.TryCreate(Home, "worker", "header\n");

        Assert.True(_store.Exists(Home, "worker"));
        Assert.Equal(["header"], _store.ReadLines(Home, "worker"));
    }

    [Fact]
    public void ASecondCreateKeepsTheFirstHeader()
    {
        _store.TryCreate(Home, "worker", "first\n");

        _store.TryCreate(Home, "worker", "second\n");

        Assert.Equal(["first"], _store.ReadLines(Home, "worker"));
    }

    [Fact]
    public void AppendsFollowTheHeaderInOrder()
    {
        _store.TryCreate(Home, "worker", "header\n");

        _store.Append(Home, "worker", "call 1\n");
        _store.Append(Home, "worker", "call 2\n");

        Assert.Equal(["header", "call 1", "call 2"], _store.ReadLines(Home, "worker"));
    }

    [Fact]
    public void AnAppendToAMissingBudgetStartsIt()
    {
        _store.Append(Home, "worker", "call 1\n");

        Assert.Equal(["call 1"], _store.ReadLines(Home, "worker"));
    }

    [Fact]
    public void DeletingRemovesOnlyThatBudgetAndAMissingOneIsNotAnError()
    {
        _store.TryCreate(Home, "gone", "a\n");
        _store.TryCreate(Home, "kept", "b\n");

        _store.Delete(Home, "gone");
        _store.Delete(Home, "never-existed");

        Assert.False(_store.Exists(Home, "gone"));
        Assert.True(_store.Exists(Home, "kept"));
    }

    [Fact]
    public void ACreateFailureThatIsNotARaceIsReported()
    {
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.TryCreateNewFile), new UnauthorizedAccessException());

        Assert.Throws<UnauthorizedAccessException>(() => _store.TryCreate(Home, "worker", "header\n"));
    }

    [Fact]
    public void SweepingRemovesBudgetsOlderThanTheRetentionAndKeepsTheRest()
    {
        _store.TryCreate(Home, "old", "a\n");
        _time.Advance(TimeSpan.FromDays(3));
        _store.TryCreate(Home, "recent", "b\n");

        _store.Sweep(Home, _time.GetUtcNow(), TimeSpan.FromDays(2));

        Assert.False(_store.Exists(Home, "old"));
        Assert.True(_store.Exists(Home, "recent"));
    }

    [Fact]
    public void SweepingKeepsEverythingWithinTheRetention()
    {
        _store.TryCreate(Home, "a", "a\n");
        _time.Advance(TimeSpan.FromHours(1));

        _store.Sweep(Home, _time.GetUtcNow(), TimeSpan.FromDays(2));

        Assert.True(_store.Exists(Home, "a"));
    }

    [Fact]
    public void ASweepThatCannotDeleteOneBudgetStillSweepsTheOthers()
    {
        _store.TryCreate(Home, "stuck", "a\n");
        _store.TryCreate(Home, "old", "b\n");
        _time.Advance(TimeSpan.FromDays(3));
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.DeleteFile) && operation.Path!.Contains("stuck", StringComparison.Ordinal));

        _store.Sweep(Home, _time.GetUtcNow(), TimeSpan.FromDays(2));

        Assert.True(_store.Exists(Home, "stuck"));
        Assert.False(_store.Exists(Home, "old"));
    }
}
