using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Finds project-level skills that hide an installed squad skill.</summary>
public interface ISquadSkillShadowing
{
    /// <summary>The project skills that shadow an installed squad skill.</summary>
    /// <param name="projectRoot">The repository root.</param>
    /// <param name="installed">The installed squad skills, project and global.</param>
    /// <param name="managedPaths">Absolute paths the deployment owns, which are never reported.</param>
    /// <returns>The shadowing project skills.</returns>
    IReadOnlyList<SquadSkillShadow> Find(
        string projectRoot,
        IReadOnlyList<SquadInstalledSkill> installed,
        IReadOnlySet<string> managedPaths);
}
