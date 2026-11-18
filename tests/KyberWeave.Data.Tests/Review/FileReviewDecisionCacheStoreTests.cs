using KyberWeave.Core.Review;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Review;

/// <summary>The decision cache keeps answers in an in-memory file system and survives an unreadable file.</summary>
public sealed class FileReviewDecisionCacheStoreTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(new InMemoryFileSystem().Root, "cache"));
    private static readonly string CachePath = Path.Combine(Root, "decisions.json");

    private readonly InMemoryFileSystem _files = new();
    private readonly FileReviewDecisionCacheStore _store;

    public FileReviewDecisionCacheStoreTests() =>
        _store = new FileReviewDecisionCacheStore(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: Root));

    [Fact]
    public void SavedAnswersLoadBack()
    {
        Dictionary<string, ReviewCachedAnswer> answers = new() { ["q1"] = new ReviewCachedAnswer("yes", 0.9, 0.5) };

        Assert.True(_store.Save(CachePath, answers));

        ReviewCachedAnswer loaded = Assert.Single(_store.Load(CachePath)).Value;
        Assert.Equal(new ReviewCachedAnswer("yes", 0.9, 0.5), loaded);
    }

    [Fact]
    public void ACacheThatWasNeverSavedLoadsEmpty() =>
        Assert.Empty(_store.Load(CachePath));

    [Fact]
    public void ACacheThatCannotBeParsedLoadsEmpty()
    {
        _files.CreateDirectory(Root);
        _files.WriteAllText(CachePath, "{ broken");

        Assert.Empty(_store.Load(CachePath));
    }

    [Fact]
    public void ASavePathSpelledWithATrailingSeparatorCreatesThatDirectoryAndFails()
    {
        string asDirectory = Path.Combine(Root, "folder") + Path.DirectorySeparatorChar;

        Assert.False(_store.Save(asDirectory, new Dictionary<string, ReviewCachedAnswer>()));
        Assert.True(_files.DirectoryExists(Path.Combine(Root, "folder")));
    }

    [Fact]
    public void ASavePathSpelledWithoutATrailingSeparatorCreatesOnlyItsParent()
    {
        string asFile = Path.Combine(Root, "folder");

        Assert.True(_store.Save(asFile, new Dictionary<string, ReviewCachedAnswer>()));
        Assert.True(_files.FileExists(asFile));
    }

    [Fact]
    public void ASaveThatFailsReportsFalse()
    {
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.WriteAllText));

        Assert.False(_store.Save(CachePath, new Dictionary<string, ReviewCachedAnswer>()));
    }
}
