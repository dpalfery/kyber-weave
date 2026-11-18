using KyberWeave.Core.Docs.Analysis;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Opens a repository's documentation analysis cache and says whether keeping one is safe.</summary>
public interface IAnalysisPersistenceFactory
{
    /// <summary>Whether the repository's state ignore file keeps the cache out of commits and nothing of it is tracked.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>True only when the cache is protected.</returns>
    bool IsCacheSafe(string repositoryRoot);

    /// <summary>Opens the cache under <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>A persistence whose <see cref="IAnalysisPersistence.IsAvailable"/> is false when the cache cannot be used.</returns>
    IAnalysisPersistence Open(string repositoryRoot);
}
