using KyberWeave.Core.Skills.Model;

namespace KyberWeave.Core.Skills.Parsing;

/// <summary>The outcome of attempting to load one skill directory.</summary>
/// <param name="Path">The path that was loaded.</param>
/// <param name="Skill">The parsed skill, or null when it could not be read or parsed.</param>
/// <param name="Error">Why the skill could not be loaded, or null when it could.</param>
public sealed record SkillLoadResult(string Path, Skill? Skill, string? Error)
{
    /// <summary>Gets whether the skill parsed.</summary>
    public bool Success => Skill is not null;
}
