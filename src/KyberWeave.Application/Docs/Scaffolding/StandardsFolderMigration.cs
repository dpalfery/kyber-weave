using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
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
    Reshape,

    /// <summary>
    /// An existing rules file gains the folder's <c>doc-type</c> and scope line,
    /// keeping every other line and the whole body.
    /// </summary>
    Frontmatter
}

/// <summary>One planned or written change inside a standards folder.</summary>
/// <param name="Kind">Rename, add, reshape or frontmatter repair.</param>
/// <param name="From">The existing path for a rename or repair; empty for an add.</param>
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
/// <param name="ConfigRegRefreshed">
/// True when the root <c>AGENTS.md</c> registry block was re-rendered because the migration
/// made it stale; in a dry run, true when it would have been.
/// </param>
public sealed record StandardsFolderMigrationResult(
    IReadOnlyList<StandardsFolderChange> Changes,
    bool DryRun,
    IReadOnlyList<StandardSectionMove> Moves,
    bool ConfigRegRefreshed = false);

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
/// its catalog vocabulary. A rules file that already exists but does not declare the
/// folder's <c>doc-type</c> and scope has those two frontmatter lines repaired in place, and
/// every other line and the whole body are kept byte for byte. A missing skills list is written as an empty
/// YAML list, and a legacy <c>.skills.md</c> is left in place. Running it twice changes nothing.
///
/// The rename also moves what each <c>&lt;technology&gt;-coding-standard</c> registry property
/// names, so the same run re-renders the generated block in the root <c>AGENTS.md</c>. Without
/// that, one command would leave the repository invalid: the block would publish paths the
/// rename removed, and only <c>docs init</c> would clear the finding.
/// </remarks>
public sealed partial class StandardsFolderMigration
{
    /// <summary>The command a caller runs to reach this migration.</summary>
    public const string Command = DocsLayout.MigrateStandardsCommand;

    /// <summary>
    /// The key under which an exception thrown by <see cref="Run"/> carries, in its <c>Data</c>, the undo steps that
    /// themselves failed while the run was rolled back. Absent when the rollback was complete.
    /// </summary>
    public const string RollbackFailuresKey = "kyber-weave.standards-migration.rollback-failures";

    /// <summary>The one file the configuration registry is rendered into.</summary>
    private const string AgentsFilePath = "AGENTS.md";

    private readonly IWritableFileSystem _files;
    private readonly IPathResolver _paths;
    private readonly IProcessEnvironment _environment;
    private readonly IIdGenerator _ids;
    private readonly KyberWeaveConfig _config;
    private readonly OntologyConfig _ontology;
    private readonly string _repoRoot;
    private readonly TimeProvider _time;

    /// <summary>Creates the migration over the seams it reads and writes through.</summary>
    /// <param name="files">Reads and writes the repository's files.</param>
    /// <param name="repoRoot">The repository whose standards folders are migrated.</param>
    /// <param name="config">
    /// The host configuration: its ontology declares the technologies and roles, and its
    /// registry settings render the block this type refreshes.
    /// </param>
    /// <param name="paths">Turns the repository root into a full path and checks a write's containment.</param>
    /// <param name="environment">Supplies the base directory a relative root is resolved against.</param>
    /// <param name="ids">Names the temporary file a stub is written through.</param>
    /// <param name="time">Supplies the date a written stub carries.</param>
    public StandardsFolderMigration(
        IWritableFileSystem files,
        string repoRoot,
        KyberWeaveConfig config,
        IPathResolver paths,
        IProcessEnvironment environment,
        IIdGenerator ids,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(ids);
        _files = files;
        _repoRoot = paths.GetFullPath(repoRoot, environment.CurrentDirectory);
        _config = config;
        _ontology = config.Ontology;
        _paths = paths;
        _environment = environment;
        _ids = ids;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Plans the migration without writing anything.</summary>
    public IReadOnlyList<StandardsFolderChange> Plan() => Run(dryRun: true).Changes;

    /// <summary>Plans and, unless <paramref name="dryRun"/>, writes the migration.</summary>
    /// <remarks>
    /// The registry block is rendered, and its write refused, before the first standards write.
    /// An unreadable, unclosed or linked <c>AGENTS.md</c> is then a refusal that leaves the
    /// repository as it was, rather than a failure reported after the standards moved.
    /// </remarks>
    public StandardsFolderMigrationResult Run(bool dryRun = false)
    {
        List<StandardsFolderChange> changes = [];
        List<StandardSectionMove> moves = [];
        string? refreshedAgents = PlanConfigRegRefresh();

        // A dry run reaches the same refusal a real run does: the registry block's target is
        // checked in both modes, and only the write is skipped when previewing.
        string? agentsTarget = refreshedAgents is not null
            ? RequireWritable(AgentsFilePath)
            : null;

        List<FolderWork> work = [];
        foreach (string scope in Scopes())
        {
            foreach (string docsRoot in _ontology.DocsRoots)
            {
                string folder = $"{docsRoot}/{DocsLayout.Standards}/{scope}";
                if (!_files.DirectoryExists(Absolute(folder))) continue;

                string standard = DocsLayout.StandardFile(docsRoot, scope);
                string legacy = $"{folder}/README.md";
                string? standardText;
                List<StandardsFolderChange> planned = [];

                if (!_files.FileExists(Absolute(standard)))
                {
                    if (_files.FileExists(Absolute(legacy)))
                    {
                        planned.Add(new StandardsFolderChange(
                            StandardsFolderChangeKind.Rename, legacy, standard));
                        standardText = _files.ReadAllText(Absolute(legacy));
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

                Dictionary<string, string> repairs = new(StringComparer.Ordinal);
                string rules = DocsLayout.RulesFile(docsRoot, scope);
                if (!_files.FileExists(Absolute(rules)))
                {
                    planned.Add(new StandardsFolderChange(
                        StandardsFolderChangeKind.Add, string.Empty, rules));
                }
                else if (RepairFrontmatter(rules, scope, "rule") is { } repaired)
                {
                    // A file the folder already holds is still repairable: without the folder's
                    // doc-type and scope line it exists but never reaches a task prompt.
                    repairs[rules] = repaired;
                    planned.Add(new StandardsFolderChange(
                        StandardsFolderChangeKind.Frontmatter, rules, rules));
                }

                // The skills list is a bare YAML list with no frontmatter to repair. A legacy
                // <scope>.skills.md beside it is never read, repaired or removed here: the
                // KW-DOC-SKILL-005 warning is what asks the operator to retire it.
                string skillsList = DocsLayout.SkillsListFile(docsRoot, scope);
                if (!_files.FileExists(Absolute(skillsList)))
                {
                    planned.Add(new StandardsFolderChange(
                        StandardsFolderChangeKind.Add, string.Empty, skillsList));
                }

                string arbiter = DocsLayout.ArbiterRulesFile(docsRoot, scope);
                if (!_files.FileExists(Absolute(arbiter)))
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

                work.Add(new FolderWork(scope, owner, reshapedText, repairs, planned, plannedMoves));
            }
        }

        // Every target the run would write is checked before the first write, so a refusal — a
        // link, a path outside the repository — leaves no standards moved and no stale registry
        // block behind. A dry run checks the same targets: it writes nothing, and previewing a
        // plan the real run would refuse is a false answer.
        foreach (FolderWork item in work)
        {
            RequireWritableTargets(item.Planned, item.ReshapedText);
        }

        List<Action> undo = [];
        try
        {
            foreach (FolderWork item in work)
            {
                // Only what the migration actually did is reported: a change whose target another
                // actor created between planning and applying is skipped, and claiming it would
                // tell the operator about a write that never happened.
                List<StandardsFolderChange> applied = dryRun
                    ? [.. item.Planned]
                    : Apply(item.Planned, item.Scope, item.Owner, item.ReshapedText, undo, item.Repairs);

                changes.AddRange(applied);
                if (applied.Any(change => change.Kind == StandardsFolderChangeKind.Reshape))
                {
                    moves.AddRange(item.Moves);
                }
            }

            if (!dryRun && agentsTarget is not null && refreshedAgents is not null)
            {
                string? agentsBefore = ReadOrNull(AgentsFilePath);
                _files.WriteAllTextAtomic(agentsTarget, refreshedAgents);
                undo.Add(() => RestoreAgents(agentsTarget, agentsBefore));
                VerifyStillWritable(agentsTarget);
            }
        }
        catch (Exception original)
        {
            // A failure part way through — a write that fails, not a target refused at planning —
            // would otherwise leave some standards moved and the registry block stale. Undo the
            // writes in reverse so the repository is exactly as it was.
            List<string> failed = RollBack(undo);
            if (failed.Count > 0)
            {
                // The original keeps its type, so a caller that handles it still does and a cancellation
                // stays a cancellation. Undo failures ride along on it, for the caller to report beside it.
                original.Data[RollbackFailuresKey] = string.Join("; ", failed);
            }

            throw;
        }

        return new StandardsFolderMigrationResult(
            changes, dryRun, moves, ConfigRegRefreshed: refreshedAgents is not null);
    }

    /// <summary>
    /// The root <c>AGENTS.md</c> with its generated registry block re-rendered, or null when
    /// the file has no block to refresh or the block already matches configuration.
    /// </summary>
    /// <remarks>
    /// Only the region between the markers is rewritten, and only where the repository already
    /// has one: a standards migration does not hand a registry to a repository that never
    /// adopted one, which is the same line <c>docs validate</c> draws. Everything outside the
    /// markers, and <c>.kyber-weave/kyber-weave.yml</c> itself, is left alone.
    /// </remarks>
    private string? PlanConfigRegRefresh()
    {
        string existing = ReadOrNull(AgentsFilePath) ?? string.Empty;
        if (!ConfigRegMarkdown.ContainsBlock(existing)) return null;

        string block = ConfigRegMarkdown.Render(
            _config.ConfigReg.Resolve(_ontology), _config.ConfigReg.Autonomy);
        string updated = ConfigRegMarkdown.Splice(existing, block);

        return string.Equals(existing, updated, StringComparison.Ordinal) ? null : updated;
    }

    /// <summary>One standards folder's plan, held until every folder's targets are checked.</summary>
    /// <param name="Scope">The technology or role scope.</param>
    /// <param name="Owner">The owner the stubs copy, or null.</param>
    /// <param name="ReshapedText">The reshaped standard, or null when it needs no reshape.</param>
    /// <param name="Repairs">Each rules file's repaired frontmatter, keyed by its path.</param>
    /// <param name="Planned">The changes the folder needs.</param>
    /// <param name="Moves">The sections a reshape places under required headings.</param>
    private sealed record FolderWork(
        string Scope,
        string? Owner,
        string? ReshapedText,
        IReadOnlyDictionary<string, string> Repairs,
        List<StandardsFolderChange> Planned,
        List<StandardSectionMove> Moves);

    /// <summary>Checks every target a folder's plan would write, without writing anything.</summary>
    /// <param name="planned">The folder's planned changes.</param>
    /// <param name="reshapedText">The reshaped standard, or null when the plan has no reshape.</param>
    private void RequireWritableTargets(
        IReadOnlyList<StandardsFolderChange> planned,
        string? reshapedText)
    {
        foreach (StandardsFolderChange change in planned)
        {
            switch (change.Kind)
            {
                case StandardsFolderChangeKind.Rename:
                    RequireWritable(change.From);
                    RequireWritable(change.To);
                    break;
                case StandardsFolderChangeKind.Add:
                    RequireWritable(change.To);
                    break;
                case StandardsFolderChangeKind.Reshape when reshapedText is not null:
                    RequireWritable(change.To);
                    break;
                case StandardsFolderChangeKind.Frontmatter:
                    RequireWritable(change.To);
                    break;
            }
        }
    }

    /// <summary>
    /// Applies the planned changes for one folder, answering the changes it actually made.
    /// Called per folder so a failure can be undone newest-first by the caller: every write is a
    /// create, and a rename whose target already exists is skipped rather than reported.
    /// </summary>
    /// <param name="planned">The folder's planned changes.</param>
    /// <param name="scope">The technology or role scope.</param>
    /// <param name="owner">The owner the stubs copy, or null.</param>
    /// <param name="reshapedText">The reshaped standard, or null when the plan has no reshape.</param>
    /// <param name="undo">Receives each write's inverse, for the caller to run if a later folder fails.</param>
    /// <param name="repairs">Each rules file's repaired frontmatter, keyed by its path.</param>
    /// <returns>The changes actually made.</returns>
    internal List<StandardsFolderChange> Apply(
        IReadOnlyList<StandardsFolderChange> planned,
        string scope,
        string? owner,
        string? reshapedText,
        List<Action>? undo = null,
        IReadOnlyDictionary<string, string>? repairs = null)
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
                    undo?.Add(() => MoveBack(change.To, change.From));
                    break;

                case StandardsFolderChangeKind.Add:
                    string target = RequireWritable(change.To);
                    string fileName = Path.GetFileName(change.To);
                    string kind = fileName.EndsWith(DocsLayout.ArbiterRulesSuffix, StringComparison.Ordinal)
                        ? "arbiter"
                        : fileName.EndsWith(DocsLayout.SkillsListSuffix, StringComparison.Ordinal)
                            ? "skills"
                            : fileName.EndsWith(DocsLayout.RulesSuffix, StringComparison.Ordinal)
                                ? "rules"
                                : "standard";

                    string content = kind switch
                    {
                        "arbiter" => ArbiterRulesStub(scope),
                        "skills" => DocsScaffoldTemplates.SkillsList(),
                        _ => Stub(scope, kind, owner)
                    };
                    if (TryCreateNew(target, new UTF8Encoding(false).GetBytes(content)))
                    {
                        applied.Add(change);
                        undo?.Add(() => DeleteIfPresent(change.To));
                    }

                    break;

                case StandardsFolderChangeKind.Reshape:
                    // The reshape's text was derived from the legacy file. When the rename was
                    // skipped, the target now holds another actor's standard, and writing the
                    // reshape over it would replace that standard with the legacy text.
                    if (reshapedText is null || skippedRenames.Contains(change.To)) break;

                    string reshapeTarget = RequireWritable(change.To);
                    string reshapeBefore = _files.ReadAllText(reshapeTarget);
                    _files.WriteAllTextAtomic(reshapeTarget, reshapedText);
                    undo?.Add(() =>
                    {
                        string undoPath = RequireWritable(change.To);
                        _files.WriteAllTextAtomic(undoPath, reshapeBefore);
                        VerifyStillWritable(undoPath);
                    });
                    VerifyStillWritable(reshapeTarget);
                    applied.Add(change);
                    break;

                case StandardsFolderChangeKind.Frontmatter:
                    if (repairs is null || !repairs.TryGetValue(change.To, out string? repairedText)) break;

                    string repairTarget = RequireWritable(change.To);
                    string repairBefore = _files.ReadAllText(repairTarget);
                    _files.WriteAllTextAtomic(repairTarget, repairedText);
                    undo?.Add(() =>
                    {
                        string undoPath = RequireWritable(change.To);
                        _files.WriteAllTextAtomic(undoPath, repairBefore);
                        VerifyStillWritable(undoPath);
                    });
                    VerifyStillWritable(repairTarget);
                    applied.Add(change);
                    break;
            }
        }

        return applied;
    }

    /// <summary>Moves a renamed standard back to the name it had before a failed run.</summary>
    /// <param name="from">The relative path the rename produced.</param>
    /// <param name="to">The relative path the standard had before.</param>
    private void MoveBack(string from, string to)
    {
        // The same containment and link checks the forward write made: an undo is still a write.
        string source = RequireWritable(from);
        string target = RequireWritable(to);
        if (_files.FileExists(source) && !_files.FileExists(target))
        {
            _files.MoveFile(source, target, overwrite: false);
        }
    }

    /// <summary>Deletes a stub a failed run wrote.</summary>
    /// <param name="relativePath">The stub's repository-relative path.</param>
    private void DeleteIfPresent(string relativePath)
    {
        string path = RequireWritable(relativePath);
        if (_files.FileExists(path))
        {
            _files.DeleteFile(path);
        }
    }

    /// <summary>Puts the root <c>AGENTS.md</c> back as it was before a failed run.</summary>
    /// <param name="path">The file's absolute path.</param>
    /// <param name="previous">Its text before the run, or null when it did not exist.</param>
    private void RestoreAgents(string path, string? previous)
    {
        RequireWritable(path, path);
        if (previous is null)
        {
            if (_files.FileExists(path))
            {
                _files.DeleteFile(path);
            }

            return;
        }

        _files.WriteAllTextAtomic(path, previous);
    }

    /// <summary>Undoes a failed run's writes in the reverse order they were made.</summary>
    /// <param name="undo">Each write's inverse, in the order the writes happened.</param>
    /// <returns>One line per undo that itself failed, so the caller can report them with the original error.</returns>
    private static List<string> RollBack(List<Action> undo)
    {
        List<string> failed = [];
        for (int index = undo.Count - 1; index >= 0; index--)
        {
            try
            {
                undo[index]();
            }
            catch (Exception exception) when (FileFailure.Is(exception))
            {
                // Rollback is best effort: every undo runs even when an earlier one failed, and a
                // failure is reported beside the original error rather than swallowed.
                failed.Add($"{exception.GetType().Name}: {exception.Message}");
            }
        }

        return failed;
    }

    /// <summary>
    /// Gives the file <paramref name="from"/> the name <paramref name="to"/>, answering false when
    /// something already sits at <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// One move, not a copy and a delete. The file keeps its bytes, its mode and its identity, and
    /// there is no moment at which both names hold the standard: a copy followed by a delete would
    /// leave two standards for one scope if the delete never happened, and the next run plans a
    /// rename only when the prefixed name is missing, so nothing would report it. A destination
    /// that already exists is answered before the move rather than by failing it, so the move's
    /// own failure is never read as "someone else got there first"; a destination that appears in
    /// between is the one case the move's failure means it.
    /// </remarks>
    private bool TryRenameNew(string from, string to)
    {
        string source = RequireWritable(from);
        string target = RequireWritable(to);
        if (_files.FileExists(target)) return false;

        try
        {
            _files.MoveFile(source, target, overwrite: false);
        }
        catch (IOException) when (_files.FileExists(target))
        {
            return false;
        }

        VerifyStillWritable(target);
        return true;
    }

    /// <summary>
    /// Writes a new file and never replaces one, reporting whether the file was created. A
    /// target that appeared since the folder was planned makes the change a no-op, so a second
    /// actor's file is left byte-for-byte intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A target that already holds a file is answered before a temporary is written: the change is
    /// a no-op, not an attempt. That keeps the move's own failure readable — with the target absent
    /// at this point, a move that fails while a file exists afterwards is the file appearing in
    /// between, and nothing else.
    /// </para>
    /// <para>
    /// Writing the temporary is this tool's own I/O, and a failure there — a full disk, a name the
    /// platform refuses — is an error the operator has to see, never a silent "someone else created
    /// it", so it is outside the filter entirely.
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
        if (_files.FileExists(path)) return false;

        string directory = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{_ids.NewGuid():N}.tmp");

        try
        {
            using (Stream stream = _files.OpenWriteNew(temporary))
            {
                stream.Write(content);
            }

            try
            {
                _files.MoveFile(temporary, path, overwrite: false);
            }
            catch (IOException) when (_files.FileExists(path))
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
        _ontology.Technologies.Concat(_ontology.Roles).Distinct(StringComparer.Ordinal);

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
        string scopeKey = DocsLayout.ScopeKey(_ontology.Roles, scope);
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

    /// <summary>
    /// The file's text with its frontmatter's <c>doc-type</c> and scope line set to what the
    /// folder requires, or null when both already say it.
    /// </summary>
    /// <remarks>
    /// Only those two lines change. A missing line is inserted at the end of the frontmatter, and
    /// every other line — the frontmatter's and the whole body's — is kept byte for byte, including
    /// the file's own line endings. A file with no closed frontmatter block is left alone: there is
    /// no block this method could safely edit, and the validator's frontmatter finding owns it.
    /// </remarks>
    /// <param name="relativePath">The file's repository-relative path, used only to read it.</param>
    /// <param name="scope">The technology or role whose folder holds it.</param>
    /// <param name="docType">The file's required <c>doc-type</c>.</param>
    private string? RepairFrontmatter(string relativePath, string scope, string docType)
    {
        string? text = ReadOrNull(relativePath);
        if (string.IsNullOrEmpty(text)) return null;

        string[] lines = text.Split('\n');
        if (lines.Length < 2 || lines[0].TrimEnd('\r') != "---") return null;

        int close = -1;
        for (int index = 1; index < lines.Length; index++)
        {
            if (lines[index].TrimEnd('\r') == "---")
            {
                close = index;
                break;
            }
        }

        if (close < 0) return null;

        string scopeKey = DocsLayout.ScopeKey(_ontology.Roles, scope);
        string carriageReturn = text.Contains("\r\n", StringComparison.Ordinal) ? "\r" : string.Empty;
        string expectedDocType = $"doc-type: {docType}";
        string expectedScope = $"{scopeKey}: {scope}";

        int docTypeLine = -1;
        int scopeLine = -1;
        for (int index = 1; index < close; index++)
        {
            string bare = lines[index].TrimEnd('\r');
            if (bare.StartsWith("doc-type:", StringComparison.Ordinal)) docTypeLine = index;
            else if (bare.StartsWith(scopeKey + ":", StringComparison.Ordinal)) scopeLine = index;
        }

        bool changed = false;
        List<string> missing = [];
        if (docTypeLine < 0)
        {
            missing.Add(expectedDocType + carriageReturn);
            changed = true;
        }
        else if (!string.Equals(lines[docTypeLine].TrimEnd(), expectedDocType, StringComparison.Ordinal))
        {
            lines[docTypeLine] = expectedDocType + carriageReturn;
            changed = true;
        }

        if (scopeLine < 0)
        {
            missing.Add(expectedScope + carriageReturn);
            changed = true;
        }
        else if (!string.Equals(lines[scopeLine].TrimEnd(), expectedScope, StringComparison.Ordinal))
        {
            lines[scopeLine] = expectedScope + carriageReturn;
            changed = true;
        }

        if (!changed) return null;
        if (missing.Count == 0) return string.Join('\n', lines);

        List<string> rebuilt = [.. lines[..close], .. missing, .. lines[close..]];
        return string.Join('\n', rebuilt);
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
        if (!_files.FileExists(full)) return null;

        try
        {
            return _files.ReadAllText(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"'{relativePath}' exists but could not be read: {exception.Message}", exception);
        }
    }

    private string Absolute(string relativePath) =>
        _paths.GetFullPath(
            Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            _repoRoot);

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
        string boundary = RepositoryBoundary.For(_repoRoot, Path.DirectorySeparatorChar);
        string full = _paths.GetFullPath(fullPath, _repoRoot);
        if (!RepositoryBoundary.IsWithin(full, boundary, _environment.IsFileSystemCaseInsensitive))
        {
            throw new IOException(
                $"Refusing to write '{named}': it resolves outside the repository root, so nothing was written.");
        }

        string current = _repoRoot.TrimEnd(Path.DirectorySeparatorChar);
        foreach (string segment in full[boundary.Length..].Split(
                     Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!IsLink(current)) continue;

            string? linkTarget = _files.GetLinkTarget(current);
            throw new IOException(
                $"Refusing to write '{named}': '{current}' is the symbolic link " +
                $"'{linkTarget ?? "whose target is missing"}', and a migration does not write through or replace a link, " +
                "so nothing was written.");
        }

        return full;
    }

    /// <summary>
    /// True when the entry at <paramref name="path"/> is a symbolic link, including one whose target is
    /// missing.
    /// </summary>
    /// <remarks>
    /// The seam's link-target read answers null for a dangling link, because a target that does not exist
    /// reads as no link at all. The parent's listing reports each child's kind without following it, so it
    /// is what tells a dangling link from a regular file — and refusing a dangling link matters most,
    /// since a write would otherwise rename over it and silently replace the host's link.
    /// </remarks>
    private bool IsLink(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        return parent is not null
            && _files.EnumerateEntries(parent)
                .Any(entry => entry.IsLink && _paths.AreSame(entry.Path, path));
    }

    /// <summary>
    /// Checks again where a file that was just written actually resolved, removing it if the path
    /// stopped being inside the repository while it was written.
    /// </summary>
    /// <remarks>
    /// Every write here ends with this call — a created stub or standard, a rename's destination,
    /// a reshaped standard — because the pre-write check and the write cannot be one operation in
    /// .NET, so the guarantee cannot rest on the check's timing. It rests on this: the write's
    /// result is inspected, and a path that now resolves outside the root — a directory replaced by
    /// a link in between — costs the file, not the repository's boundary. A replace loses the
    /// content it wrote, which is the price of not leaving a document outside the root; the file it
    /// replaced was outside the root too, since the path only resolves there now.
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
    private void Discard(string path)
    {
        try
        {
            _files.DeleteFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the refusal this supports is the failure the caller has to see.
        }
    }

    [GeneratedRegex(@"^owner:\s*(?<owner>\S.*)$", RegexOptions.Multiline)]
    private static partial Regex OwnerLine();
}
