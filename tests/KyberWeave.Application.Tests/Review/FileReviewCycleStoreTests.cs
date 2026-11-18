using System.IO;
using KyberWeave.Core.Review;
using KyberWeave.Data.Review;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The review cycle folders are read and written through the file seam: every cycle lives in memory, so the numbering,
/// the findings rule, the identity round trip and the unreadable-file rules are proven without a disk.
/// </summary>
public sealed class FileReviewCycleStoreTests
{
    private const string Review = "/repo/review";
    private const string Scratchpad = "/repo/scratchpad";

    private readonly InMemoryFileSystem _files = new();

    private FileReviewCycleStore Store() => new(_files);

    private void SeedFile(string folder, string name, string text)
    {
        _files.CreateDirectory(folder);
        _files.WriteAllText(Path.Combine(folder, name), text);
    }

    [Fact]
    public void ReadFinishedReturnsOnlyTheCyclesWithFindingsInNumberOrder()
    {
        SeedFile(Path.Combine(Review, "cycle-3"), ReviewCyclePlanning.FindingsFile, "[]");
        SeedFile(Path.Combine(Review, "cycle-1"), ReviewCyclePlanning.FindingsFile, "[]");
        SeedFile(Path.Combine(Review, "cycle-2"), ReviewCyclePlanning.HeadFile, "abc");

        IReadOnlyList<ReviewCycleRecord> cycles = Store().ReadFinished(Review);

        Assert.Equal([1, 3], cycles.Select(cycle => cycle.Number));
    }

    [Fact]
    public void ReadFinishedIsEmptyWhenTheFolderIsMissing()
    {
        Assert.Empty(Store().ReadFinished(Review));
    }

    [Fact]
    public void AnIdentityWrittenIsReadBackAsTheSameIdentity()
    {
        string folder = Path.Combine(Review, "cycle-1");
        ReviewCycleIdentity identity = new("feature/x", "0123456789abcdef0123456789abcdef01234567", "abc123");

        Store().WriteIdentity(folder, identity);

        Assert.True(Store().HasIdentity(folder));
        Assert.Equal(identity, Store().ReadRecordedIdentity(folder));
    }

    [Fact]
    public void HasIdentityIsFalseBeforeAnIdentityIsWritten()
    {
        Assert.False(Store().HasIdentity(Path.Combine(Review, "cycle-1")));
    }

    [Fact]
    public void TaskFoldersListsOnlyTheTasksThatHaveAReviewFolder()
    {
        _files.CreateDirectory(Path.Combine(Scratchpad, "alpha", "review"));
        _files.CreateDirectory(Path.Combine(Scratchpad, "beta"));

        Assert.Equal(["alpha"], Store().TaskFolders(Scratchpad));
    }

    [Fact]
    public void ReadFinishedFailsWhenTheHeadOfACycleCannotBeRead()
    {
        string head = Path.Combine(Review, "cycle-1", ReviewCyclePlanning.HeadFile);
        SeedFile(Path.Combine(Review, "cycle-1"), ReviewCyclePlanning.FindingsFile, "[]");
        SeedFile(Path.Combine(Review, "cycle-1"), ReviewCyclePlanning.HeadFile, "abc");
        _files.FailWhen(operation => operation.Name == "ReadAllText" && operation.Path == head, new IOException("unreadable"));

        _ = Assert.Throws<IOException>(() => Store().ReadFinished(Review));
    }

    [Fact]
    public void AnIdentityThatCannotBeReadIsReportedAsAbsent()
    {
        string folder = Path.Combine(Review, "cycle-1");
        string identity = Path.Combine(folder, ReviewCyclePlanning.IdentityFile);
        SeedFile(folder, ReviewCyclePlanning.IdentityFile, "{}");
        _files.FailWhen(operation => operation.Name == "ReadAllText" && operation.Path == identity, new IOException("unreadable"));

        Assert.Null(Store().ReadRecordedIdentity(folder));
    }
}
