using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
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
/// <param name="files">Probes links and lists the companion skill folders.</param>
/// <param name="paths">Resolves a Squad source root to a full path.</param>
/// <param name="environment">Supplies the directory a relative Squad source root is resolved against.</param>
public sealed class SquadCompanionSkills(IFileSystem files, IPathResolver paths, IProcessEnvironment environment)
    : ISquadCompanionSkills
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
    public string? ResolveRoot(string squadSourceRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(paths.GetFullPath(squadSourceRoot, environment.CurrentDirectory));
        string? products = Path.GetDirectoryName(root);
        string? repository = products is null ? null : Path.GetDirectoryName(products);
        if (Path.GetFileName(root) != "kyber-squad" || Path.GetFileName(products) != "products" || repository is null)
        {
            return null;
        }

        string apmRoot = Path.Combine(repository, ".apm");
        if (files.GetLinkTarget(apmRoot) is not null)
        {
            throw SymbolicLink(".apm");
        }

        string skillsRoot = Path.Combine(apmRoot, "skills");
        if (files.GetLinkTarget(skillsRoot) is not null)
        {
            throw SymbolicLink(".apm/skills");
        }

        return files.DirectoryExists(skillsRoot) ? skillsRoot : null;
    }

    /// <summary>
    /// Lists every file of every companion skill, as its Squad-tree path (<c>skills/&lt;name&gt;/&lt;relative&gt;</c>,
    /// forward slashes) and its source path. A null companion root lists nothing.
    /// </summary>
    /// <remarks>
    /// A companion name that equals a Squad skill or a Squad agent, ignoring case, throws <see cref="SquadSourceValidationException"/>.
    /// Failing closed keeps either side from silently shadowing the other: one skill would otherwise vanish from every
    /// harness with no signal. A symbolic link at a companion skill's directory, its <c>SKILL.md</c>, or any file or folder
    /// nested in it throws for the same reason. The exception carries no partial list, so no caller can report a partial tree.
    /// </remarks>
    public IReadOnlyList<(string RelativePath, string SourcePath)> Collect(
        string squadSourceRoot,
        string? companionSkillsRoot)
    {
        List<(string RelativePath, string SourcePath)> collected = [];
        if (companionSkillsRoot is null)
        {
            return collected;
        }

        foreach (string skillDirectory in SkillDirectories(companionSkillsRoot))
        {
            string name = Path.GetFileName(skillDirectory);
            RejectClash(squadSourceRoot, name);

            foreach (string filePath in EnumerateSkillFiles(skillDirectory, $".apm/skills/{name}"))
            {
                string relativePath = Path.GetRelativePath(skillDirectory, filePath).Replace('\\', '/');
                collected.Add(($"skills/{name}/{relativePath}", filePath));
            }
        }

        return collected;
    }

    /// <summary>
    /// Every direct child of the companion root that is a real directory holding a real <c>SKILL.md</c>, ordinally sorted.
    /// </summary>
    private IEnumerable<string> SkillDirectories(string companionSkillsRoot)
    {
        return files.EnumerateDirectories(companionSkillsRoot)
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
    private IEnumerable<string> EnumerateSkillFiles(string directory, string skillPath)
    {
        List<FileSystemEntry> entries = files.EnumerateEntries(directory)
            .OrderBy(entry => Path.GetFileName(entry.Path), StringComparer.Ordinal)
            .ToList();

        foreach (FileSystemEntry entry in entries)
        {
            string entryPath = $"{skillPath}/{Path.GetFileName(entry.Path)}";
            if (entry.IsLink)
            {
                throw SymbolicLink(entryPath);
            }

            if (entry.IsDirectory)
            {
                foreach (string file in EnumerateSkillFiles(entry.Path, entryPath))
                {
                    yield return file;
                }
            }
            else
            {
                yield return entry.Path;
            }
        }
    }

    /// <remarks>
    /// A symbolic link at the directory or at its <c>SKILL.md</c> throws rather than skipping the skill. A skipped skill
    /// would release a tree without a skill the operator authored, and nothing would say so. A linked skill could also
    /// pull content in from outside the tree.
    /// </remarks>
    private bool IsSkillDirectory(string directory)
    {
        string name = Path.GetFileName(directory);
        if (files.GetLinkTarget(directory) is not null)
        {
            throw SymbolicLink($".apm/skills/{name}");
        }

        string skillFile = Path.Combine(directory, "SKILL.md");
        if (files.GetLinkTarget(skillFile) is not null)
        {
            throw SymbolicLink($".apm/skills/{name}/SKILL.md");
        }

        return files.FileExists(skillFile);
    }

    private void RejectClash(string squadSourceRoot, string name)
    {
        string? squadSkill = EntryNamedIgnoringCase(Path.Combine(squadSourceRoot, "skills"), name, isDirectory: true);
        if (squadSkill is not null)
        {
            throw Clash(name, "Squad skill", $"products/kyber-squad/skills/{squadSkill}");
        }

        string? squadAgent = EntryNamedIgnoringCase(Path.Combine(squadSourceRoot, "agents"), $"{name}.md", isDirectory: false);
        if (squadAgent is not null)
        {
            throw Clash(name, "Squad agent", $"products/kyber-squad/agents/{squadAgent}");
        }
    }

    /// <summary>
    /// The entry of <paramref name="directory"/> whose name equals <paramref name="entryName"/> ignoring case, or null. The
    /// folder is listed rather than probed by path, because a probe follows the host's own case rules: on Linux
    /// <c>Task-Gate</c> would find no <c>task-gate</c>, and the install on macOS or Windows would then collide.
    /// </summary>
    private string? EntryNamedIgnoringCase(string directory, string entryName, bool isDirectory)
    {
        if (!files.DirectoryExists(directory))
        {
            return null;
        }

        IEnumerable<string> entries = isDirectory
            ? files.EnumerateDirectories(directory)
            : files.EnumerateFiles(directory, "*", recursive: false);
        return entries
            .Select(path => Path.GetFileName(path))
            .FirstOrDefault(fileName => string.Equals(fileName, entryName, StringComparison.OrdinalIgnoreCase));
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
