using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IReviewDecisionCacheStore"/>: keeps each path's answers in memory, so a test needs no cache file.</summary>
internal sealed class FakeReviewDecisionCacheStore : IReviewDecisionCacheStore
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, ReviewCachedAnswer>> _files = new(StringComparer.Ordinal);

    /// <summary>How many times a cache file was read.</summary>
    public int Loads { get; private set; }

    /// <summary>How many times a cache file was written.</summary>
    public int Saves { get; private set; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, ReviewCachedAnswer> Load(string path)
    {
        Loads++;
        return _files.TryGetValue(path, out IReadOnlyDictionary<string, ReviewCachedAnswer>? answers)
            ? answers
            : new Dictionary<string, ReviewCachedAnswer>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool Save(string path, IReadOnlyDictionary<string, ReviewCachedAnswer> answers)
    {
        Saves++;
        _files[path] = new Dictionary<string, ReviewCachedAnswer>(answers, StringComparer.Ordinal);
        return true;
    }
}
