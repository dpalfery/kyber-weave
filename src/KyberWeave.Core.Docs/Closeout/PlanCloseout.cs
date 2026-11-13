using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
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
    private static readonly string[] StageArguments = ["add", "-A", "--"];
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private readonly string _repoRoot;
    private readonly KyberWeaveConfig _config;
    private readonly Func<DocumentLoader> _loaderFactory;

    /// <summary>Creates the closeout over a repository, its effective configuration and the loader the gate reads with.</summary>
    public PlanCloseout(string repoRoot, KyberWeaveConfig config, Func<DocumentLoader> loaderFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loaderFactory);
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _loaderFactory = loaderFactory;
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

        if (CheckActivePlan(plan) is { } notActive)
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, notActive, plan, plan,
                Hint: "Name a plan file directly under the plans folder, not its index and not an archived plan."));
            return new PlanCloseoutOutcome(report, false);
        }

        string root = DocsRootOf(plan);
        string archiveFolder = Join(root, OntologyConfig.ArchiveSegment, PlansSegment);
        string target = Join(archiveFolder, name);
        string inventory = Join(archiveFolder, "README.md");
        string planIndex = PlanIndexPath()!;

        if (File.Exists(Absolute(target)))
        {
            report.Add(new Diagnostic(ArchiveTargetExists, Severity.Error,
                $"The archive already holds '{target}'.", plan, target,
                Hint: "Rename the plan, or remove the earlier archive entry if it is a stale copy."));
            return new PlanCloseoutOutcome(report, false);
        }

        foreach (string index in new[] { inventory, planIndex }.Where(index => !File.Exists(Absolute(index))))
        {
            report.Add(new Diagnostic(IndexNotEditable, Severity.Error, $"'{index}' does not exist.", plan, index,
                Hint: "Restore the index, then run the closeout again."));
            return new PlanCloseoutOutcome(report, false);
        }

        string archivedText = WithArchivedStatus(File.ReadAllText(Absolute(plan)), date);
        string taskFile = PlanTaskFileGenerator.TaskFilePathOf(plan);
        bool hasTaskFile = File.Exists(Absolute(taskFile));

        // The archive gate reads the plan where it would be archived, so the copy goes there for the check and
        // is gone again before this method returns, whatever the outcome.
        string? newInventory = null;
        string? newPlanIndex = null;
        bool planRowRemoved = false;
        try
        {
            File.WriteAllText(Absolute(target), archivedText);
            DocumentSet live = _loaderFactory().Load();
            report.AddRange(new ArchiveCloseoutValidator(_repoRoot, _config, _loaderFactory)
                .Validate(live, [new ChangedPath(target, ChangeKind.Added)]).Items);
            if (report.HasErrors)
            {
                report.Add(new Diagnostic(Step, Severity.Info, "Nothing was moved or changed.", plan, plan));
                return new PlanCloseoutOutcome(report, false);
            }

            string row = InventoryRow(_loaderFactory().LoadArchivedDocument(target), name, archivedText, date);
            newInventory = AddInventoryRow(File.ReadAllText(Absolute(inventory)), row);
            (newPlanIndex, planRowRemoved) = RemovePlanRow(File.ReadAllText(Absolute(planIndex)), name);
            if (newInventory is null)
            {
                report.Add(new Diagnostic(IndexNotEditable, Severity.Error,
                    $"'{inventory}' has no archived plans table with the columns {InventoryColumns}.", plan, inventory,
                    Hint: "Restore the table header and separator, then run the closeout again."));
                return new PlanCloseoutOutcome(report, false);
            }
        }
        finally
        {
            File.Delete(Absolute(target));
        }

        List<string> touched = [plan, target, inventory, planIndex];
        if (hasTaskFile)
            touched.Add(taskFile);

        if (!archive)
        {
            Say(report, plan, $"Closeout evidence is complete. With --archive: move {plan} to {target}.");
            Say(report, plan, hasTaskFile ? $"Remove {taskFile}." : "No task file to remove.");
            Say(report, plan, planRowRemoved ? $"Take its row out of {planIndex}." : $"{planIndex} has no row for it.");
            Say(report, plan, $"Add its row at the top of {inventory}.");
            return new PlanCloseoutOutcome(report, false);
        }

        File.WriteAllText(Absolute(target), archivedText);
        File.Delete(Absolute(plan));
        Say(report, plan, $"Moved {plan} to {target} with status archived.");
        if (hasTaskFile)
        {
            File.Delete(Absolute(taskFile));
            Say(report, plan, $"Removed {taskFile}.");
        }

        File.WriteAllText(Absolute(planIndex), newPlanIndex!);
        Say(report, plan, planRowRemoved ? $"Took its row out of {planIndex}." : $"{planIndex} had no row for it.");
        File.WriteAllText(Absolute(inventory), newInventory);
        Say(report, plan, $"Added its row at the top of {inventory}.");

        Stage(touched, report, plan);
        Say(report, plan, "Run 'docs validate . --merge-ready' and 'docs drift .' next.");
        return new PlanCloseoutOutcome(report, true);
    }

    private static void Say(DiagnosticReport report, string plan, string message) =>
        report.Add(new Diagnostic(Step, Severity.Info, message, plan, plan));

    /// <summary>Why <paramref name="plan"/> is not an active plan, or null when it is.</summary>
    private string? CheckActivePlan(string plan)
    {
        string? index = PlanIndexPath();
        if (index is null)
            return "The configuration registers no plan index, so no plan is active.";

        string folder = Path.GetDirectoryName(index)?.Replace('\\', '/') ?? string.Empty;
        if (!File.Exists(Absolute(plan)))
            return $"'{plan}' does not exist.";

        string name = Path.GetFileName(plan);
        bool directlyInFolder = string.Equals(Path.GetDirectoryName(plan)?.Replace('\\', '/') ?? string.Empty, folder,
            StringComparison.OrdinalIgnoreCase);
        if (!directlyInFolder || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(plan, index, StringComparison.OrdinalIgnoreCase))
            return $"'{plan}' is not a plan file directly under '{folder}'.";

        return DocsRootOf(plan).Length == 0 && _config.Ontology.DocsRoots.All(root => root != ".")
            ? $"'{plan}' is under no documentation root."
            : null;
    }

    private string? PlanIndexPath() =>
        _config.ConfigReg.Resolve(_config.Ontology)
            .FirstOrDefault(entry => string.Equals(entry.Name, ConfigRegConfig.PlanIndexProperty, StringComparison.Ordinal))
            ?.Path;

    /// <summary>The documentation root a path sits under; empty for the repository root itself.</summary>
    private string DocsRootOf(string relative)
    {
        foreach (string root in _config.Ontology.DocsRoots.OrderByDescending(root => root.Length))
        {
            if (root == "." || relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return root == "." ? string.Empty : root;
        }

        return string.Empty;
    }

    private static string Join(params string[] parts) => string.Join('/', parts.Where(part => part.Length > 0));

    private string Absolute(string relative) =>
        Path.Combine(_repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

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
        string created = Created().Match(archivedText) is { Success: true } match ? match.Groups[1].Value : stamp;
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

    /// <summary>Stages the paths the closeout touched, so a commit that only takes tracked files cannot leave the new archive entry behind.</summary>
    private void Stage(List<string> paths, DiagnosticReport report, string plan)
    {
        if (!Directory.Exists(Path.Combine(_repoRoot, ".git")) && !File.Exists(Path.Combine(_repoRoot, ".git")))
            return;

        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = _repoRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in StageArguments.Concat(paths))
            startInfo.ArgumentList.Add(argument);

        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
            if (result.ExitCode == 0)
            {
                Say(report, plan, "Staged the moved and edited files.");
                return;
            }

            report.Add(new Diagnostic(Step, Severity.Warning, $"git add failed ({result.StandardError.Trim()}); stage the files before committing.", plan, plan));
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException)
        {
            report.Add(new Diagnostic(Step, Severity.Warning, $"git could not be run ({ex.Message}); stage the files before committing.", plan, plan));
        }
    }

    [GeneratedRegex(@"^status:[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FrontmatterStatus();

    [GeneratedRegex(@"^\*\*Status:\*\*[ \t]*[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BodyStatusValue();

    [GeneratedRegex(@"^\*\*Status:[^*\r\n]*\*\*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BodyStatusBold();

    [GeneratedRegex(@"^created:\s*(\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Created();
}
