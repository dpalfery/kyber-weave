using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Keeps the decision-model answers one review cycle already got, in a file, so a later run of the cycle reuses them.</summary>
public interface IReviewDecisionCacheStore
{
    /// <summary>Reads the answer file.</summary>
    /// <param name="path">The cycle's answer file.</param>
    /// <returns>The answers by key; empty when the file is missing or unreadable, because an unreadable cache only costs the questions again.</returns>
    IReadOnlyDictionary<string, ReviewCachedAnswer> Load(string path);

    /// <summary>Writes the answer file, replacing it.</summary>
    /// <param name="path">The cycle's answer file.</param>
    /// <param name="answers">The answers by key.</param>
    /// <returns>True when the file was written; false when it could not be, which is not an error: the next run asks again.</returns>
    bool Save(string path, IReadOnlyDictionary<string, ReviewCachedAnswer> answers);
}
