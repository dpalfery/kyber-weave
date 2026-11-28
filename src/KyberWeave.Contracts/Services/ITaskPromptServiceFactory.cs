using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// Builds the task prompt service for one checkout, so a caller that is not the docs tool or the packet command can
/// expand a dispatch through the same service those two use.
/// </summary>
/// <remarks>
/// The service reads the checkout's documentation when it answers, so it is built for a root and a configuration
/// rather than registered as one singleton. The dispatch hook asks for it once per process, on the first dispatch
/// that names a task.
/// </remarks>
public interface ITaskPromptServiceFactory
{
    /// <summary>Builds the service over <paramref name="repositoryRoot"/>'s documentation.</summary>
    /// <param name="repositoryRoot">The checkout whose rules, skills and standards the prompt carries.</param>
    /// <param name="config">The effective host configuration, which places the docs root and holds the budget.</param>
    ITaskPromptService ForRepository(string repositoryRoot, KyberWeaveConfig config);

    /// <summary>
    /// Builds the service over <paramref name="repositoryRoot"/>'s documentation, opening the CodeGraph ports without
    /// blocking a thread.
    /// </summary>
    /// <param name="repositoryRoot">The checkout whose rules, skills and standards the prompt carries.</param>
    /// <param name="config">The effective host configuration, which places the docs root and holds the budget.</param>
    /// <param name="cancellationToken">Cancels the index read.</param>
    /// <returns>The service over that checkout.</returns>
    Task<ITaskPromptService> ForRepositoryAsync(
        string repositoryRoot,
        KyberWeaveConfig config,
        CancellationToken cancellationToken = default);
}
