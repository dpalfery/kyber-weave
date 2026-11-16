using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Contracts.Services;

/// <summary>Builds the git facts one hook evaluation enriches its fact bag with.</summary>
public interface IHookGitFactsFactory
{
    /// <summary>Creates the facts for one evaluation.</summary>
    /// <param name="workingDirectory">The worktree that holds the plan, which the git facts are read in.</param>
    /// <param name="homeDirectory">The operator's home, which holds the run records, or null for the user profile.</param>
    /// <param name="arbiterDirectory">The arbiter state directory the snapshots are recorded in, or null for <c>artifacts/arbiter</c> under the working directory.</param>
    /// <returns>The facts.</returns>
    IArbiterGitFacts Create(string workingDirectory, string? homeDirectory = null, string? arbiterDirectory = null);
}
