namespace KyberWeave.Core.Skills.Model;

/// <summary>
/// A collection of skills discovered from a directory tree. Enables cross-skill
/// analysis such as name-collision and description-overlap detection.
/// </summary>
public sealed class SkillSet(IEnumerable<Skill> skills)
{
    private readonly List<Skill> _skills = skills.ToList();

    public IReadOnlyList<Skill> Skills => _skills;

    public int Count => _skills.Count;
}
