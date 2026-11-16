using KyberWeave.Domain.Doctor;

namespace KyberWeave.Contracts.Repositories;

/// <summary>The repository settings the doctor reads from <c>.kyber-weave/kyber-weave.yml</c>.</summary>
public interface IRepositoryToolSettings
{
    /// <summary>Reads the tool settings of a repository.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>
    /// The settings. A configuration that cannot be loaded leaves the defaults in place: stop on tool failure, and
    /// index a new worktree, because a broken configuration must not be the thing that weakens either rule.
    /// </returns>
    RepositoryToolSettings Read(string repositoryRoot);
}
