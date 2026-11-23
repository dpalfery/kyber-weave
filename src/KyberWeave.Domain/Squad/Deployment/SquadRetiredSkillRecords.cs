namespace KyberWeave.Core.Squad.Deployment;

/// <summary>A retired Squad skill and where its content lives now.</summary>
/// <param name="Name">The retired skill's directory name.</param>
/// <param name="NewHome">One sentence naming where the skill's content moved and how to adopt it.</param>
public sealed record SquadRetiredSkill(string Name, string NewHome);

/// <summary>A receipt entry that still claims a retired skill's files.</summary>
/// <param name="Skill">The retired skill whose directory the path names.</param>
/// <param name="RelativePath">The receipt-relative path of the claimed file.</param>
public sealed record SquadRetiredSkillClaim(SquadRetiredSkill Skill, string RelativePath);
