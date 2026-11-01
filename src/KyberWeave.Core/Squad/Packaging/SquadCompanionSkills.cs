using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Validation;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>
/// Adds the Kyber-Weave skills authored in <c>.apm/skills</c> to a Squad source tree, so they ship and deploy with
/// the Squad skills.
/// </summary>
/// <remarks>
/// The skills stay in <c>.apm/skills</c>, their only source. The APM package reads that folder, so moving them into
/// <c>products/kyber-squad</c> would break the package, and copying them would leave two sources to drift. Pack and
/// the checkout release source both call this type, so they cannot disagree about what a Squad tree holds.
/// </remarks>
public static class SquadCompanionSkills
{
    /// <summary>
    /// Resolves the <c>.apm/skills</c> directory of the repository that holds a Squad source root shaped
    /// <c>&lt;repo&gt;/products/kyber-squad</c>, or null when the root has no such shape or the directory is absent.
    /// </summary>
    public static string? ResolveRoot(string squadSourceRoot)
    {
        DirectoryInfo root = new(Path.GetFullPath(squadSourceRoot));
        DirectoryInfo? products = root.Parent;
        DirectoryInfo? repository = products?.Parent;
        if (root.Name != "kyber-squad" || products?.Name != "products" || repository is null)
        {
            return null;
        }

        string skillsRoot = Path.Combine(repository.FullName, ".apm", "skills");
        return Directory.Exists(skillsRoot) ? skillsRoot : null;
    }

    /// <summary>
    /// Lists every file of every companion skill, as its Squad-tree path (<c>skills/&lt;name&gt;/&lt;relative&gt;</c>,
    /// forward slashes) and its source path. A null companion root lists nothing.
    /// </summary>
    /// <remarks>
    /// A companion name that equals a Squad skill or a Squad agent throws <see cref="SquadSourceValidationException"/>.
    /// Failing closed keeps either side from silently shadowing the other: one skill would otherwise vanish from every
    /// harness with no signal. The exception carries no partial list, so no caller can report a partial tree.
    /// </remarks>
    public static IReadOnlyList<(string RelativePath, string SourcePath)> Collect(
        string squadSourceRoot,
        string? companionSkillsRoot)
    {
        List<(string RelativePath, string SourcePath)> files = [];
        if (companionSkillsRoot is null)
        {
            return files;
        }

        foreach (string skillDirectory in SkillDirectories(companionSkillsRoot))
        {
            string name = Path.GetFileName(skillDirectory);
            RejectClash(squadSourceRoot, name);

            foreach (string filePath in DocsRootPath.EnumerateContainedFiles(skillDirectory, "*"))
            {
                string relativePath = Path.GetRelativePath(skillDirectory, filePath).Replace('\\', '/');
                files.Add(($"skills/{name}/{relativePath}", filePath));
            }
        }

        return files;
    }

    /// <summary>
    /// Every direct child of the companion root that is a real directory holding a real <c>SKILL.md</c>, ordinally sorted.
    /// </summary>
    private static IEnumerable<string> SkillDirectories(string companionSkillsRoot)
    {
        return Directory.EnumerateDirectories(companionSkillsRoot)
            .Where(IsSkillDirectory)
            .Order(StringComparer.Ordinal);
    }

    /// <remarks>
    /// Symbolic links are refused at both the directory and the <c>SKILL.md</c>, as the contained-file walk refuses
    /// them, so a link cannot pull a skill in from outside the tree.
    /// </remarks>
    private static bool IsSkillDirectory(string directory)
    {
        string skillFile = Path.Combine(directory, "SKILL.md");
        return new DirectoryInfo(directory).LinkTarget is null
               && new FileInfo(skillFile).LinkTarget is null
               && File.Exists(skillFile);
    }

    private static void RejectClash(string squadSourceRoot, string name)
    {
        if (Directory.Exists(Path.Combine(squadSourceRoot, "skills", name)))
        {
            throw Clash(name, "Squad skill", $"products/kyber-squad/skills/{name}");
        }

        if (File.Exists(Path.Combine(squadSourceRoot, "agents", $"{name}.md")))
        {
            throw Clash(name, "Squad agent", $"products/kyber-squad/agents/{name}.md");
        }
    }

    private static SquadSourceValidationException Clash(string name, string kind, string squadPath)
    {
        return new SquadSourceValidationException(
            $"The Kyber-Weave skill '{name}' at .apm/skills/{name} clashes with the {kind} at {squadPath}. " +
            $"Rename the .apm/skills/{name} directory; neither side may silently shadow the other.");
    }
}
