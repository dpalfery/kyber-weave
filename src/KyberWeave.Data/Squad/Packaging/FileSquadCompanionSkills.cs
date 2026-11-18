using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>Lists the companion skills of the checkout's <c>.apm/skills</c> directory.</summary>
public sealed class FileSquadCompanionSkills : ISquadCompanionSkills
{
    /// <inheritdoc />
    public IReadOnlyList<(string RelativePath, string SourcePath)> Collect(
        string squadSourceRoot,
        string? companionSkillsRoot) =>
        SquadCompanionSkills.Collect(squadSourceRoot, companionSkillsRoot);
}
