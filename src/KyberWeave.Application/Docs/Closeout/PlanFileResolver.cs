using System.Text.RegularExpressions;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Closeout;

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
public partial class PlanFileResolver
{
    private const string Markdown = ".md";
    private const string SpecTaskList = "tasks.md";

    private readonly string _repoRoot;
    private readonly IDocsEngineConfig _config;
    private readonly IRepositoryFileStore _files;
    private readonly IPathResolver _paths;

    /// <summary>Creates a resolver over a repository and its effective configuration.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="files">Probes the planning folders.</param>
    /// <param name="paths">Resolves the repository root and a typed path to full paths.</param>
    /// <param name="environment">Names the directory a relative repository root is resolved against.</param>
    public PlanFileResolver(string repoRoot, IDocsEngineConfig config, IRepositoryFileStore files, IPathResolver paths, IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        _files = files;
        _paths = paths;
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        _repoRoot = paths.GetFullPath(repoRoot, environment.CurrentDirectory);
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
            string candidate = basePath.Length == 0 ? typed : _paths.GetFullPath(typed, basePath);
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
            if (!_files.DirectoryExists(directory))
            {
                continue;
            }

            if (_files.FileExists(System.IO.Path.Combine(directory, withExtension)))
            {
                matches.Add(Relative(System.IO.Path.Combine(directory, withExtension)));
            }
        }

        if (matches.Count == 0 && bare)
        {
            foreach (string folder in folders)
            {
                string directory = Absolute(folder);
                if (!_files.DirectoryExists(directory))
                {
                    continue;
                }

                matches.AddRange(_files.EnumerateFiles(directory, "*" + Markdown, recursive: false)
                    .Where(file => IsPlanFile(file) && string.Equals(WithoutDatePrefix(System.IO.Path.GetFileNameWithoutExtension(file)), WithoutDatePrefix(stem), StringComparison.OrdinalIgnoreCase))
                    .Select(Relative));
                if (_files.DirectoryExists(System.IO.Path.Combine(directory, stem))
                    && _files.FileExists(System.IO.Path.Combine(directory, stem, SpecTaskList)))
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
    private string? FileFor(string path)
    {
        if (_files.FileExists(path))
        {
            return path;
        }

        if (_files.FileExists(path + Markdown))
        {
            return path + Markdown;
        }

        string taskList = System.IO.Path.Combine(path, SpecTaskList);
        return _files.DirectoryExists(path) && _files.FileExists(taskList) ? taskList : null;
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
