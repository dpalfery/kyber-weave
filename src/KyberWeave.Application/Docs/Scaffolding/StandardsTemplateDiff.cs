using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>How one installed standards folder differs from the current template.</summary>
/// <param name="Name">The folder's technology, role or platform name.</param>
/// <param name="Missing">Template files the host folder lacks.</param>
/// <param name="HostOnly">Files the host folder has that the template does not.</param>
/// <param name="Changed">Files present in both whose content differs.</param>
public sealed record StandardsFolderDiff(
    string Name,
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

    private readonly OntologyConfig _config;
    private readonly string _repoRoot;

    /// <summary>Creates the diff over a repository and its ontology.</summary>
    public StandardsTemplateDiff(string repoRoot, OntologyConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        _repoRoot = Path.GetFullPath(repoRoot);
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
            StandardsFolderDiff? diff = Compare(scope);
            if (diff is not null) diffs.Add(diff);
        }

        return new StandardsDiffResult(diffs);
    }

    private StandardsFolderDiff? Compare(string scope)
    {
        string folder = $"{_config.DocsRoot}/{DocsLayout.Standards}/{scope}";
        if (!Directory.Exists(Absolute(folder))) return null;

        List<string> missing = [];
        List<string> changed = [];

        foreach (KyberStandardsTemplates.Part part in Enum.GetValues<KyberStandardsTemplates.Part>())
        {
            string fileName = KyberStandardsTemplates.FileName(scope, part);
            string hostPath = $"{folder}/{fileName}";

            if (!KyberStandardsTemplates.TryRenderPart(
                    scope, part, "unassigned", "2026-01-01", out string? template))
            {
                continue;
            }

            if (!File.Exists(Absolute(hostPath)))
            {
                missing.Add(fileName);
                continue;
            }

            string host = Normalize(File.ReadAllText(Absolute(hostPath)));
            if (!string.Equals(host, Normalize(template), StringComparison.Ordinal))
            {
                changed.Add(fileName);
            }
        }

        HashSet<string> templateNames = Enum.GetValues<KyberStandardsTemplates.Part>()
            .Select(part => KyberStandardsTemplates.FileName(scope, part))
            .ToHashSet(StringComparer.Ordinal);

        List<string> hostOnly = Directory
            .EnumerateFiles(Absolute(folder))
            .Select(Path.GetFileName)
            .Where(file => file is not null && !templateNames.Contains(file))
            .Select(file => file!)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();

        return new StandardsFolderDiff(scope, missing, hostOnly, changed);
    }

    /// <summary>
    /// Drops the three host-owned keys from the frontmatter block only. A body line that happens
    /// to start with one of them is authored content: scoping the strip to the block is what
    /// keeps an edit to that line visible as a change.
    /// </summary>
    private static string Normalize(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string> kept = [];
        bool frontmatterOpen = false;
        bool frontmatterSeen = false;

        foreach (string line in lines)
        {
            bool delimiter = line.Trim() == "---";
            if (!frontmatterSeen && delimiter)
            {
                frontmatterSeen = true;
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
