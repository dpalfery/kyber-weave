using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Parsing;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using System.Diagnostics.CodeAnalysis;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Docs.KwSkills;

/// <summary>
/// The KW skill catalog for one checkout: which skills a technology and a role agent get, and one
/// skill loaded by name, the repository's before the shipped one.
/// </summary>
/// <remarks>
/// <para>
/// A skill name arrives from a caller, so it is checked before it can become a path: it must be spelled
/// as a skill name and be in <see cref="Names"/>. Only then is a path built, and every folder is read
/// with links left unfollowed, so a link in a skill folder cannot reach outside it.
/// </para>
/// <para>
/// The repository pool is <c>standards/skills/&lt;name&gt;/SKILL.md</c> under each docs root, the first
/// root holding a name winning it. A repository skill wins the whole name, so a host can replace a
/// shipped skill without a partial merge of files.
/// </para>
/// </remarks>
public sealed class KwSkillCatalog : IKwSkillCatalog
{
    private const string SkillFile = "SKILL.md";
    private const string MalformedListRule = "KW-DOC-SKILL-001";

    private readonly IWritableFileSystem _files;
    private readonly IPathResolver _paths;
    private readonly IEmbeddedResources _resources;
    private readonly ShippedKwSkills _shipped;
    private readonly string _repositoryRoot;
    private readonly IReadOnlyList<string> _docsRoots;
    private readonly IReadOnlySet<string> _roles;

    /// <summary>Creates the catalog for a checkout.</summary>
    /// <param name="files">Reads the repository pool and writes a shipped skill's copy.</param>
    /// <param name="paths">Resolves and contains paths.</param>
    /// <param name="environment">Names the working directory and the home folder.</param>
    /// <param name="resources">Holds the shipped pool.</param>
    /// <param name="repositoryRoot">The checkout's root.</param>
    /// <param name="config">The effective host configuration: docs roots and declared roles.</param>
    public KwSkillCatalog(
        IWritableFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IEmbeddedResources resources,
        string repositoryRoot,
        KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(config);
        _files = files;
        _paths = paths;
        _resources = resources;
        _shipped = new ShippedKwSkills(resources, files, paths, environment);
        _repositoryRoot = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        _docsRoots = config.Ontology.DocsRoots;
        _roles = new HashSet<string>([.. config.Ontology.Roles, .. KyberStandardsTemplates.Roles], StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public KwSkillList SkillsFor(string? technology, string? roleAgent)
    {
        List<KwSkillEntry> entries = [];
        List<KwSkillProblem> problems = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        string? tech = Scope(technology);
        string? parent = tech is null ? null : ParentOf(tech, problems);
        string? agent = roleAgent?.Trim();
        string? role = string.IsNullOrEmpty(agent) ? null : KwSkillRoles.FolderFor(agent, _roles);

        foreach (string? scope in new[] { parent, tech, role })
        {
            if (scope is null)
            {
                continue;
            }

            foreach (string name in ReadList(scope, problems))
            {
                if (seen.Add(name))
                {
                    Describe(name, scope, entries, problems);
                }
            }
        }

        return new KwSkillList(entries, problems);
    }

    /// <inheritdoc />
    public KwSkillLoadResult Load(string name)
    {
        string wanted = name?.Trim() ?? string.Empty;
        if (!KwSkillNames.IsValid(wanted))
        {
            return KwSkillLoadResult.Unknown(wanted, Names());
        }

        IReadOnlyList<string> existing = Names();
        if (!existing.Contains(wanted, StringComparer.Ordinal))
        {
            return KwSkillLoadResult.Unknown(wanted, existing);
        }

        string? folder = RepositoryFolder(wanted);
        if (folder is not null)
        {
            string? text = SafeRead(Path.Combine(folder, SkillFile));
            if (text is null)
            {
                return KwSkillLoadResult.Malformed(wanted, folder, [], "its SKILL.md could not be read");
            }

            return Parse(wanted, text, folder, out Skill? skill, out KwSkillLoadResult? bad)
                ? KwSkillLoadResult.Found(wanted, skill.InstructionsBody, folder, KwSkillSource.Repository, References(folder))
                : bad;
        }

        string? shipped = SafeShippedRead(wanted);
        if (shipped is null)
        {
            return KwSkillLoadResult.Malformed(wanted, wanted, [], "its SKILL.md could not be read");
        }

        if (!Parse(wanted, shipped, wanted, out Skill? shippedSkill, out KwSkillLoadResult? shippedBad))
        {
            return shippedBad;
        }

        try
        {
            string copy = _shipped.Materialize(wanted);
            return KwSkillLoadResult.Found(
                wanted, shippedSkill.InstructionsBody, copy, KwSkillSource.Shipped, References(copy));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or MissingEmbeddedResourceException)
        {
            // The body is still the answer; only the reference files need the copy.
            string reason = exception.Message.ReplaceLineEndings(" ");
            return KwSkillLoadResult.Found(
                wanted, shippedSkill.InstructionsBody, $"(not materialized: {reason})", KwSkillSource.Shipped, []);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Names()
    {
        SortedSet<string> names = new(StringComparer.Ordinal);
        foreach (string pool in PoolFolders())
        {
            foreach (string folder in Directories(pool))
            {
                string name = Path.GetFileName(folder);
                if (KwSkillNames.IsValid(name) && IsSkillFolder(folder))
                {
                    names.Add(name);
                }
            }
        }

        foreach (string name in _shipped.Names)
        {
            names.Add(name);
        }

        return [.. names];
    }

    private static string? Scope(string? value)
    {
        string? trimmed = value?.Trim();
        return KwSkillNames.IsValid(trimmed) ? trimmed : null;
    }

    private string Absolute(string relative) => _paths.GetFullPath(relative, _repositoryRoot);

    private IEnumerable<string> PoolFolders() =>
        _docsRoots.Select(root => Absolute(DocsLayout.SkillsPool(root))).Where(pool => _files.DirectoryExists(pool) && !IsLink(pool));

    private bool IsLink(string path) => _files.GetLinkTarget(path) is not null;

    private bool IsSkillFolder(string folder)
    {
        string file = Path.Combine(folder, SkillFile);
        return _files.FileExists(file) && !IsLink(file);
    }

    private IEnumerable<string> Directories(string folder) =>
        _files.EnumerateEntries(folder)
            .Where(entry => entry is { IsDirectory: true, IsLink: false })
            .Select(entry => entry.Path);

    private string? RepositoryFolder(string name)
    {
        foreach (string pool in PoolFolders())
        {
            string folder = Path.Combine(pool, name);
            if (_files.DirectoryExists(folder) && !IsLink(folder) && IsSkillFolder(folder))
            {
                return folder;
            }
        }

        return null;
    }

    private string? ParentOf(string technology, List<KwSkillProblem> problems)
    {
        string? file = null;
        string? text = null;
        foreach (string root in _docsRoots)
        {
            string relative = DocsLayout.StandardFile(root, technology);
            string candidate = Absolute(relative);
            if (_files.FileExists(candidate))
            {
                file = relative;
                text = SafeRead(candidate);
                if (text is null)
                {
                    problems.Add(new KwSkillProblem(
                        relative,
                        $"the '{technology}' standard could not be read, so the skills of the technology it builds on "
                        + "may not be listed"));
                }

                break;
            }
        }

        if (text is null)
        {
            try
            {
                text = _resources.ReadAllText($"Standards.{technology}.standards.md");
            }
            catch (MissingEmbeddedResourceException)
            {
                return null;
            }
        }

        FrontmatterReadResult read = MarkdownFrontmatterReader.Read(text);
        if (!read.HasFrontmatter)
        {
            return null;
        }

        try
        {
            string? parent = MarkdownFrontmatterReader.Deserializer.Deserialize<DocumentFrontmatter>(read.Yaml)?.BuildsOn;
            string? scope = Scope(parent);
            return scope == technology ? null : scope;
        }
        catch (YamlException exception)
        {
            problems.Add(new KwSkillProblem(
                file ?? $"Standards.{technology}.standards.md",
                $"the '{technology}' standard's frontmatter could not be read, so the skills of the technology "
                + $"it builds on are not listed: {exception.Message.ReplaceLineEndings(" ")}"));
            return null;
        }
    }

    private string? SafeRead(string file)
    {
        try
        {
            return _files.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a shipped skill's SKILL.md, answering null when the manifest lists a file the product
    /// never embedded under either name spelling, so a packaging gap surfaces as Malformed rather
    /// than as a thrown error.
    /// </summary>
    private string? SafeShippedRead(string name)
    {
        try
        {
            return _shipped.ReadSkillFile(name);
        }
        catch (Exception exception) when (exception is MissingEmbeddedResourceException or InvalidDataException)
        {
            return null;
        }
    }

    private List<string> ReadList(string scope, List<KwSkillProblem> problems)
    {
        foreach (string root in _docsRoots)
        {
            string relative = DocsLayout.SkillsListFile(root, scope);
            string file = Absolute(relative);
            if (!_files.FileExists(file) || IsLink(file))
            {
                continue;
            }

            string? text = SafeRead(file);
            if (text is null)
            {
                problems.Add(new KwSkillProblem(relative, $"{MalformedListRule}: the list could not be read."));
                return [];
            }

            return ParseList(text, relative, problems);
        }

        return [];
    }

    private static List<string> ParseList(string text, string relative, List<KwSkillProblem> problems)
    {
        if (KwSkillFiles.TryParseList(text, out List<string> names))
        {
            return names;
        }

        problems.Add(new KwSkillProblem(
            relative,
            $"{MalformedListRule}: a skills list is a YAML sequence of skill names and nothing else; this file lists nothing."));
        return [];
    }

    private void Describe(string name, string scope, List<KwSkillEntry> entries, List<KwSkillProblem> problems)
    {
        string listFile = $"the {scope} skills list";
        if (!KwSkillNames.IsValid(name) || !Names().Contains(name, StringComparer.Ordinal))
        {
            problems.Add(new KwSkillProblem(listFile, $"{MalformedListRule}: '{name}' is not a skill in either pool."));
            return;
        }

        string? folder = RepositoryFolder(name);
        KwSkillSource source = folder is null ? KwSkillSource.Shipped : KwSkillSource.Repository;
        string? text = folder is null ? SafeShippedRead(name) : SafeRead(Path.Combine(folder, SkillFile));
        if (text is null)
        {
            problems.Add(new KwSkillProblem(listFile, $"'{name}' cannot be read: its SKILL.md could not be read."));
            return;
        }

        if (!Parse(name, text, folder ?? name, out Skill? skill, out KwSkillLoadResult? bad))
        {
            string why = bad.Reason ?? "its SKILL.md has no " + string.Join("/", bad.MissingFields);
            problems.Add(new KwSkillProblem(listFile, $"'{name}' cannot be read: {why}."));
            return;
        }

        entries.Add(new KwSkillEntry(name, skill.Frontmatter.Description!.Trim(), source));
    }

    private static bool Parse(
        string name,
        string text,
        string folder,
        [NotNullWhen(true)] out Skill? skill,
        [NotNullWhen(false)] out KwSkillLoadResult? bad)
    {
        if (KwSkillFiles.TryInspect(text, Path.Combine(folder, SkillFile), folder, out skill, out string? parseError, out List<string> missing))
        {
            bad = null;
            return true;
        }

        bad = KwSkillLoadResult.Malformed(name, folder, missing, parseError);
        return false;
    }

    private List<string> References(string folder)
    {
        List<string> found = [];
        Walk(folder, found);
        string skillFile = Path.Combine(folder, SkillFile);
        found.RemoveAll(path => string.Equals(path, skillFile, StringComparison.Ordinal));
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private void Walk(string folder, List<string> found)
    {
        foreach (FileSystemEntry entry in _files.EnumerateEntries(folder))
        {
            if (entry.IsLink)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                Walk(entry.Path, found);
            }
            else
            {
                found.Add(entry.Path);
            }
        }
    }
}
