using System.Globalization;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Review;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>What a plan closeout did, or would do.</summary>
/// <param name="Report">The findings, and one Info line per step taken or planned.</param>
/// <param name="Archived">Whether the plan was moved to the archive. False for a preview and for a refusal.</param>
public sealed record PlanCloseoutOutcome(DiagnosticReport Report, bool Archived);

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

    /// <summary>A step done, or a step a preview would do.</summary>
    public const string Step = "KW-DOC-CLOSEOUT-004";

    private const string PlansSegment = "plans";
    private const string InventoryColumns = "| Plan | Status | Date | Archive Date | Canonical Docs / Harvested ADRs | Goal |";

    private readonly string _repoRoot;
    private readonly KyberWeaveConfig _config;
    private readonly Func<DocumentLoader> _loaderFactory;
    private readonly ActivePlanFiles _files;

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

    /// <summary>Creates the closeout over a repository, its effective configuration and the loader the gate reads with.</summary>
    public PlanCloseout(string repoRoot, KyberWeaveConfig config, Func<DocumentLoader> loaderFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loaderFactory);
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _loaderFactory = loaderFactory;
        _files = new ActivePlanFiles(_repoRoot, config);
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
        string archiveFolder = Join(root, OntologyConfig.ArchiveSegment, PlansSegment);
        string target = Join(archiveFolder, name);
        string inventory = Join(archiveFolder, "README.md");
        string planIndex = _files.PlanIndexPath()!;

        if (File.Exists(_files.Absolute(target)))
        {
            report.Add(new Diagnostic(ArchiveTargetExists, Severity.Error,
                $"The archive already holds '{target}'.", plan, target,
                Hint: "Rename the plan, or remove the earlier archive entry if it is a stale copy."));
            return new PlanCloseoutOutcome(report, false);
        }

        AfterActivePlanCheck?.Invoke(_files.Absolute(plan));

        // Every problem is collected before anything is reported: the caller is an agent that fixes what it is
        // told, so a refusal that names one gap per run costs a run per gap. A file that is there but cannot be
        // read is one of those problems, never an exception that skips the report.
        string? planIndexText = null;
        try
        {
            planIndexText = PresentFile.ReadIfPresent(_files.Absolute(planIndex));
            if (planIndexText is null)
            {
                report.Add(new Diagnostic(IndexNotEditable, Severity.Error, $"'{planIndex}' does not exist.", plan, planIndex,
                    Hint: $"Restore it with 'git checkout -- {planIndex}', then run the closeout again."));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(IndexNotEditable, Severity.Error, $"'{planIndex}' could not be read: {exception.Message}", plan, planIndex,
                Hint: "Make the plan index a readable file, then run the closeout again."));
        }

        string? planText;
        try
        {
            planText = PresentFile.ReadIfPresent(_files.Absolute(plan));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, $"'{plan}' could not be read: {exception.Message}", plan, plan,
                Hint: "Make the plan a readable file, then run the closeout again."));
            return new PlanCloseoutOutcome(report, false);
        }

        if (planText is null)
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, $"'{plan}' does not exist.", plan, plan,
                Hint: "Name a plan file directly under the plans folder, not its index and not an archived plan."));
            return new PlanCloseoutOutcome(report, false);
        }

        string archivedText = WithArchivedStatus(planText, date);
        string taskFile = PlanTaskFileGenerator.TaskFilePathOf(plan);
        bool hasTaskFile = File.Exists(_files.Absolute(taskFile));

        // The archive gate reads the plan where it would be archived, so the copy goes there for the check and
        // is gone again before this method returns, whatever the outcome.
        string? newInventory = null;
        string? newPlanIndex = null;
        bool planRowRemoved = false;
        string inventoryAction;
        string row;
        string archiveDirectory = Path.GetDirectoryName(_files.Absolute(target))!;
        string? createdDirectory = FirstMissingDirectory(archiveDirectory);
        Directory.CreateDirectory(archiveDirectory);
        bool wroteTarget = false;
        try
        {
            BeforeWriteTarget?.Invoke(_files.Absolute(target));
            if (!TryWriteNew(_files.Absolute(target), archivedText))
            {
                report.Add(new Diagnostic(ArchiveTargetExists, Severity.Error,
                    $"The archive already holds '{target}'.", plan, target,
                    Hint: "Rename the plan, or remove the earlier archive entry if it is a stale copy."));
                return new PlanCloseoutOutcome(report, false);
            }

            wroteTarget = true;
            DocumentSet live = _loaderFactory().Load();
            report.AddRange(new ArchiveCloseoutValidator(_repoRoot, _config, _loaderFactory)
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
                File.Delete(_files.Absolute(target));
            if (createdDirectory is not null)
                Directory.Delete(createdDirectory, recursive: true);
        }

        List<string> touched = [plan, target, inventory, planIndex];
        if (hasTaskFile)
            touched.Add(taskFile);

        if (!archive)
        {
            Say(report, plan, $"Closeout evidence is complete. With --archive (and without --dry-run) this would move {plan} to {target}.");
            Say(report, plan, hasTaskFile ? $"Would remove {taskFile}." : "No task file to remove.");
            Say(report, plan, planRowRemoved ? $"Would take its row out of {planIndex}." : $"{planIndex} has no row for it.");
            Say(report, plan, $"Would {inventoryAction} {inventory} with this row: {row}");
            return new PlanCloseoutOutcome(report, false);
        }

        Directory.CreateDirectory(archiveDirectory);
        BeforeWriteTarget?.Invoke(_files.Absolute(target));
        if (!TryWriteNew(_files.Absolute(target), archivedText))
        {
            report.Add(new Diagnostic(ArchiveTargetExists, Severity.Error,
                $"The archive already holds '{target}'. Nothing was moved.", plan, target,
                Hint: "Rename the plan, or remove the earlier archive entry if it is a stale copy."));
            return new PlanCloseoutOutcome(report, false);
        }

        File.Delete(_files.Absolute(plan));
        Say(report, plan, $"Moved {plan} to {target} with status archived.");
        if (hasTaskFile)
        {
            File.Delete(_files.Absolute(taskFile));
            Say(report, plan, $"Removed {taskFile}.");
        }

        File.WriteAllText(_files.Absolute(planIndex), newPlanIndex!);
        Say(report, plan, planRowRemoved ? $"Took its row out of {planIndex}." : $"{planIndex} had no row for it.");
        File.WriteAllText(_files.Absolute(inventory), newInventory);
        Say(report, plan, $"Done: {inventoryAction} {inventory} with its row.");

        _files.Stage(touched, report, plan, Step);
        Say(report, plan, "Run 'docs validate . --merge-ready' and 'docs drift .' next.");
        return new PlanCloseoutOutcome(report, true);
    }

    /// <summary>
    /// Writes <paramref name="text"/> to a file that must not exist yet, in one step, so a file that appeared since the
    /// last look is never overwritten. False when the file is already there. A failure while writing (a full disk, an
    /// unencodable character) removes the partial file this call created and propagates, so it is never mistaken for
    /// "already there" and never leaves a truncated archive behind.
    /// </summary>
    internal static bool TryWriteNew(string path, string text)
    {
        bool created = false;
        try
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            using StreamWriter writer = new(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
            writer.Write(text);
            return true;
        }
        catch (IOException) when (!created && File.Exists(path))
        {
            return false;
        }
        catch when (created)
        {
            File.Delete(path);
            throw;
        }
    }

    /// <summary>The outermost directory on the way to <paramref name="directory"/> that does not exist yet, or null.</summary>
    private static string? FirstMissingDirectory(string directory)
    {
        string? missing = null;
        for (string? current = directory; current is not null && !Directory.Exists(current); current = Path.GetDirectoryName(current))
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
            ? BodyStatusValue().Replace(body, $"**Status:** Archived, {stamp}", 1)
            : BodyStatusBold().Replace(body, $"**Status: Archived, {stamp}.**", 1);
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
        return $"| [{name}]({name}) | Archived — complete {stamp} | {created} | {stamp} | " +
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
        if (PresentFile.ReadIfPresent(_files.Absolute(inventory)) is not { } text)
        {
            string owner = Owner().Match(planIndexText) is { Success: true } match
                ? match.Groups[1].Value
                : "Maintainers";
            string stamp = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return ($"---\nid: {OntologyConfig.ArchiveSegment}/{PlansSegment}/index\ntitle: Archived Plan Inventory\n" +
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
