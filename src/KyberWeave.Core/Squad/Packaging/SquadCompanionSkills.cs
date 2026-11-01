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
    /// <remarks>
    /// A symbolic link at <c>.apm</c> or <c>.apm/skills</c> throws, for the same reason a linked skill does: the packer
    /// would read whatever the link points at, and the per-skill checks never see the root.
    /// </remarks>
    /// <exception cref="SquadSourceValidationException">Thrown when <c>.apm</c> or <c>.apm/skills</c> is a symbolic link.</exception>
    public static string? ResolveRoot(string squadSourceRoot)
    {
        DirectoryInfo root = new(Path.GetFullPath(squadSourceRoot));
        DirectoryInfo? products = root.Parent;
        DirectoryInfo? repository = products?.Parent;
        if (root.Name != "kyber-squad" || products?.Name != "products" || repository is null)
        {
            return null;
        }

        string apmRoot = Path.Combine(repository.FullName, ".apm");
        if (new DirectoryInfo(apmRoot).LinkTarget is not null)
        {
            throw SymbolicLink(".apm");
        }

        string skillsRoot = Path.Combine(apmRoot, "skills");
        if (new DirectoryInfo(skillsRoot).LinkTarget is not null)
        {
            throw SymbolicLink(".apm/skills");
        }

        return Directory.Exists(skillsRoot) ? skillsRoot : null;
    }

    /// <summary>
    /// Lists every file of every companion skill, as its Squad-tree path (<c>skills/&lt;name&gt;/&lt;relative&gt;</c>,
    /// forward slashes) and its source path. A null companion root lists nothing.
    /// </summary>
    /// <remarks>
    /// A companion name that equals a Squad skill or a Squad agent throws <see cref="SquadSourceValidationException"/>.
    /// Failing closed keeps either side from silently shadowing the other: one skill would otherwise vanish from every
    /// harness with no signal. A symbolic link at a companion skill's directory, its <c>SKILL.md</c>, or any file or folder
    /// nested in it throws for the same reason. The exception carries no partial list, so no caller can report a partial tree.
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

            foreach (string filePath in EnumerateSkillFiles(skillDirectory, $".apm/skills/{name}"))
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

    /// <summary>
    /// Every file under one companion skill, depth first, each folder ordinally sorted. A symbolic link at any depth
    /// throws. The contained-file walk skips links, so skipping one here would ship a skill that is missing a file it
    /// names, with no signal.
    /// </summary>
    /// <remarks>
    /// Lists one folder at a time, so a link is seen as a link and never descended into. Hidden entries are included.
    /// </remarks>
    private static IEnumerable<string> EnumerateSkillFiles(string directory, string skillPath)
    {
        EnumerationOptions options = new() { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        List<FileSystemInfo> entries = new DirectoryInfo(directory)
            .EnumerateFileSystemInfos("*", options)
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();

        foreach (FileSystemInfo entry in entries)
        {
            string entryPath = $"{skillPath}/{entry.Name}";
            if (entry.LinkTarget is not null)
            {
                throw SymbolicLink(entryPath);
            }

            if (entry is DirectoryInfo subdirectory)
            {
                foreach (string file in EnumerateSkillFiles(subdirectory.FullName, entryPath))
                {
                    yield return file;
                }
            }
            else
            {
                yield return entry.FullName;
            }
        }
    }

    /// <remarks>
    /// A symbolic link at the directory or at its <c>SKILL.md</c> throws rather than skipping the skill. A skipped skill
    /// would release a tree without a skill the operator authored, and nothing would say so. A linked skill could also
    /// pull content in from outside the tree.
    /// </remarks>
    private static bool IsSkillDirectory(string directory)
    {
        string name = Path.GetFileName(directory);
        if (new DirectoryInfo(directory).LinkTarget is not null)
        {
            throw SymbolicLink($".apm/skills/{name}");
        }

        string skillFile = Path.Combine(directory, "SKILL.md");
        if (new FileInfo(skillFile).LinkTarget is not null)
        {
            throw SymbolicLink($".apm/skills/{name}/SKILL.md");
        }

        return File.Exists(skillFile);
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

    private static SquadSourceValidationException SymbolicLink(string path)
    {
        return new SquadSourceValidationException(
            $"The Kyber-Weave skill path {path} is a symbolic link. Replace the link with a real copy of the file or " +
            "folder; a link is refused rather than skipped, so no skill ships without its content.");
    }
}
