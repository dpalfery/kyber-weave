using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>What a standards-folder migration does to one file.</summary>
public enum StandardsFolderChangeKind
{
    /// <summary>The folder's legacy <c>README.md</c> becomes its prefixed standard.</summary>
    Rename,

    /// <summary>A file the folder was missing is written as a stub.</summary>
    Add,

    /// <summary>The standard's own sections are placed under the six required headings.</summary>
    Reshape
}

/// <summary>One planned or written change inside a standards folder.</summary>
/// <param name="Kind">Rename or add.</param>
/// <param name="From">The existing path for a rename; empty for an add.</param>
/// <param name="To">The path the change produces.</param>
public sealed record StandardsFolderChange(StandardsFolderChangeKind Kind, string From, string To);

/// <summary>One authored section the migration moved under a required heading.</summary>
/// <param name="Standard">The repository-relative standard the move happened in.</param>
/// <param name="Heading">The authored heading, without the leading hashes.</param>
/// <param name="Under">The required heading the section now sits under.</param>
public sealed record StandardSectionMove(string Standard, string Heading, string Under);

/// <summary>The changes a migration planned, and whether it wrote them.</summary>
/// <param name="Changes">Repository-relative paths, in the order they are applied.</param>
/// <param name="DryRun">True when the caller asked for the plan only.</param>
/// <param name="Moves">The authored sections a reshape placed under a required heading.</param>
public sealed record StandardsFolderMigrationResult(
    IReadOnlyList<StandardsFolderChange> Changes,
    bool DryRun,
    IReadOnlyList<StandardSectionMove> Moves);

/// <summary>
/// Renames a host's standards files to their prefixed names and writes the four files a
/// technology or role folder is required to hold.
/// </summary>
/// <remarks>
/// A rename never changes a byte: the standard's own text is the host's, and the only job is
/// to give it the name the registry and the folder check expect. A standard whose own
/// headings predate the six the ontology requires is reshaped so those six appear in order,
/// with every authored section kept beneath the heading it fits, and each move is reported.
/// A missing file is written as a stub whose frontmatter states which scope it belongs to and
/// whose frontmatter's owner is copied from the folder's standard, so the corpus stays within
/// its catalog vocabulary. Running it twice changes nothing.
/// </remarks>
public sealed partial class StandardsFolderMigration
{
    /// <summary>The command a caller runs to reach this migration.</summary>
    public const string Command = DocsLayout.MigrateStandardsCommand;

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
        List<StandardSectionMove> moves = [];

        foreach (string scope in Scopes())
        {
            foreach (string docsRoot in _config.DocsRoots)
            {
                string folder = $"{docsRoot}/{DocsLayout.Standards}/{scope}";
                if (!Directory.Exists(Absolute(folder))) continue;

                string standard = DocsLayout.StandardFile(docsRoot, scope);
                string legacy = $"{folder}/README.md";
                string? standardText;
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
                else
                {
                    standardText = ReadOrNull(standard);
                }

                string? owner = OwnerOf(standardText);

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

                string? reshapedText = null;
                if (standardText is not null
                    && StandardShape.Reshape(standardText, DocsLayout.StandardHeadings) is { } shape)
                {
                    reshapedText = shape.Text;
                    planned.Add(new StandardsFolderChange(
                        StandardsFolderChangeKind.Reshape, standard, standard));
                    moves.AddRange(shape.Moves.Select(move =>
                        new StandardSectionMove(standard, move.Heading, move.Under)));
                }

                changes.AddRange(planned);
                if (!dryRun) Apply(planned, scope, owner, reshapedText);
            }
        }

        return new StandardsFolderMigrationResult(changes, dryRun, moves);
    }

    /// <summary>
    /// Applies the planned changes for one folder. Called per folder so a failure leaves the
    /// nodes before it complete and rerunnable: every write is a create, and the rename is
    /// skipped when its target already exists.
    /// </summary>
    private void Apply(
        IReadOnlyList<StandardsFolderChange> planned,
        string scope,
        string? owner,
        string? reshapedText)
    {
        foreach (StandardsFolderChange change in planned)
        {
            switch (change.Kind)
            {
                case StandardsFolderChangeKind.Rename:
                    if (!File.Exists(Absolute(change.To)))
                        File.Move(Absolute(change.From), Absolute(change.To));
                    break;

                case StandardsFolderChangeKind.Reshape:
                    if (reshapedText is not null)
                        AtomicFile.WriteAllText(RequireWritable(change.To), reshapedText);
                    break;

                case StandardsFolderChangeKind.Add:
                    string target = RequireWritable(change.To);
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
                    AtomicFile.WriteAllText(target, content);
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

    /// <summary>
    /// Reads a standard, distinguishing a file the folder does not hold from one it holds but
    /// cannot use. An absent file is null; an unreadable one is an error naming it, so a
    /// permission failure is never mistaken for a folder with no standard and answered with a stub.
    /// </summary>
    private string? ReadOrNull(string relativePath)
    {
        string full = Absolute(relativePath);
        if (!File.Exists(full)) return null;

        try
        {
            return File.ReadAllText(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"'{relativePath}' exists but could not be read: {exception.Message}", exception);
        }
    }

    private string Absolute(string relativePath) =>
        Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The full path a write may reach, refusing one that leaves the repository, whether by a
    /// rooted or <c>..</c> segment or through a symbolic link between here and the file.
    /// </summary>
    /// <remarks>
    /// A standards folder is host-owned, and a host may keep one behind a link. Following it would
    /// put this tool's stub outside the tree it was told to migrate, so the link is refused and the
    /// folder reported instead. Links that stay inside the root are left alone.
    /// </remarks>
    private string RequireWritable(string relativePath)
    {
        string boundary = _repoRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Absolute(relativePath));
        if (!full.StartsWith(boundary, StringComparison.Ordinal))
        {
            throw new IOException(
                $"Refusing to write '{relativePath}': it resolves outside the repository root, so nothing was written.");
        }

        string current = _repoRoot.TrimEnd(Path.DirectorySeparatorChar);
        foreach (string segment in full[boundary.Length..].Split(
                     Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (entry.LinkTarget is null) continue;

            string? resolved = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (resolved is null || !resolved.StartsWith(boundary, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Refusing to write '{relativePath}': it passes through the symbolic link '{current}', " +
                    "which leads outside the repository root, so nothing was written.");
            }
        }

        return full;
    }

    [GeneratedRegex(@"^owner:\s*(?<owner>\S.*)$", RegexOptions.Multiline)]
    private static partial Regex OwnerLine();
}
