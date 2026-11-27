using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>How one installed standards folder differs from the current template.</summary>
/// <param name="Name">The folder's technology, role or platform name.</param>
/// <param name="Root">The docs root the folder was found under.</param>
/// <param name="Missing">Template files the host folder lacks.</param>
/// <param name="HostOnly">Files the host folder has that the template does not.</param>
/// <param name="Changed">Files present in both whose content differs.</param>
public sealed record StandardsFolderDiff(
    string Name,
    string Root,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> HostOnly,
    IReadOnlyList<string> Changed);

/// <summary>The diff for one folder, or for every installed folder.</summary>
public sealed record StandardsDiffResult(IReadOnlyList<StandardsFolderDiff> Folders);

/// <summary>
/// Compares a host's standards folders with the templates that ship, and writes nothing.
/// </summary>
/// <remarks>
/// The owner, status and last-reviewed frontmatter values are host choices, not differences:
/// a standard promoted to <c>current</c> with the host's owner is not "out of date". The
/// comparison ignores those three lines and compares everything else.
/// </remarks>
public sealed class StandardsTemplateDiff
{
    private static readonly string[] HostOwnedKeys = ["owner:", "status:", "last-reviewed:"];

    private readonly IFileSystem _files;
    private readonly IKyberStandardsTemplates _templates;
    private readonly OntologyConfig _config;
    private readonly string _repoRoot;

    /// <summary>Creates the diff over the seams it reads through.</summary>
    /// <param name="files">Reads the host's standards files.</param>
    /// <param name="repoRoot">The repository whose standards folders are compared.</param>
    /// <param name="config">The ontology that declares the technologies and roles.</param>
    /// <param name="paths">Turns the repository root into a full path.</param>
    /// <param name="environment">Supplies the base directory a relative root is resolved against.</param>
    /// <param name="templates">Supplies the template text a host file is compared with.</param>
    public StandardsTemplateDiff(
        IFileSystem files,
        string repoRoot,
        OntologyConfig config,
        IPathResolver paths,
        IProcessEnvironment environment,
        IKyberStandardsTemplates templates)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(templates);
        _files = files;
        _templates = templates;
        _repoRoot = paths.GetFullPath(repoRoot, environment.CurrentDirectory);
        _config = config;
    }

    /// <summary>Compares one named folder, or every declared folder when <paramref name="name"/> is null.</summary>
    public StandardsDiffResult Run(string? name = null)
    {
        IReadOnlyList<string> scopes = _config.Technologies
            .Concat(_config.Roles)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(name))
        {
            string trimmed = name.Trim();
            if (!scopes.Contains(trimmed, StringComparer.Ordinal))
            {
                string? nearest = KyberWeave.Core.Text.StringDistance.Nearest(trimmed, scopes);
                throw new ArgumentException(
                    nearest is null
                        ? $"No standards folder named '{trimmed}' is declared by this repository."
                        : $"No standards folder named '{trimmed}'. Did you mean '{nearest}'?",
                    nameof(name));
            }

            scopes = [trimmed];
        }

        List<StandardsFolderDiff> diffs = [];
        foreach (string scope in scopes)
        {
            foreach (string docsRoot in _config.DocsRoots)
            {
                StandardsFolderDiff? diff = Compare(docsRoot, scope);
                if (diff is not null) diffs.Add(diff);
            }
        }

        return new StandardsDiffResult(diffs);
    }

    private StandardsFolderDiff? Compare(string docsRoot, string scope)
    {
        string folder = $"{docsRoot}/{DocsLayout.Standards}/{scope}";
        if (!_files.DirectoryExists(Absolute(folder))) return null;

        List<string> missing = [];
        List<string> changed = [];

        foreach (KyberStandardsTemplates.Part part in Enum.GetValues<KyberStandardsTemplates.Part>())
        {
            string fileName = KyberStandardsTemplates.FileName(scope, part);
            string hostPath = $"{folder}/{fileName}";

            if (!_templates.TryRenderPart(
                    scope, part, "unassigned", "2026-01-01", out string? template))
            {
                continue;
            }

            if (!_files.FileExists(Absolute(hostPath)))
            {
                missing.Add(fileName);
                continue;
            }

            string host = Normalize(_files.ReadAllText(Absolute(hostPath)));
            if (!string.Equals(host, Normalize(template), StringComparison.Ordinal))
            {
                changed.Add(fileName);
            }
        }

        HashSet<string> templateNames = Enum.GetValues<KyberStandardsTemplates.Part>()
            .Select(part => KyberStandardsTemplates.FileName(scope, part))
            .ToHashSet(StringComparer.Ordinal);

        List<string> hostOnly = _files
            .EnumerateFiles(Absolute(folder), "*", recursive: false)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(file => !templateNames.Contains(file))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();

        return new StandardsFolderDiff(scope, docsRoot, missing, hostOnly, changed);
    }

    /// <summary>
    /// Drops the three host-owned keys from the frontmatter block only. A body line that happens
    /// to start with one of them is authored content: scoping the strip to the block is what
    /// keeps an edit to that line visible as a change.
    /// </summary>
    /// <remarks>
    /// The block is a frontmatter block only when the file's first line opens it. A <c>---</c>
    /// further down is a horizontal rule or a YAML example the author wrote; opening a block there
    /// would drop every line beneath it from the comparison, hiding an edit to one of them.
    /// </remarks>
    internal static string Normalize(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string> kept = [];
        bool frontmatterOpen = false;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            bool delimiter = line.Trim() == "---";

            if (index == 0 && delimiter)
            {
                frontmatterOpen = true;
                kept.Add(line);
                continue;
            }

            if (frontmatterOpen && delimiter)
            {
                frontmatterOpen = false;
                kept.Add(line);
                continue;
            }

            if (frontmatterOpen
                && HostOwnedKeys.Any(key => line.StartsWith(key, StringComparison.Ordinal)))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept).TrimEnd();
    }

    private string Absolute(string relativePath) =>
        Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
