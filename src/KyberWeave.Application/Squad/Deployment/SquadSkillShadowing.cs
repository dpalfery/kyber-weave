using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Squad.Deployment;

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
/// (<see cref="SquadSkillOutputPaths.ProjectSkillRoots"/>), so the list cannot drift from the renderers.
/// </para>
/// </remarks>
/// <param name="files">Reads the installed and project skill files.</param>
/// <param name="paths">Turns the roots and managed paths into full paths.</param>
/// <param name="environment">Supplies the base directory a relative root resolves against.</param>
public sealed class SquadSkillShadowing(
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : ISquadSkillShadowing
{
    private const string SkillFileName = "SKILL.md";

    /// <summary>The project skills that shadow an installed squad skill.</summary>
    /// <param name="projectRoot">The repository root.</param>
    /// <param name="installed">The installed squad skills, project and global.</param>
    /// <param name="managedPaths">Absolute paths the deployment owns, which are never reported.</param>
    public IReadOnlyList<SquadSkillShadow> Find(
        string projectRoot,
        IReadOnlyList<SquadInstalledSkill> installed,
        IReadOnlySet<string> managedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(managedPaths);

        string root = FullPath(projectRoot);
        HashSet<string> managed = new(managedPaths.Select(FullPath), StringComparer.Ordinal);
        List<SquadSkillShadow> shadows = [];

        foreach (IGrouping<string, SquadInstalledSkill> skill in installed.GroupBy(i => i.Name, StringComparer.Ordinal))
        {
            List<(SquadInstalledSkill Copy, string Body)> copies = [];
            foreach (SquadInstalledSkill copy in skill)
            {
                if (ReadBody(copy.FilePath) is { } body) copies.Add((copy, body));
            }

            if (copies.Count == 0) continue;

            foreach (string location in SquadSkillOutputPaths.ProjectSkillRoots())
            {
                string candidate = FullPath(Path.Combine(
                    root, location.Replace('/', Path.DirectorySeparatorChar), skill.Key, SkillFileName));
                if (managed.Contains(candidate) || !files.FileExists(candidate)) continue;
                if (ReadBody(candidate) is not { } projectBody) continue;
                if (copies.Exists(c => string.Equals(c.Body, projectBody, StringComparison.Ordinal))) continue;

                shadows.Add(new SquadSkillShadow(skill.Key, candidate, copies[0].Copy.FilePath));
            }
        }

        return shadows;
    }

    private string FullPath(string path) => paths.GetFullPath(path, environment.CurrentDirectory);

    private string? ReadBody(string path)
    {
        string text;
        try
        {
            text = files.ReadAllText(path);
        }
        catch (Exception ex) when (FileFailure.Is(ex))
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
