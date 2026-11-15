namespace KyberWeave.Core.CodeGraph;

/// <summary>
/// An index that can say whether it is older than the code it describes.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="ICodeGraphResolver"/> so the many resolvers that only answer lookups
/// (fakes, snapshots) need not invent an answer: a resolver that does not implement this is simply
/// taken as current.
/// </remarks>
public interface ICodeGraphIndexFreshness
{
    /// <summary>
    /// One line saying how the index lags the checkout, or null when it is current or cannot be judged.
    /// </summary>
    string? DescribeStaleness();
}
