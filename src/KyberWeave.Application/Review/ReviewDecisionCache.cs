using System.Diagnostics.CodeAnalysis;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>
/// The decision-model answers one review cycle already got, keyed by rule, subject and a digest of the text asked
/// about, so <c>review verdict --candidates</c> and the verdict run that follows it give the same answer for one call.
/// </summary>
/// <remarks>A changed finding text changes the digest, so a stale answer is never reused.</remarks>
internal sealed class ReviewDecisionCache
{
    private readonly IReviewDecisionCacheStore _store;
    private readonly string _path;
    private readonly Dictionary<string, ReviewCachedAnswer> _entries;
    private bool _dirty;

    private ReviewDecisionCache(IReviewDecisionCacheStore store, string path, Dictionary<string, ReviewCachedAnswer> entries)
    {
        _store = store;
        _path = path;
        _entries = entries;
    }

    /// <summary>Reads the cache file; a missing or unreadable file starts an empty cache.</summary>
    public static ReviewDecisionCache Load(IReviewDecisionCacheStore store, string path) =>
        new(store, path, new Dictionary<string, ReviewCachedAnswer>(store.Load(path), StringComparer.Ordinal));

    public bool TryGet(ArbiterRule rule, ReviewQuestion question, [NotNullWhen(true)] out ReviewAnswer? answer)
    {
        answer = _entries.TryGetValue(ReviewDecisionCacheKey.Of(rule, question), out ReviewCachedAnswer? hit)
            ? new ReviewAnswer(hit.Answer, hit.Score, hit.Threshold)
            : null;
        return answer is not null;
    }

    public void Put(ArbiterRule rule, ReviewQuestion question, ReviewAnswer answer)
    {
        _entries[ReviewDecisionCacheKey.Of(rule, question)] = new ReviewCachedAnswer(answer.Answer, answer.Score, answer.Threshold);
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty)
            return;

        if (_store.Save(_path, _entries))
            _dirty = false;
    }
}
