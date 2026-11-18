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
                List<StandardSectionMove> plannedMoves = [];
                if (standardText is not null
                    && StandardShape.Reshape(standardText, DocsLayout.StandardHeadings) is { } shape)
                {
                    reshapedText = shape.Text;
                    planned.Add(new StandardsFolderChange(
                        StandardsFolderChangeKind.Reshape, standard, standard));
                    plannedMoves.AddRange(shape.Moves.Select(move =>
                        new StandardSectionMove(standard, move.Heading, move.Under)));
                }

                // Only what the migration actually did is reported: a change whose target another
                // actor created between planning and applying is skipped, and claiming it would
                // tell the operator about a write that never happened.
                List<StandardsFolderChange> applied = dryRun
                    ? planned
                    : Apply(planned, scope, owner, reshapedText);

                changes.AddRange(applied);
                if (applied.Any(change => change.Kind == StandardsFolderChangeKind.Reshape))
                {
                    moves.AddRange(plannedMoves);
                }
            }
        }

        return new StandardsFolderMigrationResult(changes, dryRun, moves);
    }

    /// <summary>
    /// Applies the planned changes for one folder, answering the changes it actually made.
    /// Called per folder so a failure leaves the nodes before it complete and rerunnable: every
    /// write is a create, and a rename whose target already exists is skipped rather than reported.
    /// </summary>
    internal List<StandardsFolderChange> Apply(
        IReadOnlyList<StandardsFolderChange> planned,
        string scope,
        string? owner,
        string? reshapedText)
    {
        List<StandardsFolderChange> applied = [];
        HashSet<string> skippedRenames = new(StringComparer.Ordinal);

        foreach (StandardsFolderChange change in planned)
        {
            switch (change.Kind)
            {
                case StandardsFolderChangeKind.Rename:
                    if (!TryRenameNew(change.From, change.To))
                    {
                        skippedRenames.Add(change.To);
                        break;
                    }

                    applied.Add(change);
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
                    if (TryCreateNew(target, new UTF8Encoding(false).GetBytes(content))) applied.Add(change);
                    break;

                case StandardsFolderChangeKind.Reshape:
                    // The reshape's text was derived from the legacy file. When the rename was
                    // skipped, the target now holds another actor's standard, and writing the
                    // reshape over it would replace that standard with the legacy text.
                    if (reshapedText is null || skippedRenames.Contains(change.To)) break;

                    AtomicFile.WriteAllText(RequireWritable(change.To), reshapedText);
                    applied.Add(change);
                    break;
            }
        }

        return applied;
    }

    /// <summary>
    /// Gives the file <paramref name="from"/> the name <paramref name="to"/>, answering false when
    /// something already sits at <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// The destination is created before the source is removed. Creation is one call that refuses
    /// an existing path instead of replacing it, so what is at the destination is decided by the
    /// write itself rather than by a check a later write could contradict: the same "create, never
    /// replace" rule every other write here follows, applied to a rename. Copying rather than
    /// moving keeps the bytes identical and leaves the source in place if the create fails, so a
    /// refused rename loses nothing; the cost is that a crash between the two calls leaves both
    /// names holding the standard, which the next run reports rather than resolves.
    /// </remarks>
    private bool TryRenameNew(string from, string to)
    {
        string source = RequireWritable(from);
        string target = RequireWritable(to);
        byte[] content = File.ReadAllBytes(source);
        if (!TryCreateNew(target, content)) return false;

        File.Delete(source);
        return true;
    }

    /// <summary>
    /// Writes a new file and never replaces one, reporting whether the file was created. A
    /// target that appeared since the folder was planned makes the change a no-op, so a second
    /// actor's file is left byte-for-byte intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the final move can be a skip, because only the move fails for the reason a skip
    /// means: a second actor created the target in the meantime. Writing the temporary is this
    /// tool's own I/O, and a failure there — a full disk, a name the platform refuses — is an
    /// error the operator has to see, never a silent "someone else created it".
    /// </para>
    /// <para>
    /// The containment check ran before this call and cannot be atomic with it, so the created
    /// file's own location is what the guarantee rests on: a path that started resolving outside
    /// the root in between shows up in the re-check, and the file is removed instead of left
    /// there. <see cref="RequireWritable(string)"/> has already refused a link at the target; the
    /// re-check is what covers a directory replaced by one after that refusal.
    /// </para>
    /// </remarks>
    private bool TryCreateNew(string path, byte[] content)
    {
        string directory = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
            }

            try
            {
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }

            VerifyStillWritable(path);
            return true;
        }
        finally
        {
            Discard(temporary);
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
    /// A standards folder is host-owned, and a host may keep a file in it behind a link — a
    /// standard shared with a sibling repository, a stub pointing at a template. Every write here
    /// either replaces a file or creates one, and neither is what the host asked for at the end of
    /// a link: a replace cuts the link and leaves its target behind, a create beside it hides
    /// which copy is authoritative. So a link anywhere on the path is refused, wherever it leads,
    /// and the folder is reported instead. Refusing every link rather than only those that leave
    /// the root also keeps the rule one sentence long, which is what makes it predictable.
    /// </remarks>
    private string RequireWritable(string relativePath) => RequireWritable(Absolute(relativePath), relativePath);

    /// <inheritdoc cref="RequireWritable(string)" path="/summary"/>
    /// <param name="fullPath">The absolute path to check.</param>
    /// <param name="named">How a refusal names the path: the repository-relative path the caller asked for.</param>
    private string RequireWritable(string fullPath, string named)
    {
        string boundary = _repoRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(boundary, StringComparison.Ordinal))
        {
            throw new IOException(
                $"Refusing to write '{named}': it resolves outside the repository root, so nothing was written.");
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

            throw new IOException(
                $"Refusing to write '{named}': '{current}' is the symbolic link " +
                $"'{entry.LinkTarget}', and a migration does not write through or replace a link, " +
                "so nothing was written.");
        }

        return full;
    }

    /// <summary>
    /// Checks again where a file that was just created actually resolved, removing it if the path
    /// stopped being inside the repository while it was written.
    /// </summary>
    /// <remarks>
    /// The pre-write check and the write cannot be one operation in .NET, so the guarantee cannot
    /// rest on the check's timing. It rests on this: the write's result is inspected, and a path
    /// that now resolves outside the root — a directory replaced by a link in between — costs the
    /// file, not the repository's boundary.
    /// </remarks>
    private void VerifyStillWritable(string path)
    {
        try
        {
            RequireWritable(path, path);
        }
        catch (IOException exception)
        {
            Discard(path);
            throw new IOException(
                $"Refusing to keep '{path}': it stopped resolving inside the repository root while it was " +
                "written, so the file it was written to was removed.",
                exception);
        }
    }

    /// <summary>Deletes a file, swallowing a cleanup failure that would take the real error's place.</summary>
    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the refusal this supports is the failure the caller has to see.
        }
    }

    [GeneratedRegex(@"^owner:\s*(?<owner>\S.*)$", RegexOptions.Multiline)]
    private static partial Regex OwnerLine();
}
