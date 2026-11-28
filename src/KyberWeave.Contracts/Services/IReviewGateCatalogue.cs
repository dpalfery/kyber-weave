using KyberWeave.Core.Docs.Scaffolding;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// Renders the catalogue <c>review:</c> block a repository's declared technologies would get,
/// without writing it.
/// </summary>
public interface IReviewGateCatalogue
{
    /// <summary>
    /// Renders the catalogue for <paramref name="declaredTechnologies"/> without blocking a thread, detecting each
    /// family's project roots under <paramref name="repoRoot"/> by that family's own rules.
    /// </summary>
    /// <param name="repoRoot">The repository whose project roots are detected.</param>
    /// <param name="declaredTechnologies">The technology slugs the repository declares.</param>
    /// <param name="cancellationToken">Cancels the repository file listing.</param>
    /// <returns>The rendered block and whether there is anything to write.</returns>
    Task<ReviewGatesPlan> PlanAsync(
        string repoRoot,
        IReadOnlyList<string> declaredTechnologies,
        CancellationToken cancellationToken = default);
}
