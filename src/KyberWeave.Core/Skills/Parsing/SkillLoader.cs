using KyberWeave.Core.Skills.Model;

namespace KyberWeave.Core.Skills.Parsing;

/// <summary>The outcome of attempting to load one skill directory.</summary>
public sealed record SkillLoadResult(string Path, Skill? Skill, string? Error)
{
    public bool Success => Skill is not null;
}

/// <summary>
/// Discovers and parses skills from disk. A "skill" is any directory containing a
/// SKILL.md. Discovery walks the tree so a single root can hold many skills, matching
/// the way runtimes enumerate a skills folder.
/// </summary>
public static class SkillLoader
{
    /// <summary>
    /// Load all skills under <paramref name="rootPath"/>. If the path is itself a
    /// SKILL.md or a directory containing one, that single skill is loaded.
    /// </summary>
    public static IReadOnlyList<SkillLoadResult> Load(string rootPath)
    {
        var results = new List<SkillLoadResult>();

        if (File.Exists(rootPath) && Path.GetFileName(rootPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(LoadOne(rootPath));
            return results;
        }

        if (!Directory.Exists(rootPath))
        {
            results.Add(new SkillLoadResult(rootPath, null, $"Path not found: {rootPath}"));
            return results;
        }

        var skillFiles = Directory
            .EnumerateFiles(rootPath, "SKILL.md", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (skillFiles.Count == 0)
        {
            results.Add(new SkillLoadResult(rootPath, null, $"No SKILL.md files found under: {rootPath}"));
            return results;
        }

        foreach (var file in skillFiles)
            results.Add(LoadOne(file));

        return results;
    }

    /// <summary>Load only the skills that parsed successfully into a <see cref="SkillSet"/>.</summary>
    public static SkillSet LoadSet(string rootPath) =>
        new(Load(rootPath).Where(r => r.Success).Select(r => r.Skill!));

    private static SkillLoadResult LoadOne(string skillFilePath)
    {
        try
        {
            var skill = SkillParser.ParseFile(skillFilePath);
            return new SkillLoadResult(skillFilePath, skill, null);
        }
        catch (SkillParseException ex)
        {
            return new SkillLoadResult(skillFilePath, null, ex.Message);
        }
    }
}
