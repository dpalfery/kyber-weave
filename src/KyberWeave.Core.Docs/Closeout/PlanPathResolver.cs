using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>What <see cref="PlanPathResolver"/> found for a plan or specification argument.</summary>
/// <param name="Path">The repository-relative path of the one file the argument names, or null.</param>
/// <param name="Candidates">When the argument matches several files, their repository-relative paths; otherwise empty.</param>
/// <param name="SearchedFolders">The planning folders that were searched, repository-relative.</param>
public sealed record PlanPathResolution(string? Path, IReadOnlyList<string> Candidates, IReadOnlyList<string> SearchedFolders)
{
    /// <summary>True when the argument named exactly one file.</summary>
    public bool Found => Path is not null;

    /// <summary>True when the argument matched more than one file.</summary>
    public bool Ambiguous => Candidates.Count > 1;
}

/// <summary>
/// The one place a plan or specification argument becomes a file. Every command that takes one — <c>docs closeout</c>,
/// <c>docs tasks</c>, <c>arbiter plan</c> — resolves it here, so an agent that types the plan the way it naturally
/// refers to it gets the same answer from each.
/// </summary>
/// <remarks>
/// <para>
/// Spellings accepted, in this order, the first that names a file winning: a full path; a path relative to the current
/// directory; a path relative to the repository root; a bare file name, with or without <c>.md</c>, in a planning
/// folder; a bare slug, with or without its date prefix, in a planning folder; a specification's folder name or its
/// <c>tasks.md</c>. The planning folders are the ones the <c>plan-index</c> and <c>specification-index</c> registry
/// properties name, so a host whose docs root is not <c>docs</c> is searched where its plans are.
/// </para>
/// <para>
/// Only ambiguity is an error: a slug that matches several active plans resolves to none of them and lists each.
/// </para>
/// </remarks>
public sealed partial class PlanPathResolver
{
    private const string Markdown = ".md";
    private const string SpecTaskList = "tasks.md";

    private readonly string _repoRoot;
    private readonly KyberWeaveConfig _config;

    /// <summary>Creates a resolver over a repository and its effective configuration.</summary>
    public PlanPathResolver(string repoRoot, KyberWeaveConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        _repoRoot = System.IO.Path.GetFullPath(repoRoot);
        _config = config;
    }

    /// <summary>The folders under the repository root a bare name or slug is searched in, repository-relative.</summary>
    public IReadOnlyList<string> PlanningFolders()
    {
        IReadOnlyList<ConfigRegEntry> registry = _config.ConfigReg.Resolve(_config.Ontology);
        List<string> folders = [];
        foreach (string property in new[] { ConfigRegConfig.PlanIndexProperty, ConfigRegConfig.SpecificationIndexProperty })
        {
            string? index = registry.FirstOrDefault(entry => string.Equals(entry.Name, property, StringComparison.Ordinal))?.Path;
            if (string.IsNullOrWhiteSpace(index))
            {
                continue;
            }

            string normalized = index.Replace('\\', '/');
            int separator = normalized.LastIndexOf('/');
            string folder = separator > 0 ? normalized[..separator] : string.Empty;
            if (folder.Length > 0 && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }

    /// <summary>Resolves <paramref name="argument"/> to the one plan or specification file it names.</summary>
    /// <param name="argument">What the caller typed.</param>
    /// <param name="workingDirectory">The directory a relative path is read against.</param>
    public PlanPathResolution Resolve(string argument, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        IReadOnlyList<string> folders = PlanningFolders();
        string typed = argument.Trim().Trim('"', '\'');
        if (typed.Length == 0)
        {
            return new PlanPathResolution(null, [], folders);
        }

        string[] bases = System.IO.Path.IsPathRooted(typed) ? [string.Empty] : [workingDirectory, _repoRoot];
        foreach (string basePath in bases)
        {
            string candidate = basePath.Length == 0 ? typed : System.IO.Path.GetFullPath(typed, basePath);
            if (FileFor(candidate) is { } file)
            {
                return Found(file);
            }
        }

        string name = typed.Replace('\\', '/').TrimEnd('/');
        bool bare = !name.Contains('/', StringComparison.Ordinal);
        string withExtension = name.EndsWith(Markdown, StringComparison.OrdinalIgnoreCase) ? name : name + Markdown;
        string stem = withExtension[..^Markdown.Length];

        List<string> matches = [];
        foreach (string folder in folders)
        {
            string directory = Absolute(folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (File.Exists(System.IO.Path.Combine(directory, withExtension)))
            {
                matches.Add(Relative(System.IO.Path.Combine(directory, withExtension)));
            }
        }

        if (matches.Count == 0 && bare)
        {
            foreach (string folder in folders)
            {
                string directory = Absolute(folder);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                matches.AddRange(Directory.EnumerateFiles(directory, "*" + Markdown, SearchOption.TopDirectoryOnly)
                    .Where(file => IsPlanFile(file) && string.Equals(WithoutDatePrefix(System.IO.Path.GetFileNameWithoutExtension(file)), WithoutDatePrefix(stem), StringComparison.OrdinalIgnoreCase))
                    .Select(Relative));
                if (Directory.Exists(System.IO.Path.Combine(directory, stem))
                    && File.Exists(System.IO.Path.Combine(directory, stem, SpecTaskList)))
                {
                    matches.Add(Relative(System.IO.Path.Combine(directory, stem, SpecTaskList)));
                }
            }
        }

        matches = matches.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        return matches.Count switch
        {
            0 => new PlanPathResolution(null, [], folders),
            1 => new PlanPathResolution(matches[0], [], folders),
            _ => new PlanPathResolution(null, matches, folders)
        };
    }

    private PlanPathResolution Found(string absoluteFile) => new(Relative(absoluteFile), [], PlanningFolders());

    /// <summary>The file a path names: itself, the same with <c>.md</c> added, or a specification folder's <c>tasks.md</c>.</summary>
    private static string? FileFor(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (File.Exists(path + Markdown))
        {
            return path + Markdown;
        }

        string taskList = System.IO.Path.Combine(path, SpecTaskList);
        return Directory.Exists(path) && File.Exists(taskList) ? taskList : null;
    }

    private static bool IsPlanFile(string file) =>
        !string.Equals(System.IO.Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase);

    private static string WithoutDatePrefix(string stem) => DatePrefix().Replace(stem, string.Empty, 1);

    private string Absolute(string relative) =>
        System.IO.Path.Combine(_repoRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>A path relative to the repository root, or the full path when the file lies outside it.</summary>
    private string Relative(string absolute)
    {
        string relative = System.IO.Path.GetRelativePath(_repoRoot, absolute).Replace('\\', '/');
        return relative.StartsWith("../", StringComparison.Ordinal) ? absolute.Replace('\\', '/') : relative;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-")]
    private static partial Regex DatePrefix();
}
