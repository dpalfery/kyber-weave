using KyberWeave.Core.Docs.Analysis;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Opens a repository's documentation analysis cache and says whether keeping one is safe.</summary>
public interface IAnalysisPersistenceFactory
{
    /// <summary>
    /// Whether the repository's state ignore file keeps the cache out of commits and nothing of it is tracked,
    /// checked without blocking a thread.
    /// </summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="cancellationToken">Cancels the git read the check needs.</param>
    /// <returns>True only when the cache is protected.</returns>
    Task<bool> IsCacheSafeAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>Opens the cache under <paramref name="repositoryRoot"/> without blocking a thread.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="cancellationToken">Cancels the safety check the open needs.</param>
    /// <returns>A persistence whose <see cref="IAnalysisPersistence.IsAvailable"/> is false when the cache cannot be used.</returns>
    Task<IAnalysisPersistence> OpenAsync(string repositoryRoot, CancellationToken cancellationToken = default);
}
