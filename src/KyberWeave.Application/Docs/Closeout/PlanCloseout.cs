using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Review;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>
/// Does the mechanical half of closing out a finished plan: checks the closeout evidence the archive gate checks,
/// then moves the plan to the archive with its status set, removes its task file, takes its row out of the plan
/// index and adds one to the archived plan inventory.
/// </summary>
/// <remarks>
/// <para>
/// The judgement half stays with the documentation developer: harvesting the plan's decisions into canonical
/// documents and writing the Closeout mapping. This class refuses, naming each missing condition, when that work is
/// not on the plan, because the evidence is checked by <see cref="ArchiveCloseoutValidator"/> against the plan as it
/// would stand once archived. Nothing is moved until the evidence is complete and both indexes can be edited.
/// </para>
/// <para>
/// The plan's own <c>last-reviewed</c> is left alone: the gate compares it with the mapped documents' review dates.
/// </para>
/// </remarks>
public sealed partial class PlanCloseout
{
    /// <summary>The path is not an active plan this command can archive.</summary>
    public const string NotAnActivePlan = "KW-DOC-CLOSEOUT-001";

    /// <summary>The archive already holds a file with the plan's name.</summary>
    public const string ArchiveTargetExists = "KW-DOC-CLOSEOUT-002";

    /// <summary>An index has no table to take the row out of or add it to.</summary>
    public const string IndexNotEditable = "KW-DOC-CLOSEOUT-003";

    /// <summary>The plan's task list still has unchecked tasks, so archiving it would record unfinished work as done.</summary>
    public const string IncompleteTaskList = "KW-DOC-CLOSEOUT-006";

    /// <summary>A step done, or a step a preview would do.</summary>
    public const string Step = "KW-DOC-CLOSEOUT-004";

    private const string InventoryColumns = "| Plan | Status | Date | Archive Date | Canonical Docs / Harvested ADRs | Goal |";

    private readonly string _repoRoot;
    private readonly IPathResolver _paths;
    private readonly IProcessEnvironment _environment;
    private readonly KyberWeaveConfig _config;
    private readonly Func<IDocumentSource> _loaderFactory;
    private readonly ActivePlanFiles _files;
    private readonly IRepositoryFileStore _store;
    private readonly IExclusiveFileWriter _exclusive;

    /// <summary>
    /// Test seam, called with the plan's absolute path once the plan has passed the active-plan check and before it is
    /// read: the moment a plan can vanish between the check and the read.
    /// </summary>
    internal Action<string>? AfterActivePlanCheck { get; init; }

    /// <summary>
    /// Test seam, called with the archive target's absolute path just before each exclusive create: the moment another
    /// process can put a file there after the existence check.
    /// </summary>
    internal Action<string>? BeforeWriteTarget { get; init; }

    /// <summary>Creates the closeout over a repository, its effective configuration, the loader the gate reads with and the ports it writes through.</summary>
    public PlanCloseout(
        string repoRoot,
        KyberWeaveConfig config,
        Func<IDocumentSource> loaderFactory,
        IRepositoryFileStore files,
        IBoundedProcessExecutor process,
        IExclusiveFileWriter exclusive,
        IPathResolver paths,
        IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(exclusive);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loaderFactory);
        _repoRoot = paths.GetFullPath(repoRoot, environment.CurrentDirectory);
        _paths = paths;
        _environment = environment;
        _config = config;
        _loaderFactory = loaderFactory;
        _store = files;
        _exclusive = exclusive;
        _files = new ActivePlanFiles(_repoRoot, config, files, process);
    }

    /// <summary>Checks the plan's closeout evidence and, with <paramref name="archive"/>, archives it.</summary>
    /// <param name="planPath">The plan, relative to the repository root.</param>
    /// <param name="date">The archive date.</param>
    /// <param name="archive">True to move the plan; false to check and report what would happen.</param>
    public PlanCloseoutOutcome Run(string planPath, DateOnly date, bool archive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planPath);

        DiagnosticReport report = new();
        string plan = planPath.Replace('\\', '/');
        string name = Path.GetFileName(plan);

        if (_files.CheckActivePlan(plan) is { } notActive)
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, notActive, plan, plan,
                Hint: "Name a plan file directly under the plans folder, not its index and not an archived plan."));
            return new PlanCloseoutOutcome(report, false);
        }

        string root = _files.DocsRootOf(plan);
        string archiveFolder = Join(root, OntologyConfig.ArchiveSegment, DocsLayout.Plans);
        string target = Join(archiveFolder, name);
        string inventory = Join(archiveFolder, "README.md");
        string planIndex = _files.PlanIndexPath()!;

        if (_store.FileExists(_files.Absolute(target)))
        {
            report.Add(new Diagnostic(ArchiveTargetExists, Severity.Error,
                $"The archive already holds '{target}'.", plan, target,
                Hint: "Rename the plan, remove the earlier archive entry if it is a stale copy, or make the archive folder writable."));
            return new PlanCloseoutOutcome(report, false);
        }

        AfterActivePlanCheck?.Invoke(_files.Absolute(plan));

        // Every problem is collected before anything is reported: the caller is an agent that fixes what it is
        // told, so a refusal that names one gap per run costs a run per gap. A file that is there but cannot be
        // read is one of those problems, never an exception that skips the report.
        string? planIndexText = ReadPlanIndex(plan, planIndex, report);

        if (!TryReadPlan(plan, report, out string? planText))
            return new PlanCloseoutOutcome(report, false);

        // A plan's links are written for the folder it is in, and the archive is one folder deeper. Re-expressing them
        // before the copy is written is what keeps the archived plan's links, and the inventory row copied from its
        // Closeout mapping, pointing at the same files they pointed at before the move.
        string planFolder = Path.GetDirectoryName(plan)?.Replace('\\', '/') ?? string.Empty;
        string archivedText = RelativeLinkRebaser.Rebase(planText, planFolder, archiveFolder, out int rewrittenLinks);
        archivedText = WithArchivedStatus(archivedText, date);
        string taskFile = PlanTaskFileGenerator.TaskFilePathOf(plan);
        bool hasTaskFile = _store.FileExists(_files.Absolute(taskFile));

        // An archived plan is the record a reader checks the work against, so every task it lists as open
        // would read as unfinished work shipped as done. The tasks are ticked by the documentation developer
        // while the closeout evidence is written; this gate is what makes that step impossible to skip.
        List<string> uncheckedTasks = [.. PlanTaskCheckboxes.Unchecked(planText)];
        if (uncheckedTasks.Count > 0)
        {
            string ids = string.Join(", ", uncheckedTasks.Take(5));
            report.Add(new Diagnostic(IncompleteTaskList, Severity.Error,
                $"'{plan}' still lists {uncheckedTasks.Count} task(s) unchecked: {ids}.",
                plan, plan,
                Hint: "Tick every task that completed: '- [ ] <id>' becomes '- [x] <id>'. A task that did not " +
                "complete needs an escalation, and the plan stays open until it has one. Then run the closeout again."));
        }

        // The archive gate reads the plan where it would be archived, so the copy goes there for the check and
        // is gone again before this method returns, whatever the outcome.
        string? newInventory = null;
        string? newPlanIndex = null;
        bool planRowRemoved = false;
        string inventoryAction;
        string row;
        string archiveDirectory = Path.GetDirectoryName(_files.Absolute(target))!;
        string? createdDirectory = FirstMissingDirectory(archiveDirectory);
        _store.CreateDirectory(archiveDirectory);
        bool wroteTarget = false;
        try
        {
            BeforeWriteTarget?.Invoke(_files.Absolute(target));
            if (!_exclusive.TryWriteNew(_files.Absolute(target), archivedText))
            {
                report.Add(new Diagnostic(ArchiveTargetExists, Severity.Error,
                    $"The archive already holds '{target}'.", plan, target,
                    Hint: "Rename the plan, remove the earlier archive entry if it is a stale copy, or make the archive folder writable."));
                return new PlanCloseoutOutcome(report, false);
            }

            wroteTarget = true;
            DocumentSet live = _loaderFactory().Load();
            report.AddRange(new ArchiveCloseoutValidator(_repoRoot, _config, _loaderFactory, _store, _paths, _environment)
                .Itemise(live, [new ChangedPath(target, ChangeKind.Added)]).Items);
            if (report.HasErrors)
            {
                report.Add(new Diagnostic(Step, Severity.Info,
                    "Nothing was moved or changed. Fix every error above, then run the closeout again.", plan, plan));
                return new PlanCloseoutOutcome(report, false);
            }

            row = InventoryRow(_loaderFactory().LoadArchivedDocument(target), name, archivedText, date);
            try
            {
                (newInventory, inventoryAction) = WithInventoryRow(inventory, planIndexText ?? string.Empty, row, date);
            }
            catch (Exception exception) when (FileFailure.Is(exception))
            {
                report.Add(new Diagnostic(IndexNotEditable, Severity.Error, $"'{inventory}' could not be read: {exception.Message}", plan, inventory,
                    Hint: "Make the archived plan inventory a readable file, then run the closeout again."));
                return new PlanCloseoutOutcome(report, false);
            }

            (newPlanIndex, planRowRemoved) = RemovePlanRow(planIndexText ?? string.Empty, name);
        }
        finally
        {
            // Only a file this run created is removed: one that appeared before the exclusive create is someone else's.
            if (wroteTarget)
                _store.DeleteFile(_files.Absolute(target));
            if (createdDirectory is not null)
                _store.DeleteDirectory(createdDirectory, recursive: true);
        }

        List<string> touched = [plan, target, inventory, planIndex];
        if (hasTaskFile)
            touched.Add(taskFile);

        if (!archive)
        {
            ReportDryRunSteps(report, plan, archiveFolder, target, rewrittenLinks, hasTaskFile, taskFile, planIndex, planRowRemoved, inventory, inventoryAction, row);
            return new PlanCloseoutOutcome(report, false);
        }

        foreach (string linked in new[] { planIndex, inventory }.Where(file => PlanFinalize.IsLink(_store, _files.Absolute(file))))
        {
            report.Add(new Diagnostic(IndexNotEditable, Severity.Error,
                $"'{linked}' is a symbolic link, which this step does not write through. Nothing was moved.", plan, linked,
                Hint: "Make it a regular file, then run the closeout again."));
            return new PlanCloseoutOutcome(report, false);
        }

        _store.CreateDirectory(archiveDirectory);
        BeforeWriteTarget?.Invoke(_files.Absolute(target));
        if (!_exclusive.TryWriteNew(_files.Absolute(target), archivedText))
        {
            report.Add(new Diagnostic(ArchiveTargetExists, Severity.Error,
                $"The archive already holds '{target}'. Nothing was moved.", plan, target,
                Hint: "Rename the plan, remove the earlier archive entry if it is a stale copy, or make the archive folder writable."));
            return new PlanCloseoutOutcome(report, false);
        }

        _store.DeleteFile(_files.Absolute(plan));
        Say(report, plan, $"Moved {plan} to {target} with status archived.");
        if (rewrittenLinks > 0)
            Say(report, plan, $"Rewrote {rewrittenLinks} relative link{(rewrittenLinks == 1 ? string.Empty : "s")} so they still resolve from {archiveFolder}.");
        if (hasTaskFile)
        {
            _store.DeleteFile(_files.Absolute(taskFile));
            Say(report, plan, $"Removed {taskFile}.");
        }

        _store.WriteAllText(_files.Absolute(planIndex), newPlanIndex!);
        Say(report, plan, planRowRemoved ? $"Took its row out of {planIndex}." : $"{planIndex} had no row for it.");
        _store.WriteAllText(_files.Absolute(inventory), newInventory);
        Say(report, plan, $"Done: {inventoryAction} {inventory} with its row.");

        _files.Stage(touched, report, plan, Step);
        Say(report, plan, "Run 'docs validate . --merge-ready' and 'docs drift .' next.");
        return new PlanCloseoutOutcome(report, true);
    }

    /// <summary>
    /// Reads the plan index, naming the problem when it is missing or unreadable. A missing or
    /// unreadable index is a collected problem, never an exception that skips the report.
    /// </summary>
    private string? ReadPlanIndex(string plan, string planIndex, DiagnosticReport report)
    {
        try
        {
            string? text = _store.ReadTextIfPresent(_files.Absolute(planIndex));
            if (text is null)
            {
                report.Add(new Diagnostic(IndexNotEditable, Severity.Error, $"'{planIndex}' does not exist.", plan, planIndex,
                    Hint: $"Restore it with 'git checkout -- {planIndex}', then run the closeout again."));
            }

            return text;
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            report.Add(new Diagnostic(IndexNotEditable, Severity.Error, $"'{planIndex}' could not be read: {exception.Message}", plan, planIndex,
                Hint: "Make the plan index a readable file, then run the closeout again."));
            return null;
        }
    }

    /// <summary>
    /// Reads the plan, naming the problem when it cannot be read or is gone. False refuses the
    /// closeout: without the plan's text nothing later can be computed.
    /// </summary>
    private bool TryReadPlan(string plan, DiagnosticReport report, [NotNullWhen(true)] out string? planText)
    {
        try
        {
            planText = _store.ReadTextIfPresent(_files.Absolute(plan));
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, $"'{plan}' could not be read: {exception.Message}", plan, plan,
                Hint: "Make the plan a readable file, then run the closeout again."));
            planText = null;
            return false;
        }

        if (planText is null)
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, $"'{plan}' does not exist.", plan, plan,
                Hint: "Name a plan file directly under the plans folder, not its index and not an archived plan."));
            return false;
        }

        return true;
    }

    /// <summary>The dry-run summary: what the archive would do, without doing it.</summary>
    private static void ReportDryRunSteps(
        DiagnosticReport report,
        string plan,
        string archiveFolder,
        string target,
        int rewrittenLinks,
        bool hasTaskFile,
        string taskFile,
        string planIndex,
        bool planRowRemoved,
        string inventory,
        string inventoryAction,
        string row)
    {
        if (rewrittenLinks > 0)
            Say(report, plan, $"Would rewrite {rewrittenLinks} relative link{(rewrittenLinks == 1 ? string.Empty : "s")} so they still resolve from {archiveFolder}.");
        Say(report, plan, $"Closeout evidence is complete. With --archive (and without --dry-run) this would move {plan} to {target}.");
        Say(report, plan, hasTaskFile ? $"Would remove {taskFile}." : "No task file to remove.");
        Say(report, plan, planRowRemoved ? $"Would take its row out of {planIndex}." : $"{planIndex} has no row for it.");
        Say(report, plan, $"Would {inventoryAction} {inventory} with this row: {row}");
    }

    /// <summary>The outermost directory on the way to <paramref name="directory"/> that does not exist yet, or null.</summary>
    private string? FirstMissingDirectory(string directory)
    {
        string? missing = null;
        for (string? current = directory; current is not null && !_store.DirectoryExists(current); current = Path.GetDirectoryName(current))
            missing = current;
        return missing;
    }

    private static void Say(DiagnosticReport report, string plan, string message) =>
        report.Add(new Diagnostic(Step, Severity.Info, message, plan, plan));

    private static string Join(params string[] parts) => string.Join('/', parts.Where(part => part.Length > 0));

    /// <summary>The plan text with its frontmatter status and its body status line set to archived.</summary>
    private static string WithArchivedStatus(string text, DateOnly date)
    {
        string stamp = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        int frontEnd = text.StartsWith("---", StringComparison.Ordinal)
            ? text.IndexOf("\n---", 3, StringComparison.Ordinal)
            : -1;
        string front = frontEnd < 0 ? string.Empty : text[..frontEnd];
        string body = frontEnd < 0 ? text : text[frontEnd..];

        front = FrontmatterStatus().Replace(front, "status: archived", 1);
        body = BodyStatusValue().IsMatch(body)
            ? BodyStatusValue().Replace(body, $"**Status:** {PlanStatus.Archived}, {stamp}", 1)
            : BodyStatusBold().Replace(body, $"**Status: {PlanStatus.Archived}, {stamp}.**", 1);
        return front + body;
    }

    private string InventoryRow(DocumentModel archived, string name, string archivedText, DateOnly date)
    {
        string stamp = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string created = Created().Match(archivedText) is { Success: true } match ? match.Groups[1].Value : string.Empty;
        List<string> destinations = [];
        foreach (string[] cells in ArchiveCloseoutValidator.TableRows(archived, "Closeout mapping") ?? [])
        {
            string cell = cells.Length > 1 ? cells[1].Trim() : string.Empty;
            string entry = cell.Length == 0 ? string.Empty : $"{cell} ({cells[0].Trim()})";
            if (entry.Length > 0 && !destinations.Contains(entry, StringComparer.Ordinal))
                destinations.Add(entry);
        }

        string goal = (archived.Frontmatter.Title ?? name).Replace("|", "\\|", StringComparison.Ordinal);
        return $"| [{name}]({name}) | {PlanStatus.Archived} — complete {stamp} | {created} | {stamp} | " +
               $"{string.Join("; ", destinations).Replace("|", "\\|", StringComparison.Ordinal)} | {goal} |";
    }

    /// <summary>
    /// The archived plan inventory with <paramref name="row"/> in it, and what was done to get there. A missing
    /// inventory is created, and one without the table gets the table appended: both are mechanical and have one
    /// right shape, so refusing would only cost the caller a run.
    /// </summary>
    private (string Text, string Action) WithInventoryRow(string inventory, string planIndexText, string row, DateOnly date)
    {
        string table = $"{InventoryColumns}\n|---|---|---|---|---|---|\n{row}\n";
        if (_store.ReadTextIfPresent(_files.Absolute(inventory)) is not { } text)
        {
            string owner = Owner().Match(planIndexText) is { Success: true } match
                ? match.Groups[1].Value
                : "Maintainers";
            string stamp = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return ($"---\nid: {OntologyConfig.ArchiveSegment}/{DocsLayout.Plans}/index\ntitle: Archived Plan Inventory\n" +
                    $"doc-type: index\nstatus: current\nowner: {owner}\nlast-reviewed: {stamp}\n---\n\n" +
                    $"# Archived Plan Inventory\n\nPlans that finished and were archived, newest first.\n\n" +
                    $"## Archived Plans\n\n{table}", "create");
        }

        if (AddInventoryRow(text, row) is { } added)
            return (added, "add its row at the top of");

        string separator = text.EndsWith('\n') ? "\n" : "\n\n";
        return ($"{text}{separator}## Archived Plans\n\n{table}", "append the archived plans table to");
    }

    /// <summary>The inventory with <paramref name="row"/> at the top of its table, or null when it has no such table.</summary>
    private static string? AddInventoryRow(string text, string row)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        int header = lines.FindIndex(line => Squash(line).StartsWith(Squash(InventoryColumns), StringComparison.Ordinal));
        if (header < 0 || header + 1 >= lines.Count || !lines[header + 1].TrimStart().StartsWith("|-", StringComparison.Ordinal))
            return null;

        lines.Insert(header + 2, row);
        return string.Join(newline, lines);
    }

    private static string Squash(string line) => Regex.Replace(line, @"\s+", string.Empty);

    /// <summary>The plan index without the plan's table rows, and whether there were any.</summary>
    private static (string Text, bool Removed) RemovePlanRow(string text, string name)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        Regex link = new($@"\]\((?:\./)?{Regex.Escape(name)}(?:#[^)]*)?\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        int before = lines.Count;
        lines.RemoveAll(line => line.TrimStart().StartsWith('|') && link.IsMatch(line));
        bool removed = lines.Count < before;

        if (removed)
        {
            // A table with a header and a separator and no rows is the empty state's wrong shape.
            for (int index = 0; index + 1 < lines.Count; index++)
            {
                bool header = lines[index].TrimStart().StartsWith('|') && lines[index + 1].TrimStart().StartsWith("|-", StringComparison.Ordinal);
                bool rows = header && index + 2 < lines.Count && lines[index + 2].TrimStart().StartsWith('|');
                if (header && !rows)
                {
                    lines.RemoveRange(index, 2);
                    lines.Insert(index, "No plans are open.");
                    break;
                }
            }
        }

        return (string.Join(newline, lines), removed);
    }

    [GeneratedRegex(@"^status:[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FrontmatterStatus();

    [GeneratedRegex(@"^\*\*Status:\*\*[ \t]*[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BodyStatusValue();

    [GeneratedRegex(@"^\*\*Status:[^*\r\n]*\*\*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BodyStatusBold();

    [GeneratedRegex(@"^owner:[ \t]*(\S[^\r\n]*?)[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Owner();

    [GeneratedRegex(@"^created:[ \t]*(\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Created();
}
