using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>Where each target installs a skill, projected from the renderers' own layout.</summary>
public static class SquadSkillOutputPaths
{
    /// <summary>
    /// Every project-relative folder a renderer writes skills into, such as <c>.claude/skills</c>,
    /// in target order. The harnesses read these folders themselves, so a skill of the same name
    /// placed in any of them can be the one an agent loads.
    /// </summary>
    public static IReadOnlyList<string> ProjectSkillRoots()
    {
        return
        [
            .. SquadTargetCatalog.All
                .Select(ProjectSkillsRoot)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// The directory <paramref name="target"/> installs project-scope skills to, or null when the
    /// target has no skill directory.
    /// </summary>
    public static string? ProjectSkillsRoot(SquadTarget target)
    {
        const string suffix = "/x/SKILL.md";
        HashSet<string> none = new(StringComparer.Ordinal);
        return SkillOutputPath(target, "x", none) is { } path ? path[..^suffix.Length] : null;
    }

    public static string? SkillOutputPath(
        SquadTarget target,
        string name,
        IReadOnlySet<string> suppressedSkillIdentities)
    {
        ArgumentNullException.ThrowIfNull(suppressedSkillIdentities);
        return target switch
        {
            SquadTarget.Copilot when !suppressedSkillIdentities.Contains(name) => $".github/skills/{name}/SKILL.md",
            SquadTarget.Cursor when !suppressedSkillIdentities.Contains(name) => $".cursor/skills/{name}/SKILL.md",
            SquadTarget.Claude when !suppressedSkillIdentities.Contains(name) => $".claude/skills/{name}/SKILL.md",
            SquadTarget.Codex when !suppressedSkillIdentities.Contains(name) => $".codex/skills/{name}/SKILL.md",
            SquadTarget.OpenCode when !suppressedSkillIdentities.Contains(name) => $".opencode/skills/{name}/SKILL.md",
            SquadTarget.Kilo when !suppressedSkillIdentities.Contains(name) => $".kilo/skills/{name}/SKILL.md",
            SquadTarget.Factory when !suppressedSkillIdentities.Contains(name) => $".factory/skills/{name}/SKILL.md",
            SquadTarget.Antigravity => $".agents/skills/{name}/SKILL.md",
            SquadTarget.Warp => $".warp/skills/{name}/SKILL.md",
            SquadTarget.Pi when !suppressedSkillIdentities.Contains(name) => $".pi/skills/{name}/SKILL.md",
            SquadTarget.ZCode when !suppressedSkillIdentities.Contains(name) => $".zcode/skills/{name}/SKILL.md",
            SquadTarget.Devin when !suppressedSkillIdentities.Contains(name) => $".devin/skills/{name}/SKILL.md",
            SquadTarget.CommandCode when !suppressedSkillIdentities.Contains(name) =>
                $".commandcode/skills/{name}/SKILL.md",
            _ => null
        };
    }
}
