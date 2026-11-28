using KyberWeave.Core.CodeGraph;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Opens the CodeGraph ports over the index of one checkout.</summary>
public interface ICodeGraphPortsFactory
{
    /// <summary>Builds the ports over the index under <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The checkout whose <c>.codegraph/codegraph.db</c> is read.</param>
    /// <returns>
    /// One object holding every port the loaded adapter answers, so a caller takes the ports it needs from a single
    /// index read instead of naming the adapter class.
    /// </returns>
    CodeGraphPorts ForRepository(string repositoryRoot);

    /// <summary>Builds the ports over the index under <paramref name="repositoryRoot"/> without blocking a thread.</summary>
    /// <param name="repositoryRoot">The checkout whose <c>.codegraph/codegraph.db</c> is read.</param>
    /// <param name="cancellationToken">Cancels the index read.</param>
    /// <returns>
    /// One object holding every port the loaded adapter answers, so a caller takes the ports it needs from a single
    /// index read instead of naming the adapter class.
    /// </returns>
    Task<CodeGraphPorts> ForRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default);
}
