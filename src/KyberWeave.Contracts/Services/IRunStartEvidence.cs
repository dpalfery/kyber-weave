namespace KyberWeave.Contracts.Services;

/// <summary>
/// Whether a run has been started before an orchestrator hands a specialist work that writes files. Read by
/// <c>KW-ARB-RUN-001</c> and by the Copilot conductor guard, from machine facts only: the record <c>run start</c>
/// wrote and git's answer for the repository and the run's worktree.
/// </summary>
public interface IRunStartEvidence
{
    /// <summary>Whether a run is live for the repository <paramref name="workingDirectory" /> belongs to, without blocking a thread while git runs.</summary>
    /// <param name="workingDirectory">The tool call's working directory.</param>
    /// <param name="homeDirectory">The operator's home, which holds <c>.kyber-weave/runs</c>.</param>
    /// <param name="cancellationToken">Cancels the git child.</param>
    /// <returns>True or false from the records; null when git cannot name the repository or the records cannot be read.</returns>
    Task<bool?> StartedAsync(string workingDirectory, string homeDirectory, CancellationToken cancellationToken = default);
}
