using KyberWeave.Core.Squad.Rendering;

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

/// <summary>
/// Finds project-level skills that hide an installed squad skill.
/// </summary>
/// <remarks>
/// <para>
/// The harnesses read project skill folders themselves, and a project copy of a skill with the
/// same name can be the one a worker loads instead of the installed one. A repository that kept
/// its own old copy of a skill then runs whatever that copy says, long after the squad changed it.
/// </para>
/// <para>
/// Only copies the deployment does not own are considered: a managed file is the squad's own
/// output. Content is compared without the frontmatter and line-ending differences, because each
/// harness renders its own frontmatter around the same skill; a copy equal to any installed copy is
/// the same skill. Each project skill location is one a renderer writes to
/// (<see cref="SquadRendererRegistry.ProjectSkillRoots"/>), so the list cannot drift from the renderers.
/// </para>
/// </remarks>
public static class SquadSkillShadowing
{
    private const string SkillFileName = "SKILL.md";

    /// <summary>The project skills that shadow an installed squad skill.</summary>
    /// <param name="projectRoot">The repository root.</param>
    /// <param name="installed">The installed squad skills, project and global.</param>
    /// <param name="managedPaths">Absolute paths the deployment owns, which are never reported.</param>
    public static IReadOnlyList<SquadSkillShadow> Find(
        string projectRoot,
        IReadOnlyList<SquadInstalledSkill> installed,
        IReadOnlySet<string> managedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(managedPaths);

        string root = Path.GetFullPath(projectRoot);
        HashSet<string> managed = new(managedPaths.Select(Path.GetFullPath), StringComparer.Ordinal);
        List<SquadSkillShadow> shadows = [];

        foreach (IGrouping<string, SquadInstalledSkill> skill in installed.GroupBy(i => i.Name, StringComparer.Ordinal))
        {
            List<(SquadInstalledSkill Copy, string Body)> copies = [];
            foreach (SquadInstalledSkill copy in skill)
            {
                if (ReadBody(copy.FilePath) is { } body) copies.Add((copy, body));
            }

            if (copies.Count == 0) continue;

            foreach (string location in SquadRendererRegistry.ProjectSkillRoots())
            {
                string candidate = Path.GetFullPath(Path.Combine(
                    root, location.Replace('/', Path.DirectorySeparatorChar), skill.Key, SkillFileName));
                if (managed.Contains(candidate) || !File.Exists(candidate)) continue;
                if (ReadBody(candidate) is not { } projectBody) continue;
                if (copies.Exists(c => string.Equals(c.Body, projectBody, StringComparison.Ordinal))) continue;

                shadows.Add(new SquadSkillShadow(skill.Key, candidate, copies[0].Copy.FilePath));
            }
        }

        return shadows;
    }

    private static string? ReadBody(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        int start = 0;
        if (lines.Length > 0 && lines[0].TrimEnd() == "---")
        {
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd() != "---") continue;
                start = i + 1;
                break;
            }
        }

        return string.Join('\n', lines.Skip(start).Select(line => line.TrimEnd())).Trim();
    }
}
