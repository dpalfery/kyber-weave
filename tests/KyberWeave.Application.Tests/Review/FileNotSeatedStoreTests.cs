using KyberWeave.Core.Review;
using KyberWeave.Data.Review;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The not-seated store over the in-memory file system: the record file, its merge and the cycle listing are proven
/// without a disk.
/// </summary>
public sealed class FileNotSeatedStoreTests
{
    private static readonly string Reviews = Path.GetFullPath("/repo/reviews");

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private FileNotSeatedStore Store() => new(_files);

    private string Cycle(int number) => Path.Combine(Reviews, $"cycle-{number}");

    private string RecordPath(string cycleFolder) => Path.Combine(cycleFolder, NotSeatedRecord.FileName);

    [Fact]
    public void AddCreatesTheCycleFolderAndWritesTheRecord()
    {
        Store().Add(Cycle(1), new Dictionary<string, string> { ["security"] = "no files match" });

        Assert.True(_files.DirectoryExists(Cycle(1)));
        Assert.Equal(
            new Dictionary<string, string> { ["security"] = "no files match" },
            Store().Read(Cycle(1)));
    }

    [Fact]
    public void AddKeepsEarlierEntriesAndOverwritesTheSameLens()
    {
        Store().Add(Cycle(1), new Dictionary<string, string> { ["a"] = "one", ["b"] = "two" });

        Store().Add(Cycle(1), new Dictionary<string, string> { ["b"] = "changed", ["c"] = "three" });

        Assert.Equal(
            new Dictionary<string, string> { ["a"] = "one", ["b"] = "changed", ["c"] = "three" },
            Store().Read(Cycle(1)));
    }

    [Fact]
    public void ReplaceDropsAnOwnedLensThatIsNoLongerDecided()
    {
        Store().Add(Cycle(1), new Dictionary<string, string> { ["a"] = "one", ["b"] = "two" });

        Store().Replace(Cycle(1), ["a"], new Dictionary<string, string>());

        Assert.Equal(new Dictionary<string, string> { ["b"] = "two" }, Store().Read(Cycle(1)));
    }

    [Fact]
    public void ReadOfAFolderWithoutARecordIsEmpty()
    {
        _files.CreateDirectory(Cycle(1));

        Assert.Empty(Store().Read(Cycle(1)));
    }

    [Fact]
    public void ReadOfARecordThatIsNotJsonIsEmpty()
    {
        _files.CreateDirectory(Cycle(1));
        _files.WriteAllText(RecordPath(Cycle(1)), "{ not json");

        Assert.Empty(Store().Read(Cycle(1)));
    }

    [Fact]
    public void ReadOfARecordTheFileSystemCannotReadIsEmpty()
    {
        _files.CreateDirectory(Cycle(1));
        _files.WriteAllText(RecordPath(Cycle(1)), "{}");
        _files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText),
            new UnauthorizedAccessException("denied"));

        Assert.Empty(Store().Read(Cycle(1)));
    }

    [Fact]
    public void LatestCycleOfAMissingReviewFolderIsNull()
    {
        Assert.Null(Store().LatestCycle(Reviews));
    }

    [Fact]
    public void LatestCycleIsTheHighestNumberedFolderThatHasSeats()
    {
        _files.CreateDirectory(Path.Combine(Cycle(1), "seats"));
        _files.CreateDirectory(Path.Combine(Cycle(2), "seats"));
        _files.CreateDirectory(Path.Combine(Cycle(10), "seats"));
        _files.CreateDirectory(Cycle(11));
        _files.CreateDirectory(Path.Combine(Reviews, "cycle-x", "seats"));
        _files.CreateDirectory(Path.Combine(Reviews, "notes", "seats"));

        Assert.Equal(Cycle(10), Store().LatestCycle(Reviews));
    }

    [Fact]
    public void LatestCycleIsNullWhenNoFolderHasSeats()
    {
        _files.CreateDirectory(Cycle(1));

        Assert.Null(Store().LatestCycle(Reviews));
    }
}
