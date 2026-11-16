using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>What a standards-folder migration does to one file.</summary>
public enum StandardsFolderChangeKind
{
    /// <summary>The folder's legacy <c>README.md</c> becomes its prefixed standard.</summary>
    Rename,

    /// <summary>A file the folder was missing is written as a stub.</summary>
    Add
}

/// <summary>One planned or written change inside a standards folder.</summary>
/// <param name="Kind">Rename or add.</param>
/// <param name="From">The existing path for a rename; empty for an add.</param>
/// <param name="To">The path the change produces.</param>
public sealed record StandardsFolderChange(StandardsFolderChangeKind Kind, string From, string To);

/// <summary>The changes a migration planned, and whether it wrote them.</summary>
/// <param name="Changes">Repository-relative paths, in the order they are applied.</param>
/// <param name="DryRun">True when the caller asked for the plan only.</param>
public sealed record StandardsFolderMigrationResult(
    IReadOnlyList<StandardsFolderChange> Changes,
    bool DryRun);

/// <summary>
/// Renames a host's standards files to their prefixed names and writes the four files a
/// technology or role folder is required to hold.
/// </summary>
/// <remarks>
/// A rename never changes a byte: the standard's own text is the host's, and the only job is
/// to give it the name the registry and the folder check expect. A missing file is written
/// as a stub whose frontmatter states which scope it belongs to and whose frontmatter's
/// owner is copied from the folder's standard, so the corpus stays within its catalog
/// vocabulary. Running it twice changes nothing.
/// </remarks>
public sealed partial class StandardsFolderMigration
{
    /// <summary>The command a caller runs to reach this migration.</summary>
    public const string Command = "kyber-weave docs standards migrate";

    private readonly OntologyConfig _config;
    private readonly string _repoRoot;
    private readonly TimeProvider _time;

    /// <summary>Creates the migration over a repository root and its ontology.</summary>
    /// <param name="repoRoot">The repository whose standards folders are migrated.</param>
    /// <param name="config">The ontology that declares the technologies and roles.</param>
    /// <param name="time">Supplies the date a written stub carries.</param>
    public StandardsFolderMigration(string repoRoot, OntologyConfig config, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Plans the migration without writing anything.</summary>
    public IReadOnlyList<StandardsFolderChange> Plan() => Run(dryRun: true).Changes;

    /// <summary>Plans and, unless <paramref name="dryRun"/>, writes the migration.</summary>
    public StandardsFolderMigrationResult Run(bool dryRun = false)
    {
        List<StandardsFolderChange> changes = [];

        foreach (string scope in Scopes())
        {
            foreach (string docsRoot in _config.DocsRoots)
            {
                string folder = $"{docsRoot}/{DocsLayout.Standards}/{scope}";
                if (!Directory.Exists(Absolute(folder))) continue;

                string standard = DocsLayout.StandardFile(docsRoot, scope);
                string legacy = $"{folder}/README.md";
                string? standardText = null;
                List<StandardsFolderChange> planned = [];

                if (!File.Exists(Absolute(standard)))
                {
                    if (File.Exists(Absolute(legacy)))
                    {
                        planned.Add(new StandardsFolderChange(
                            StandardsFolderChangeKind.Rename, legacy, standard));
                        standardText = File.ReadAllText(Absolute(legacy));
                    }
                    else
                    {
                        standardText = Stub(scope, "standard", OwnerOf(null));
                        planned.Add(new StandardsFolderChange(
                            StandardsFolderChangeKind.Add, string.Empty, standard));
                    }
                }

                string? owner = OwnerOf(standardText ?? ReadOrNull(standard));

                foreach ((string path, string _) in new[]
                         {
                             (DocsLayout.RulesFile(docsRoot, scope), "rules"),
                             (DocsLayout.SkillsFile(docsRoot, scope), "skills")
                         })
                {
                    if (File.Exists(Absolute(path))) continue;
                    planned.Add(new StandardsFolderChange(
                        StandardsFolderChangeKind.Add, string.Empty, path));
                }

                string arbiter = DocsLayout.ArbiterRulesFile(docsRoot, scope);
                if (!File.Exists(Absolute(arbiter)))
                {
                    planned.Add(new StandardsFolderChange(
                        StandardsFolderChangeKind.Add, string.Empty, arbiter));
                }

                changes.AddRange(planned);
                if (!dryRun) Apply(planned, scope, owner);
            }
        }

        return new StandardsFolderMigrationResult(changes, dryRun);
    }

    /// <summary>
    /// Applies the planned changes for one folder. Called per folder so a failure leaves the
    /// nodes before it complete and rerunnable: every write is a create, and the rename is
    /// skipped when its target already exists.
    /// </summary>
    private void Apply(
        IReadOnlyList<StandardsFolderChange> planned,
        string scope,
        string? owner)
    {
        foreach (StandardsFolderChange change in planned)
        {
            switch (change.Kind)
            {
                case StandardsFolderChangeKind.Rename:
                    if (!File.Exists(Absolute(change.To)))
                        File.Move(Absolute(change.From), Absolute(change.To));
                    break;

                case StandardsFolderChangeKind.Add:
                    string fileName = Path.GetFileName(change.To);
                    string kind = fileName.EndsWith(DocsLayout.ArbiterRulesSuffix, StringComparison.Ordinal)
                        ? "arbiter"
                        : fileName.EndsWith(DocsLayout.SkillsSuffix, StringComparison.Ordinal)
                            ? "skills"
                            : fileName.EndsWith(DocsLayout.RulesSuffix, StringComparison.Ordinal)
                                ? "rules"
                                : "standard";

                    string content = kind == "arbiter"
                        ? ArbiterRulesStub(scope)
                        : Stub(scope, kind, owner);
                    File.WriteAllText(Absolute(change.To), content, new UTF8Encoding(false));
                    break;
            }
        }
    }

    private IEnumerable<string> Scopes() =>
        _config.Technologies.Concat(_config.Roles).Distinct(StringComparer.Ordinal);

    private string Stub(string scope, string kind, string? owner)
    {
        string ownerLine = owner ?? "unassigned";
        string today = _time.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string docType = kind switch
        {
            "standard" => "coding-standard",
            "rules" => "rule",
            _ => "skill"
        };
        string scopeKey = _config.Roles.Contains(scope, StringComparer.Ordinal)
            ? "role"
            : "technology";
        string title = kind == "standard" ? $"{scope} standard" : $"{scope} {kind}";

        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(CultureInfo.InvariantCulture, $"id: standards/{scope}");
        if (kind != "standard") builder.Append(CultureInfo.InvariantCulture, $"/{kind}");
        builder.Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"title: {title}\n");
        builder.Append(CultureInfo.InvariantCulture, $"doc-type: {docType}\n");
        builder.Append("status: draft\n");
        builder.Append(CultureInfo.InvariantCulture, $"{scopeKey}: {scope}\n");
        builder.Append(CultureInfo.InvariantCulture, $"owner: {ownerLine}\n");
        builder.Append(CultureInfo.InvariantCulture, $"last-reviewed: {today}\n");
        builder.Append("---\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"# {title}\n");

        if (kind != "standard") return builder.ToString();

        builder.Append('\n');
        foreach (string heading in DocsLayout.StandardHeadings)
        {
            builder.Append(CultureInfo.InvariantCulture, $"## {heading}\n\n");
        }

        return builder.ToString();
    }

    private static string ArbiterRulesStub(string scope) =>
        $"# Machine rules the Arbiter evaluates for the '{scope}' folder.\n" +
        "# An empty list is valid: most folders declare none.\n" +
        "rules: []\n";

    /// <summary>
    /// Reads the owner a folder already stated, so a generated stub names a catalog owner in
    /// the host's own vocabulary rather than one this tool invented.
    /// </summary>
    private static string? OwnerOf(string? standardText)
    {
        if (string.IsNullOrEmpty(standardText)) return null;

        Match match = OwnerLine().Match(standardText);
        return match.Success ? match.Groups["owner"].Value.Trim() : null;
    }

    private string? ReadOrNull(string relativePath)
    {
        string full = Absolute(relativePath);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    private string Absolute(string relativePath) =>
        Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    [GeneratedRegex(@"^owner:\s*(?<owner>\S.*)$", RegexOptions.Multiline)]
    private static partial Regex OwnerLine();
}
