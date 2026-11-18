using KyberWeave.Core.Review;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Data.Review;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The decision-model answer file a review cycle reuses: the answers survive a process restart, a file written by an
/// earlier version still loads, and a file that cannot be read or written only costs the questions again.
/// </summary>
/// <remarks>
/// The unit tests ask the model through an in-memory cache store, which cannot see the JSON the product writes. These
/// run the real store on disk, so a change to the serialized shape shows up here rather than as a cache that silently
/// misses on every finding and re-asks the provider.
/// </remarks>
public sealed class ReviewDecisionCacheStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string CachePath => Path.Combine(_temp.Path, "review", "decision-model.json");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AnAnswerSavedInOneRunIsReadBackWithEveryField()
    {
        FileReviewDecisionCacheStore store = NewStore();
        Dictionary<string, ReviewCachedAnswer> answers = new(StringComparer.Ordinal)
        {
            ["rule|subject|digest"] = new ReviewCachedAnswer("belongs", 0.95, 0.8),
            ["rule|other|digest"] = new ReviewCachedAnswer("no", null, null),
        };

        Assert.True(store.Save(CachePath, answers));

        IReadOnlyDictionary<string, ReviewCachedAnswer> read = store.Load(CachePath);
        Assert.Equal(2, read.Count);
        Assert.Equal(new ReviewCachedAnswer("belongs", 0.95, 0.8), read["rule|subject|digest"]);
        Assert.Equal(new ReviewCachedAnswer("no", null, null), read["rule|other|digest"]);
    }

    [Fact]
    public void AnAnswerFileWrittenByAnEarlierRunStillLoads()
    {
        // The shape as it is on disk today. A serializer or property-name change breaks this test instead of making
        // every existing decision-model.json unreadable in the field.
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(
            CachePath,
            """{"rule|subject|digest":{"Answer":"belongs","Score":0.95,"Threshold":0.8}}""");

        IReadOnlyDictionary<string, ReviewCachedAnswer> read = NewStore().Load(CachePath);

        Assert.Equal(new ReviewCachedAnswer("belongs", 0.95, 0.8), Assert.Single(read).Value);
    }

    [Fact]
    public void ACorruptAnswerFileCostsTheQuestionsAgainRatherThanFailingTheRun()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, "{ not json");

        Assert.Empty(NewStore().Load(CachePath));
    }

    [Fact]
    public void AnUnwritableAnswerFileIsReportedAsNotSaved()
    {
        Directory.CreateDirectory(CachePath);

        Assert.False(NewStore().Save(CachePath, new Dictionary<string, ReviewCachedAnswer>()));
    }

    private static FileReviewDecisionCacheStore NewStore()
    {
        PhysicalFileSystem files = new();
        return new FileReviewDecisionCacheStore(files, new PhysicalPathResolver(files), new ProcessEnvironment());
    }
}
