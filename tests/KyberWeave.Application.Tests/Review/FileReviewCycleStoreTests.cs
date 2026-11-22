using System.IO;
using KyberWeave.Core.Review;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
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

    private FileReviewCycleStore Store(string currentDirectory = "/") =>
        new(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: currentDirectory));

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
    public void ReadFinishedReadsARelativeFolderAgainstTheWorkingDirectory()
    {
        string folder = Path.Combine("/work", "repo", "review", "cycle-1");
        SeedFile(folder, ReviewCyclePlanning.FindingsFile, "{}");

        ReviewCycleRecord cycle = Assert.Single(Store(currentDirectory: "/work").ReadFinished("repo/review"));

        Assert.Equal(1, cycle.Number);
    }

    [Fact]
    public void HasIdentityReadsARelativeFolderAgainstTheWorkingDirectory()
    {
        SeedFile(
            Path.Combine("/work", "repo", "review", "cycle-1"),
            ReviewCyclePlanning.IdentityFile,
            "{}");

        Assert.True(Store(currentDirectory: "/work").HasIdentity("repo/review/cycle-1"));
    }

    [Fact]
    public void WriteIdentityResolvesARelativeFolderAgainstTheWorkingDirectory()
    {
        string relative = Path.Combine("repo", "review", "cycle-1");
        ReviewCycleIdentity identity = new("feature/x", "0123456789abcdef0123456789abcdef01234567", "abc123");

        Store(currentDirectory: "/work").WriteIdentity(relative, identity);

        Assert.True(_files.FileExists(
            Path.Combine("/work", "repo", "review", "cycle-1", ReviewCyclePlanning.IdentityFile)));
    }

    [Fact]
    public void TaskFoldersReadsARelativeScratchpadAgainstTheWorkingDirectory()
    {
        _files.CreateDirectory(Path.Combine("/work", "repo", "scratchpad", "task-a", "review"));

        Assert.Equal(
            ["task-a"],
            Store(currentDirectory: "/work").TaskFolders(Path.Combine("repo", "scratchpad")));
    }

    [Fact]
    public void ReadThroughReadsARelativeFolderAgainstTheWorkingDirectory()
    {
        string folder = Path.Combine("/repo", "review", "cycle-2");
        SeedFile(folder, ReviewCyclePlanning.FindingsFile, "{}");

        ReviewCycleRecord cycle = Assert.Single(Store().ReadThrough("repo/review/cycle-2"));

        Assert.Equal(2, cycle.Number);
    }

    [Fact]
    public void ReadCycleReadsOneFolderWithoutReadingTheOthers()
    {
        SeedFile(Path.Combine(Review, "cycle-1"), ReviewCyclePlanning.FindingsFile, "[]");
        SeedFile(Path.Combine(Review, "cycle-2"), ReviewCyclePlanning.FindingsFile, "[]");

        ReviewCycleRecord cycle = Store().ReadCycle(Path.Combine(Review, "cycle-2"))!;

        Assert.Equal(2, cycle.Number);
        Assert.Equal(Path.Combine(Review, "cycle-2"), cycle.Folder);
    }

    [Fact]
    public void ReadCycleReturnsNullForAFolderThatIsNotANumberedCycle()
    {
        SeedFile(Path.Combine(Review, "not-a-cycle"), ReviewCyclePlanning.FindingsFile, "[]");

        Assert.Null(Store().ReadCycle(Path.Combine(Review, "not-a-cycle")));
    }

    /// <summary>
    /// A cycle whose head cannot be read still fails only its own read, so a caller that reads the cycles one at a
    /// time keeps the others instead of losing the whole history.
    /// </summary>
    [Fact]
    public void ReadCycleThrowsOnlyForTheCycleWhoseHeadCannotBeRead()
    {
        string head = Path.Combine(Review, "cycle-1", ReviewCyclePlanning.HeadFile);
        SeedFile(Path.Combine(Review, "cycle-1"), ReviewCyclePlanning.FindingsFile, "[]");
        SeedFile(Path.Combine(Review, "cycle-1"), ReviewCyclePlanning.HeadFile, "abc");
        SeedFile(Path.Combine(Review, "cycle-2"), ReviewCyclePlanning.FindingsFile, "[]");
        _files.FailWhen(operation => operation.Name == "ReadAllText" && operation.Path == head, new IOException("unreadable"));

        FileReviewCycleStore store = Store();

        _ = Assert.Throws<IOException>(() => store.ReadCycle(Path.Combine(Review, "cycle-1")));
        Assert.Equal(2, store.ReadCycle(Path.Combine(Review, "cycle-2"))!.Number);
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
