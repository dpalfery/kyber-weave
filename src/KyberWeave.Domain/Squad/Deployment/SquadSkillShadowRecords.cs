namespace KyberWeave.Core.Squad.Deployment;

/// <summary>A squad skill, installed by a deployment, and where its <c>SKILL.md</c> is.</summary>
/// <param name="Name">The skill's directory name.</param>
/// <param name="FilePath">The absolute path of the installed <c>SKILL.md</c>.</param>
public sealed record SquadInstalledSkill(string Name, string FilePath);

/// <summary>A project skill that repeats an installed squad skill's name with different content.</summary>
/// <param name="Name">The skill's directory name.</param>
/// <param name="ProjectPath">The project's own <c>SKILL.md</c>.</param>
/// <param name="InstalledPath">An installed squad copy it differs from.</param>
public sealed record SquadSkillShadow(string Name, string ProjectPath, string InstalledPath);
