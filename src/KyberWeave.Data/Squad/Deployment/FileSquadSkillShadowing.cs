using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Finds shadowing project skills on disk through <see cref="SquadSkillShadowing"/>.</summary>
public sealed class FileSquadSkillShadowing : ISquadSkillShadowing
{
    /// <inheritdoc />
    public IReadOnlyList<SquadSkillShadow> Find(
        string projectRoot,
        IReadOnlyList<SquadInstalledSkill> installed,
        IReadOnlySet<string> managedPaths) =>
        SquadSkillShadowing.Find(projectRoot, installed, managedPaths);
}
