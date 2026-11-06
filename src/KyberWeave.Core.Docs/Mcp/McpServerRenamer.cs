using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>What one target of a rename came to.</summary>
public enum McpRenameOutcome
{
    /// <summary>The target holds no old key, so there is nothing to rename.</summary>
    NothingToDo,

    /// <summary>A proven old key was rewritten to its shipped name.</summary>
    Renamed,

    /// <summary>A proven old key was removed because the shipped name already held the same launch.</summary>
    RemovedDuplicate,

    /// <summary>An old key was reported and left alone, because it could not be proven ours.</summary>
    Reported,

    /// <summary>The target could not be migrated, and no byte of it was written.</summary>
    Refused
}

/// <summary>What a rename found, did, or refused, in the words the operator reads.</summary>
/// <param name="Harness">The target slug the change is about, or <c>declaration</c> for the host declaration.</param>
/// <param name="Path">The repository-relative file, or null when the target has no file in the tree.</param>
/// <param name="Outcome">What happened, or what would happen in a dry run.</param>
/// <param name="OldKey">The retired key the change is about, when there is one.</param>
/// <param name="NewKey">The shipped key the change is about, when there is one.</param>
/// <param name="Code">The stable <c>KW-*</c> rule id the change carries.</param>
/// <param name="Message">One sentence describing the change or the refusal.</param>
/// <param name="Hint">The remedy, as the command or edit to make.</param>
/// <param name="Snippet">For a harness this tool does not write: what to paste instead.</param>
/// <param name="Written">Whether bytes reached the file. Always false in a dry run.</param>
public sealed record McpRenameChange(
    string Harness,
    string? Path,
    McpRenameOutcome Outcome,
    string? OldKey = null,
    string? NewKey = null,
    string? Code = null,
    string? Message = null,
    string? Hint = null,
    string? Snippet = null,
    bool Written = false);

/// <summary>
/// One retired MCP server key, the key that replaced it, and the launch that proves an entry written
/// under the old one is Kyber-Weave's.
/// </summary>
/// <param name="OldKey">The key existing installs were written under.</param>
/// <param name="NewKey">The key this version writes.</param>
/// <param name="Binary">The file name of the binary the old key's server launches.</param>
/// <param name="NextArgument">
/// The argument that must follow the binary for the launch to be this server's, or null when the
/// binary alone identifies it.
/// </param>
public sealed record McpRenameRule(
    string OldKey,
    string NewKey,
    string Binary,
    string? NextArgument);

/// <summary>
/// Renames the retired MCP server keys in an existing install, in place, and never touches an entry
/// it cannot prove is Kyber-Weave's.
/// </summary>
/// <remarks>
/// <para>
/// A key is not proof. Existing installs declare <c>kyber-weave</c>, <c>kyber-weave-arbiter</c> and
/// <c>kyberdash</c>, and every one of those keys could also be an operator's own server that happens
/// to share the name, or a wrapper such as <c>npx kyber-weave-mcp</c> that runs it some other way.
/// The rule this type implements is therefore a pair: the exact old key <em>and</em> a launch whose
/// first argument's file name is the shipped binary and whose next argument is the one that server's
/// entry requires. Anything less is reported and left alone.
/// </para>
/// <para>
/// The migration is a byte splice, never a re-serialisation: <see cref="JsonServerSplice"/> rewrites
/// the one property-name token and proves the result by re-parsing it. A file's comments, key order,
/// trailing commas and byte-order mark survive, because they are the operator's text.
/// </para>
/// <para>
/// Nothing here reads or writes a user-scope file. The scan walks the repository it is given, and the
/// paths it will not enter are named in the report so the operator can edit them by hand.
/// </para>
/// </remarks>
public static class McpServerRenamer
{
    /// <summary>A proven old key is present; the remedy is this command.</summary>
    public const string RenameCode = "KW-MCP-WIRE-007";

    /// <summary>An old key is present but not provably ours, so it is left alone.</summary>
    public const string LeftAloneCode = "KW-MCP-WIRE-008";

    /// <summary>A target was refused and no byte was written to it.</summary>
    public const string RefusedCode = "KW-MCP-WIRE-009";

    /// <summary>The target slug that names the host declaration rather than a harness.</summary>
    public const string DeclarationTarget = "declaration";

    /// <summary>The command an operator runs to make the rename.</summary>
    public const string MigrateCommand = "kyber-weave docs mcp-migrate";

    /// <summary>The suffix a Windows install appends to an executable's file name.</summary>
    private const string WindowsExecutableSuffix = ".exe";

    private static readonly JsonDocumentOptions LenientReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>The retired keys and the launch that proves each one ours, in report order.</summary>
    public static IReadOnlyList<McpRenameRule> Rules { get; } =
    [
        new("kyber-weave", McpServerNames.Docs, "kyber-weave-mcp", null),
        new("kyber-weave-arbiter", McpServerNames.Arbiter, "kyber-weave-arbiter", "serve"),
        new("kyberdash", McpServerNames.Dash, "kyberdash", "mcp")
    ];

    /// <summary>The rule for a retired key, or null when the key is not one of them.</summary>
    public static McpRenameRule? RuleForOldKey(string oldKey)
    {
        ArgumentNullException.ThrowIfNull(oldKey);

        foreach (McpRenameRule rule in Rules)
        {
            if (string.Equals(rule.OldKey, oldKey, StringComparison.Ordinal))
                return rule;
        }

        return null;
    }

    /// <summary>The rule whose shipped key is <paramref name="newKey"/>, or null when none is.</summary>
    public static McpRenameRule? RuleForNewKey(string newKey)
    {
        ArgumentNullException.ThrowIfNull(newKey);

        foreach (McpRenameRule rule in Rules)
        {
            if (string.Equals(rule.NewKey, newKey, StringComparison.Ordinal))
                return rule;
        }

        return null;
    }

    /// <summary>
    /// Whether a launch is provably the server <paramref name="rule"/> names: argv[0]'s file name is
    /// the shipped binary — any directory, either case, an optional <c>.exe</c> — and, where the rule
    /// requires one, the next argument is the one that server's entry passes.
    /// </summary>
    /// <param name="rule">The retired key's rule.</param>
    /// <param name="argv">The launch command an entry body names.</param>
    public static bool IsProven(McpRenameRule rule, IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count == 0)
            return false;

        string program = Path.GetFileName(argv[0]);

        if (!string.Equals(program, rule.Binary, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(program, rule.Binary + WindowsExecutableSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return rule.NextArgument is null
            || (argv.Count > 1 && string.Equals(argv[1], rule.NextArgument, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reads the repository and reports every old key it holds, without writing anything at all.
    /// </summary>
    /// <param name="repositoryRoot">The repository whose repository-scope files are read.</param>
    /// <param name="harness">
    /// The harness slug to read, or null to read every harness the catalog knows in this tree.
    /// </param>
    /// <remarks>
    /// The scan is deliberately independent of <see cref="McpWiringInspector"/>: the inspector answers
    /// "is the declared server wired", and returns nothing at all when nothing is declared, while a
    /// leftover key is worth reporting either way.
    /// </remarks>
    public static IReadOnlyList<McpRenameChange> Scan(string repositoryRoot, string? harness = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        string root = Path.GetFullPath(repositoryRoot);
        List<McpRenameChange> changes = [];

        foreach (HarnessMcpEntry row in Rows(harness))
            changes.AddRange(ScanRow(root, row));

        return changes;
    }

    /// <summary>
    /// Renames the old keys the named targets hold, and edits the host declaration on the way.
    /// </summary>
    /// <param name="repositoryRoot">The repository whose files are read and written.</param>
    /// <param name="targets">
    /// The harness slugs to migrate, plus <see cref="DeclarationTarget"/> for the host declaration.
    /// </param>
    /// <param name="dryRun">
    /// When true the same computation runs and the same report is produced, but no byte is written —
    /// not even the receipt.
    /// </param>
    /// <remarks>
    /// A target that refuses does not stop another: one unlucky file in a repository is no reason to
    /// leave the rest of the install under the retired names.
    /// </remarks>
    public static IReadOnlyList<McpRenameChange> Migrate(
        string repositoryRoot,
        IReadOnlyList<string> targets,
        bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(targets);

        string root = Path.GetFullPath(repositoryRoot);
        List<McpRenameChange> changes = [];
        bool declaration = false;
        bool namedDeclaration = false;

        foreach (string target in targets)
        {
            if (string.Equals(target, DeclarationTarget, StringComparison.Ordinal))
            {
                declaration = true;
                namedDeclaration = true;
                continue;
            }

            HarnessMcpEntry? row = HarnessMcpCatalog.Find(target);

            if (row is null)
            {
                changes.Add(Refused(
                    target,
                    null,
                    $"'{target}' is not a harness this tool knows, and not the '{DeclarationTarget}' target",
                    $"Targets are harness slugs from the catalog — {WritableTargets()} — or '{DeclarationTarget}'."));
                continue;
            }

            changes.AddRange(row.Fixability == McpWiringFixability.Fixable && row.Shape == McpConfigurationShape.Json
                ? MigrateRow(root, row, dryRun)
                : ReportOnly(root, row));

            declaration = true;
        }

        if (declaration)
            changes.AddRange(MigrateDeclaration(root, dryRun, namedDeclaration));

        return changes;
    }

    /// <summary>The harness rows a scan covers, in catalog order.</summary>
    private static IEnumerable<HarnessMcpEntry> Rows(string? harness)
    {
        if (harness is null)
            return HarnessMcpCatalog.Rows;

        HarnessMcpEntry? row = HarnessMcpCatalog.Find(harness);
        return row is null ? [] : [row];
    }

    /// <summary>Reads one harness's repository-scope files and reports what they hold.</summary>
    private static IReadOnlyList<McpRenameChange> ScanRow(string root, HarnessMcpEntry row)
    {
        if (row.Shape == McpConfigurationShape.RepositorySettings || row.Paths.Count == 0)
            return [];

        List<McpRenameChange> changes = [];

        foreach (ConfiguredFile file in ConfiguredFiles(root, row))
            changes.AddRange(ScanFile(row, file));

        return changes;
    }

    private static IReadOnlyList<McpRenameChange> ScanFile(HarnessMcpEntry row, ConfiguredFile file)
    {
        if (file.Probe.Obstruction != McpLinkObstruction.None)
        {
            return
            [
                Refused(
                    row.Harness,
                    file.RelativePath,
                    $"{McpConfigurationLink.Describe(file.RelativePath, file.Probe)}, so nothing was read",
                    "Kyber-Weave reads repository-scope files only; put a real file inside the repository and run this command again.")
            ];
        }

        if (row.Shape == McpConfigurationShape.Toml)
            return ScanToml(row, file);

        FileAnalysis analysis = Analyze(row, file.AbsolutePath, file.RelativePath);

        if (analysis.Problem is not null)
            return [Refused(row.Harness, file.RelativePath, analysis.Problem, FixHint(file.RelativePath, row))];

        List<McpRenameChange> changes = [];

        foreach (RuleState state in analysis.States)
        {
            if (!state.OldPresent)
                continue;

            changes.Add(DescribeState(row, file.RelativePath, state));
        }

        return changes;
    }

    /// <summary>The report a read-only harness gets: the key, kept, with what to paste instead.</summary>
    private static IReadOnlyList<McpRenameChange> ScanToml(HarnessMcpEntry row, ConfiguredFile file)
    {
        string text = ReadText(file.AbsolutePath) ?? string.Empty;
        TomlScanReport scan = TomlTableScanner.Scan(text);

        List<McpRenameChange> changes = [];

        foreach (McpRenameRule rule in Rules)
        {
            if (!scan.Servers.Contains(rule.OldKey, StringComparer.Ordinal))
                continue;

            changes.Add(ReportedChange(row, file.RelativePath, rule, proven: true));
        }

        return changes;
    }

    /// <summary>
    /// Reads one file's server table and answers, per retired key, whether the old key and the new one
    /// are there and what each launches.
    /// </summary>
    private static FileAnalysis Analyze(HarnessMcpEntry row, string absolutePath, string relativePath)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(absolutePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new FileAnalysis([], $"{relativePath} could not be read: {exception.Message}");
        }

        return Analyze(row, bytes, relativePath);
    }

    private static FileAnalysis Analyze(HarnessMcpEntry row, byte[] document, string relativePath)
    {
        int bomLength = JsonServerSplice.ByteOrderMarkLength(document);

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(document.AsMemory(bomLength), LenientReadOptions);
            JsonElement container = parsed.RootElement;

            if (container.ValueKind != JsonValueKind.Object)
                return new FileAnalysis([], ForeignMessage(relativePath, "its root is not a JSON object"));

            foreach (string key in row.ServerKeyPath)
            {
                if (!container.TryGetProperty(key, out JsonElement next))
                    return new FileAnalysis(States(row, default), null);

                if (next.ValueKind != JsonValueKind.Object)
                    return new FileAnalysis([], ForeignMessage(relativePath, $"its '{key}' key is not a JSON object"));

                container = next;
            }

            return new FileAnalysis(States(row, container), null);
        }
        catch (JsonException exception)
        {
            return new FileAnalysis([], $"{relativePath} cannot be migrated: it is not valid JSON ({exception.Message})");
        }
    }

    private static IReadOnlyList<RuleState> States(HarnessMcpEntry row, JsonElement container)
    {
        List<RuleState> states = [];

        foreach (McpRenameRule rule in Rules)
        {
            bool oldPresent = container.ValueKind == JsonValueKind.Object
                && container.TryGetProperty(rule.OldKey, out JsonElement oldEntry);
            bool newPresent = container.ValueKind == JsonValueKind.Object
                && container.TryGetProperty(rule.NewKey, out JsonElement newEntry);

            states.Add(new RuleState(
                rule,
                oldPresent,
                newPresent,
                oldPresent ? Launch(row, container.GetProperty(rule.OldKey)) : null,
                newPresent ? Launch(row, container.GetProperty(rule.NewKey)) : null));
        }

        return states;
    }

    /// <summary>The launch command an entry body names, when the harness's shape pins one.</summary>
    private static IReadOnlyList<string>? Launch(HarnessMcpEntry row, JsonElement entry)
    {
        McpWiringInspector.EntryBody body = McpWiringInspector.ReadEntryBody(entry, row.CommandShape);

        return body.Reading == McpWiringInspector.McpEntryReading.Readable ? body.Tokens : null;
    }

    /// <summary>What a scan reports for one old key it found in a readable file.</summary>
    private static McpRenameChange DescribeState(HarnessMcpEntry row, string relativePath, RuleState state)
    {
        if (row.Fixability != McpWiringFixability.Fixable)
            return ReportedChange(row, relativePath, state.Rule, proven: state.Proven);

        if (!state.Proven)
            return LeftAloneChange(row, relativePath, state.Rule);

        if (!state.NewPresent)
        {
            return new McpRenameChange(
                row.Harness,
                relativePath,
                McpRenameOutcome.Renamed,
                state.Rule.OldKey,
                state.Rule.NewKey,
                RenameCode,
                $"'{state.Rule.OldKey}' is provably Kyber-Weave's and would be renamed to '{state.Rule.NewKey}'");
        }

        return SameLaunch(row, state.OldLaunch ?? [], state.NewLaunch ?? [])
            ? new McpRenameChange(
                row.Harness,
                relativePath,
                McpRenameOutcome.RemovedDuplicate,
                state.Rule.OldKey,
                state.Rule.NewKey,
                RenameCode,
                $"'{state.Rule.OldKey}' and '{state.Rule.NewKey}' launch the same server, so the old member would be removed")
            : new McpRenameChange(
                row.Harness,
                relativePath,
                McpRenameOutcome.Refused,
                state.Rule.OldKey,
                state.Rule.NewKey,
                RefusedCode,
                BothLaunches(row, state),
                $"Decide which server {relativePath} should hold, delete the other entry by hand, and run this command again.");
    }

    /// <summary>Migrates one writable harness's file, and its receipt, in place.</summary>
    private static IReadOnlyList<McpRenameChange> MigrateRow(string root, HarnessMcpEntry row, bool dryRun)
    {
        string relativePath = row.Paths[0];

        if (McpRepositoryPath.Contained(root, relativePath) is not { } fullPath)
        {
            return
            [
                Refused(
                    row.Harness,
                    relativePath,
                    $"{relativePath} does not resolve inside the repository, so nothing was written",
                    "Report this: a repository-scope file has to name a path in the tree this command was given.")
            ];
        }

        McpLinkProbe probe = McpConfigurationLink.Probe(root, fullPath);

        if (probe.Obstruction != McpLinkObstruction.None)
        {
            return
            [
                Refused(
                    row.Harness,
                    relativePath,
                    $"{McpConfigurationLink.Describe(relativePath, probe)}, so nothing was read or written",
                    "Kyber-Weave reads and writes repository-scope files only; put a real file inside the repository and run this command again.")
            ];
        }

        if (!File.Exists(fullPath))
            return [NothingToDo(row.Harness, relativePath)];

        byte[] original;

        try
        {
            original = File.ReadAllBytes(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [Refused(row.Harness, relativePath, $"{relativePath} could not be read: {exception.Message}", null)];
        }

        FileAnalysis analysis = Analyze(row, original, relativePath);

        if (analysis.Problem is not null)
            return [Refused(row.Harness, relativePath, analysis.Problem, FixHint(relativePath, row))];

        List<McpRenameChange> changes = [];
        List<string> servers = [];
        byte[] planned = original;
        bool changed = false;

        foreach (RuleState state in analysis.States)
        {
            if (!state.OldPresent)
                continue;

            if (row.Fixability != McpWiringFixability.Fixable || !state.Proven)
            {
                changes.Add(row.Fixability != McpWiringFixability.Fixable
                    ? ReportedChange(row, relativePath, state.Rule, state.Proven)
                    : LeftAloneChange(row, relativePath, state.Rule));
                continue;
            }

            if (state.NewPresent && !SameLaunch(row, state.OldLaunch ?? [], state.NewLaunch ?? []))
            {
                changes.Add(new McpRenameChange(
                    row.Harness,
                    relativePath,
                    McpRenameOutcome.Refused,
                    state.Rule.OldKey,
                    state.Rule.NewKey,
                    RefusedCode,
                    BothLaunches(row, state),
                    $"Decide which server {relativePath} should hold, delete the other entry by hand, and run this command again. Nothing was written."));
                continue;
            }

            JsonSplicePlan plan = state.NewPresent
                ? JsonServerSplice.PlanRemoval(planned, row.ServerKeyPath, state.Rule.OldKey)
                : JsonServerSplice.PlanRename(planned, row.ServerKeyPath, state.Rule.OldKey, state.Rule.NewKey);

            JsonSpliceOutcome expected = state.NewPresent ? JsonSpliceOutcome.Removed : JsonSpliceOutcome.Renamed;

            if (plan.Outcome != expected)
            {
                changes.Add(RefusedFromPlan(row.Harness, relativePath, state.Rule, plan));
                continue;
            }

            planned = plan.Document;
            changed = true;
            servers.Add(state.Rule.NewKey);
            changes.Add(state.NewPresent
                ? new McpRenameChange(
                    row.Harness,
                    relativePath,
                    McpRenameOutcome.RemovedDuplicate,
                    state.Rule.OldKey,
                    state.Rule.NewKey,
                    RenameCode,
                    $"removed duplicate '{state.Rule.OldKey}': '{state.Rule.NewKey}' already holds the same launch")
                : new McpRenameChange(
                    row.Harness,
                    relativePath,
                    McpRenameOutcome.Renamed,
                    state.Rule.OldKey,
                    state.Rule.NewKey,
                    RenameCode,
                    $"renamed '{state.Rule.OldKey}' to '{state.Rule.NewKey}'"));
        }

        // A refusal anywhere in the file means the file keeps every byte, so the report may not read
        // as though the other rules had been applied.
        if (changes.Exists(change => change.Outcome == McpRenameOutcome.Refused))
            return [.. changes.Where(change => change.Outcome == McpRenameOutcome.Refused)];

        if (!changed)
            return changes.Count == 0 ? [NothingToDo(row.Harness, relativePath)] : changes;

        if (dryRun)
            return changes;

        if (Record(root, relativePath, original, planned, servers) is { } failure)
        {
            return
            [
                Refused(
                    row.Harness,
                    relativePath,
                    $"{relativePath} was not written: the record that would make the change undoable could not be persisted ({failure})",
                    $"Check that {McpWiringReceipt.RelativePath} is writable, then run this command again.")
            ];
        }

        try
        {
            AtomicFile.WriteAllBytes(fullPath, planned);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [Refused(row.Harness, relativePath, $"{relativePath} could not be written: {exception.Message}", null)];
        }

        return [.. changes.Select(change => change with { Written = true })];
    }

    /// <summary>
    /// The report a harness this tool does not write gets: the keys it holds, named, and the text to
    /// paste into its configuration by hand.
    /// </summary>
    private static IReadOnlyList<McpRenameChange> ReportOnly(string root, HarnessMcpEntry row)
    {
        List<McpRenameChange> changes = [];

        foreach (ConfiguredFile file in ConfiguredFiles(root, row))
        {
            if (row.Shape == McpConfigurationShape.Toml)
            {
                changes.AddRange(ScanToml(row, file));
                continue;
            }

            FileAnalysis analysis = Analyze(row, file.AbsolutePath, file.RelativePath);

            foreach (RuleState state in analysis.States)
            {
                if (state.OldPresent)
                    changes.Add(ReportedChange(row, file.RelativePath, state.Rule, state.Proven));
            }
        }

        if (changes.Count > 0)
            return changes;

        // A harness with no file in the tree — the Copilot coding agent keeps its servers in
        // repository settings — still has to be told what to rename, and where.
        return [ReportedChange(row, row.Paths.Count > 0 ? row.Paths[0] : null, Rules[0], proven: false)];
    }

    /// <summary>
    /// Edits the host declaration's server key on its own line, and reloads the edited text to prove
    /// the edit changed the name and nothing else.
    /// </summary>
    private static IReadOnlyList<McpRenameChange> MigrateDeclaration(string root, bool dryRun, bool explicitTarget)
    {
        string? configPath = FindConfigPath(root);
        string relativePath = configPath is null
            ? Path.Combine(KyberWeaveYamlParser.DefaultDirectoryName, KyberWeaveYamlParser.DefaultFileName).Replace('\\', '/')
            : Path.GetRelativePath(root, configPath).Replace('\\', '/');

        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root);

        if (!loaded.Success || loaded.Config is null)
        {
            return configPath is null
                ? []
                : [Refused(
                    DeclarationTarget,
                    relativePath,
                    $"the declaration could not be read: {loaded.Error}",
                    $"Fix {relativePath} so it loads, then run this command again. Nothing was written.")];
        }

        List<McpRenameChange> changes = [];
        string text = configPath is null ? string.Empty : File.ReadAllText(configPath);

        foreach (McpRenameRule rule in Rules)
        {
            if (!loaded.Config.Mcp.RequiredServers.TryGetValue(rule.OldKey, out RequiredMcpServer? server))
                continue;

            if (!IsProven(rule, server.Command))
            {
                changes.Add(new McpRenameChange(
                    DeclarationTarget,
                    relativePath,
                    McpRenameOutcome.Reported,
                    rule.OldKey,
                    rule.NewKey,
                    LeftAloneCode,
                    $"the declaration's '{rule.OldKey}' launches '{string.Join(' ', server.Command)}', which is not provably "
                        + $"the shipped binary, so it was left alone; rename the key to '{rule.NewKey}' by hand",
                    $"Edit {relativePath}: rename '{rule.OldKey}:' to '{rule.NewKey}:' if that entry is Kyber-Weave's."));
                continue;
            }

            if (TryReplaceKeyLine(text, rule, out string edited, out string? refusal))
            {
                if (!Verifies(edited, rule))
                {
                    changes.Add(new McpRenameChange(
                        DeclarationTarget,
                        relativePath,
                        McpRenameOutcome.Refused,
                        rule.OldKey,
                        rule.NewKey,
                        RefusedCode,
                        $"renaming '{rule.OldKey}' to '{rule.NewKey}' in {relativePath} did not reload as the same declaration "
                            + "with one name changed, so nothing was written",
                        $"Edit {relativePath} by hand: rename '{rule.OldKey}:' to '{rule.NewKey}:'."));
                    continue;
                }

                changes.Add(new McpRenameChange(
                    DeclarationTarget,
                    relativePath,
                    McpRenameOutcome.Renamed,
                    rule.OldKey,
                    rule.NewKey,
                    RenameCode,
                    $"renamed '{rule.OldKey}' to '{rule.NewKey}' in the host declaration"));

                if (dryRun || configPath is null)
                    continue;

                if (WriteDeclaration(configPath, edited) is { } failure)
                {
                    changes[^1] = changes[^1] with
                    {
                        Outcome = McpRenameOutcome.Refused,
                        Code = RefusedCode,
                        Message = $"{relativePath} could not be written: {failure}",
                        Written = false
                    };
                    continue;
                }

                text = edited;
                changes[^1] = changes[^1] with { Written = true };
                continue;
            }

            if (refusal is not null)
            {
                changes.Add(new McpRenameChange(
                    DeclarationTarget,
                    relativePath,
                    McpRenameOutcome.Refused,
                    rule.OldKey,
                    rule.NewKey,
                    RefusedCode,
                    refusal,
                    $"Edit {relativePath} by hand: rename '{rule.OldKey}:' to '{rule.NewKey}:'."));
            }
        }

        if (changes.Count == 0 && explicitTarget)
            changes.Add(new McpRenameChange(DeclarationTarget, relativePath, McpRenameOutcome.NothingToDo));

        return changes;
    }

    /// <summary>
    /// Replaces one key line in the declaration text: the key itself, and not the indentation, the
    /// value, or any comment beside it.
    /// </summary>
    /// <returns>
    /// True when the line was found and replaced; false when there was nothing to replace, with
    /// <paramref name="refusal"/> set when the declaration's shape is not one a one-line edit can
    /// serve.
    /// </returns>
    private static bool TryReplaceKeyLine(
        string text,
        McpRenameRule rule,
        out string edited,
        out string? refusal)
    {
        edited = text;
        refusal = null;
        string[] lines = text.Split('\n');
        int sectionIndent = -1;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            string trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;

            if (trimmed.StartsWith("required-servers:", StringComparison.Ordinal))
            {
                string rest = trimmed["required-servers:".Length..].Trim();

                if (rest.StartsWith('{'))
                {
                    refusal = $"the declaration writes 'required-servers' as a flow mapping, so the rename cannot be made "
                        + $"by editing one line; write it by hand: rename '{rule.OldKey}' to '{rule.NewKey}'";
                    return false;
                }

                sectionIndent = indent;
                continue;
            }

            if (sectionIndent < 0 || trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith('#'))
                continue;

            if (indent <= sectionIndent)
                break;

            string key = rule.OldKey + ":";

            if (!trimmed.StartsWith(key, StringComparison.Ordinal))
                continue;

            string remainder = trimmed[key.Length..];

            if (remainder.Length > 0 && remainder[0] is not (' ' or '\t' or '#'))
                continue;

            StringBuilder rebuilt = new(text.Length + rule.NewKey.Length);

            for (int before = 0; before < index; before++)
                rebuilt.Append(lines[before]).Append('\n');

            rebuilt.Append(line[..indent]).Append(rule.NewKey).Append(':').Append(remainder)
                .Append(lines[index][line.Length..]);

            for (int after = index + 1; after < lines.Length; after++)
                rebuilt.Append('\n').Append(lines[after]);

            edited = rebuilt.ToString();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether an edited declaration reloads as the same declaration with one server key renamed.
    /// </summary>
    private static bool Verifies(string edited, McpRenameRule rule)
    {
        try
        {
            KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml(edited);

            if (!config.Mcp.RequiredServers.ContainsKey(rule.NewKey) ||
                config.Mcp.RequiredServers.ContainsKey(rule.OldKey))
            {
                return false;
            }

            return config.Mcp.RequiredServers[rule.NewKey].Command.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? WriteDeclaration(string configPath, string edited)
    {
        byte[] original = File.ReadAllBytes(configPath);
        bool bom = JsonServerSplice.ByteOrderMarkLength(original) > 0;
        byte[] content = Encoding.UTF8.GetBytes(edited);

        try
        {
            AtomicFile.WriteAllBytes(configPath, bom ? [0xEF, 0xBB, 0xBF, .. content] : content);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// Persists the undo record before the bytes land, rebasing an existing record whose post-write
    /// digest still matches the file so that a removal undoes the original wire rather than the
    /// rename.
    /// </summary>
    private static string? Record(
        string root,
        string relativePath,
        byte[] preWrite,
        byte[] postWrite,
        IReadOnlyList<string> servers)
    {
        if (servers.Count == 0)
            return null;

        List<McpWiringReceiptRecord> records = [.. McpWiringReceipt.Load(root).Files];
        string preDigest = Digest(preWrite);
        int at = records.FindIndex(candidate => McpWiringReceipt.SamePath(candidate.Path, relativePath));
        bool rebase = at >= 0 && string.Equals(records[at].PostSha256, preDigest, StringComparison.OrdinalIgnoreCase);

        McpWiringReceiptRecord record = rebase
            ? records[at] with
            {
                Servers = servers,
                PostSha256 = Digest(postWrite),
                WrittenAtUtc = DateTimeOffset.UtcNow
            }
            : new McpWiringReceiptRecord(
                relativePath,
                McpWiringWriteMode.Inserted,
                servers,
                preDigest,
                Digest(postWrite),
                DateTimeOffset.UtcNow,
                Convert.ToBase64String(preWrite));

        if (at >= 0)
            records.RemoveAt(at);

        records.Add(record);

        return McpWiringReceipt.Save(root, records);
    }

    private static IReadOnlyList<ConfiguredFile> ConfiguredFiles(string root, HarnessMcpEntry row)
    {
        List<ConfiguredFile> files = [];

        foreach (string relativePath in row.Paths)
        {
            if (McpRepositoryPath.Contained(root, relativePath) is not { } path)
                continue;

            McpLinkProbe probe = McpConfigurationLink.Probe(root, path);

            if (probe.Obstruction != McpLinkObstruction.None || File.Exists(path))
                files.Add(new ConfiguredFile(path, relativePath, probe));
        }

        return files;
    }

    private static string? FindConfigPath(string root)
    {
        string defaultPath = Path.Combine(
            root,
            KyberWeaveYamlParser.DefaultDirectoryName,
            KyberWeaveYamlParser.DefaultFileName);

        if (File.Exists(defaultPath))
            return defaultPath;

        string legacy = Path.Combine(root, KyberWeaveYamlParser.DefaultFileName);
        return File.Exists(legacy) ? legacy : null;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SameLaunch(HarnessMcpEntry row, IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int index = 0; index < left.Count; index++)
        {
            if (!TokenMatches(row, left[index], right[index]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether two launch tokens name the same thing, allowing the harness's own spelling of the
    /// repository root: <c>.</c> and <c>${workspaceFolder}</c> are the same directory to VS Code.
    /// </summary>
    private static bool TokenMatches(HarnessMcpEntry row, string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal)
        || (string.Equals(left, JsonServerSplice.RepositoryRootToken, StringComparison.Ordinal)
            && string.Equals(right, row.RepositoryRootToken, StringComparison.Ordinal));

    private static string BothLaunches(HarnessMcpEntry row, RuleState state) =>
        $"{row.Harness} holds '{state.Rule.OldKey}' and '{state.Rule.NewKey}' with different launches: "
        + $"'{Describe(state.OldLaunch)}' and '{Describe(state.NewLaunch)}'";

    private static string Describe(IReadOnlyList<string>? argv) =>
        argv is null ? "(a launch this reader could not read)" : string.Join(' ', argv);

    private static string ForeignMessage(string relativePath, string reason) =>
        $"{relativePath} cannot be migrated: it is not a document this harness could load ({reason})";

    private static string FixHint(string relativePath, HarnessMcpEntry row) =>
        $"Fix {relativePath} so it holds a JSON object with the server map at '{string.Join('.', row.ServerKeyPath)}', "
        + "or restore it from version control; this command never rewrites a document it could not read.";

    private static string WritableTargets() =>
        string.Join(", ", HarnessMcpCatalog.Rows
            .Where(row => row.Fixability == McpWiringFixability.Fixable && row.Shape == McpConfigurationShape.Json)
            .Select(row => row.Harness));

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static McpRenameChange NothingToDo(string harness, string? path) =>
        new(harness, path, McpRenameOutcome.NothingToDo);

    private static McpRenameChange LeftAloneChange(HarnessMcpEntry row, string path, McpRenameRule rule) =>
        new(
            row.Harness,
            path,
            McpRenameOutcome.Reported,
            rule.OldKey,
            rule.NewKey,
            LeftAloneCode,
            $"'{rule.OldKey}' could not be proven to be Kyber-Weave's, so it was left alone; rename it to "
                + $"'{rule.NewKey}' by hand if it is",
            $"Edit {path}: rename '{rule.OldKey}' to '{rule.NewKey}' if that entry launches "
                + $"{rule.Binary}{(rule.NextArgument is null ? string.Empty : " " + rule.NextArgument)}.");

    private static McpRenameChange ReportedChange(
        HarnessMcpEntry row,
        string? path,
        McpRenameRule rule,
        bool proven) =>
        new(
            row.Harness,
            path,
            McpRenameOutcome.Reported,
            rule.OldKey,
            rule.NewKey,
            LeftAloneCode,
            row.Shape == McpConfigurationShape.RepositorySettings
                ? $"its MCP servers are configured in repository settings, not a file in this tree, so nothing was "
                    + $"written; rename '{rule.OldKey}' to '{rule.NewKey}' there"
                : $"Kyber-Weave does not write {row.Harness} configuration, so '{rule.OldKey}' was left alone; "
                    + $"rename the key to '{rule.NewKey}' by hand",
            proven
                ? $"Edit {path ?? row.InPlaySignal}: rename '{rule.OldKey}' to '{rule.NewKey}'."
                : $"Edit {path ?? row.InPlaySignal}: rename '{rule.OldKey}' to '{rule.NewKey}' if that entry is Kyber-Weave's.",
            Snippet(row, rule));

    private static string Snippet(HarnessMcpEntry row, McpRenameRule rule) =>
        row.Shape == McpConfigurationShape.Toml
            ? $"[{string.Join('.', row.ServerKeyPath)}.{rule.NewKey}]\n"
                + $"command = \"{rule.Binary}\"\nargs = [\"--repo-root\", \".\"]"
            : $"\"{rule.NewKey}\" under {string.Join('.', row.ServerKeyPath)}: "
                + $"{{ \"command\": \"{rule.Binary}\", \"args\": [\"--repo-root\", \".\"] }}";

    private static McpRenameChange RefusedFromPlan(
        string harness,
        string path,
        McpRenameRule rule,
        JsonSplicePlan plan) =>
        Refused(
            harness,
            path,
            $"{path} was not changed: {plan.Message}",
            $"Edit {path} by hand: rename '{rule.OldKey}' to '{rule.NewKey}'.");

    private static McpRenameChange Refused(string harness, string? path, string message, string? hint) =>
        new(
            harness,
            path,
            McpRenameOutcome.Refused,
            Code: RefusedCode,
            Message: message,
            Hint: hint);

    /// <summary>One catalog row's server file, as the scan or the migration reads it.</summary>
    private readonly record struct ConfiguredFile(string AbsolutePath, string RelativePath, McpLinkProbe Probe);

    /// <summary>What one file holds for one retired key.</summary>
    private readonly record struct RuleState(
        McpRenameRule Rule,
        bool OldPresent,
        bool NewPresent,
        IReadOnlyList<string>? OldLaunch,
        IReadOnlyList<string>? NewLaunch)
    {
        /// <summary>
        /// Whether the old entry launches the shipped binary, which is the half of the proof a key
        /// alone can never supply.
        /// </summary>
        public bool Proven => OldLaunch is not null && McpServerRenamer.IsProven(Rule, OldLaunch);
    }

    /// <summary>One file's reading: what it holds per retired key, or why it could not be read.</summary>
    private readonly record struct FileAnalysis(IReadOnlyList<RuleState> States, string? Problem);
}
