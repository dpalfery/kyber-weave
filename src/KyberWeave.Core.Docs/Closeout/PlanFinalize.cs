using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>Who approved a plan for execution.</summary>
public enum PlanApprovalMode
{
    /// <summary>The conductor accepted the recommendation under autonomy <c>autonomous</c>.</summary>
    Autonomous,

    /// <summary>The owner approved the plan at the approve-and-execute gate.</summary>
    Supervised
}

/// <summary>What a plan finalize did.</summary>
/// <param name="Report">The findings, and one Info line per step taken.</param>
/// <param name="Finalized">True when this run moved the plan from Draft to Ready.</param>
/// <param name="AlreadyReady">True when the plan was already Ready with a current task file, so nothing changed.</param>
/// <param name="TaskFilePath">The task file beside the plan, repository-relative.</param>
/// <param name="ReadyQueue">One line per task, from the task file, then the tasks that may start now; null when refused.</param>
public sealed record PlanFinalizeOutcome(
    DiagnosticReport Report, bool Finalized, bool AlreadyReady, string TaskFilePath, string? ReadyQueue = null);

/// <summary>
/// Does the mechanical half of finalizing an approved Draft plan: records the approval in its Decisions table, sets
/// the frontmatter, body and index status to Ready, writes its task file, and commits those three files.
/// </summary>
/// <remarks>
/// The judgement half stays with the architect: a plan with an open decision, one the task file cannot be derived from,
/// or one that then fails validation is refused, naming what is wrong, and nothing is changed. Under autonomy a
/// decision still marked <c>RECOMMENDED</c> is accepted as recommended. The class edits only the plan, the plan index
/// and the task file, commits only those, never pushes, and refuses any path that is not a plan file directly under
/// the plans folder, or a checkout with other files staged.
/// </remarks>
public sealed partial class PlanFinalize
{
    /// <summary>The path is not an active plan.</summary>
    public const string NotAnActivePlan = "KW-DOC-FINALIZE-001";

    /// <summary>The plan is neither Draft nor Ready.</summary>
    public const string NotDraft = "KW-DOC-FINALIZE-002";

    /// <summary>The approval is missing or unusable.</summary>
    public const string NoApproval = "KW-DOC-FINALIZE-003";

    /// <summary>The plan has no Decisions table, or a decision is still open.</summary>
    public const string OpenDecisions = "KW-DOC-FINALIZE-004";

    /// <summary>The task file could not be written from the plan, or the finalized plan failed validation.</summary>
    public const string PlanNeedsArchitect = "KW-DOC-FINALIZE-005";

    /// <summary>Other files are staged, so the plan files cannot be committed alone.</summary>
    public const string OtherFilesStaged = "KW-DOC-FINALIZE-008";

    /// <summary>A step done.</summary>
    public const string Step = "KW-DOC-FINALIZE-006";

    /// <summary>The longest approval line recorded.</summary>
    public const int MaxApprovalLength = 300;

    private const string ArchitectHint =
        "Send the plan back to architect with these findings; the finalize step never edits plan content.";

    private readonly string _repoRoot;
    private readonly KyberWeaveConfig _config;
    private readonly Func<DocumentLoader> _loaderFactory;
    private readonly ActivePlanFiles _files;
    private readonly IPlanTaskAgentPicker? _picker;

    /// <summary>Creates the finalize step over a repository, its effective configuration and the loader validation reads with.</summary>
    public PlanFinalize(string repoRoot, KyberWeaveConfig config, Func<DocumentLoader> loaderFactory)
        : this(repoRoot, config, loaderFactory, null)
    {
    }

    /// <summary>Creates the finalize step with a picker that chooses each unassigned task's agent as the task file is written.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="loaderFactory">The loader validation reads with.</param>
    /// <param name="picker">The agent picker, or null when no decision model is configured.</param>
    public PlanFinalize(string repoRoot, KyberWeaveConfig config, Func<DocumentLoader> loaderFactory, IPlanTaskAgentPicker? picker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loaderFactory);
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _loaderFactory = loaderFactory;
        _files = new ActivePlanFiles(_repoRoot, config);
        _picker = picker;
    }

    /// <summary>Finalizes the approved Draft plan at <paramref name="planPath"/>, or refuses and changes nothing.</summary>
    /// <param name="planPath">The plan, relative to the repository root.</param>
    /// <param name="mode">Who approved it.</param>
    /// <param name="approval">One line saying what approved it, recorded in the Decisions table.</param>
    /// <param name="date">The approval date.</param>
    public PlanFinalizeOutcome Run(string planPath, PlanApprovalMode mode, string? approval, DateOnly date)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planPath);

        DiagnosticReport report = new();
        string plan = planPath.Replace('\\', '/');
        string taskFile = PlanTaskFileGenerator.TaskFilePathOf(plan);
        PlanFinalizeOutcome Refused() => new(report, false, false, taskFile);

        if (_files.CheckActivePlan(plan) is { } notActive)
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, notActive, plan, plan,
                Hint: "Name a plan file directly under the plans folder, not its index and not an archived plan."));
            return Refused();
        }

        if (PresentFile.ReadIfPresent(_files.Absolute(plan)) is not { } planText)
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, $"'{plan}' does not exist.", plan, plan,
                Hint: "Name a plan file directly under the plans folder, not its index and not an archived plan."));
            return Refused();
        }

        if (ReadyStatus().IsMatch(planText))
            return AlreadyReady(plan, report, taskFile);

        if (!DraftStatus().IsMatch(planText))
        {
            report.Add(new Diagnostic(NotDraft, Severity.Error,
                $"'{plan}' is not a Draft plan: its body has no '**Status: Draft**' line.", plan, plan,
                Hint: "Only an approved Draft plan is finalized. Ask architect to finish the Draft first."));
            return Refused();
        }

        string? planIndex = _files.PlanIndexPath();
        string? indexText;
        try
        {
            indexText = planIndex is null ? null : PresentFile.ReadIfPresent(_files.Absolute(planIndex));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                $"The plan index could not be read, so nothing was changed: {exception.Message}", plan, plan, Hint: ArchitectHint));
            return Refused();
        }

        List<string> touched = [plan, taskFile];
        if (indexText is not null)
            touched.Add(planIndex!);
        if (HasOtherStaged(touched, plan, report))
            return Refused();

        string? line = ApprovalLine(approval, mode, planText, plan, report);
        if (line is null)
            return Refused();

        string? withApproval = WithApproval(planText, mode, line, date, plan, report);
        if (withApproval is null)
            return Refused();

        PlanTaskFileGenerator generator = new(_repoRoot, _config, _picker);
        PlanTaskFileOutcome preview = generator.Run(plan, PlanTaskFileMode.Preview);
        if (preview.Report.HasErrors)
        {
            report.AddRange(preview.Report.Items);
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                "The task file cannot be written from this plan. Nothing was changed.", plan, plan, Hint: ArchitectHint));
            return Refused();
        }

        string? newIndex = indexText is null ? null : WithReadyIndexRow(indexText, Path.GetFileName(plan));
        string newPlan = WithReadyStatus(withApproval, date);
        if (!ReadyStatus().IsMatch(newPlan))
        {
            report.Add(new Diagnostic(NotDraft, Severity.Error,
                $"The Draft status line of '{plan}' is not in a form this step can rewrite. Nothing was changed.", plan, plan,
                Hint: "Write the body status line as '**Status: Draft.**' or '**Status:** Draft'."));
            return Refused();
        }

        string? taskFileBefore;
        try
        {
            // One read answers both "is it there" and "what does it hold": null means absent at this read, and only then
            // may a restore delete it.
            taskFileBefore = PresentFile.ReadIfPresent(_files.Absolute(taskFile));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing has been written yet, so there is nothing to restore; restoring from an unread copy would delete the file.
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                $"The existing task file could not be read, so nothing was changed: {exception.Message}", plan, plan, Hint: ArchitectHint));
            return Refused();
        }

        try
        {
            File.WriteAllText(_files.Absolute(plan), newPlan);
            if (newIndex is not null)
                File.WriteAllText(_files.Absolute(planIndex!), newIndex);

            PlanTaskFileOutcome written = generator.Run(plan, PlanTaskFileMode.Write);
            report.AddRange(written.Report.Items.Where(item => item.Severity == Severity.Error));
            if (!report.HasErrors)
                report.AddRange(Validate(plan, taskFile, planIndex));

            if (report.HasErrors)
            {
                report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                    "The finalized plan failed validation, so every change was undone.", plan, plan, Hint: ArchitectHint));
                Restore(plan, planText, planIndex, indexText, taskFile, taskFileBefore);
                return Refused();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Restore(plan, planText, planIndex, indexText, taskFile, taskFileBefore);
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                $"Finalizing failed and every change was undone: {exception.Message}", plan, plan));
            return Refused();
        }

        Say(report, plan, $"Recorded the {(mode == PlanApprovalMode.Autonomous ? "autonomous" : "owner")} approval in the Decisions table and set {plan} to Ready.");
        Say(report, plan, newIndex is not null && !string.Equals(newIndex, indexText, StringComparison.Ordinal)
            ? $"Set its row in {planIndex} to Ready."
            : $"{planIndex} has no Draft row for it to change.");
        Say(report, plan, $"Wrote {taskFile}.");
        _files.Stage(touched, report, plan, Step);
        _files.CommitOnly(touched, CommitMessage(plan), report, plan, Step);
        return new PlanFinalizeOutcome(report, true, false, taskFile, ReadyQueue(taskFile));
    }

    /// <summary>Reads an approval mode, <c>autonomous</c> or <c>supervised</c>, case-insensitively.</summary>
    public static bool TryParseMode(string? value, out PlanApprovalMode mode)
    {
        mode = PlanApprovalMode.Supervised;
        if (string.Equals(value?.Trim(), "autonomous", StringComparison.OrdinalIgnoreCase))
        {
            mode = PlanApprovalMode.Autonomous;
            return true;
        }

        return string.Equals(value?.Trim(), "supervised", StringComparison.OrdinalIgnoreCase);
    }

    private PlanFinalizeOutcome AlreadyReady(string plan, DiagnosticReport report, string taskFile)
    {
        PlanTaskFileOutcome check = new PlanTaskFileGenerator(_repoRoot, _config).Run(plan, PlanTaskFileMode.Check);
        if (check.Report.HasErrors)
        {
            report.AddRange(check.Report.Items);
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                $"'{plan}' is already Ready but its task file is not current with it. Nothing was changed.", plan, plan,
                Hint: ArchitectHint));
            return new PlanFinalizeOutcome(report, false, false, taskFile);
        }

        Say(report, plan, $"{plan} is already Ready and {taskFile} is current; nothing changed.");
        List<string> own = [plan, taskFile];
        string? index = _files.PlanIndexPath();
        if (index is not null && PresentFile.ReadIfPresent(_files.Absolute(index)) is not null)
            own.Add(index);
        if (!HasOtherStaged(own, plan, report))
            _files.CommitOnly(own, CommitMessage(plan), report, plan, Step);
        return new PlanFinalizeOutcome(report, false, true, taskFile, ReadyQueue(taskFile));
    }

    private bool HasOtherStaged(IReadOnlyList<string> own, string plan, DiagnosticReport report)
    {
        if (_files.StagedOutside(own) is not { Count: > 0 } others)
            return false;

        report.Add(new Diagnostic(OtherFilesStaged, Severity.Error,
            $"Other files are staged ({string.Join(", ", others.Take(5))}), so the plan files cannot be committed alone. Nothing was changed.",
            plan, plan, Hint: "Unstage or commit them first, then call finalize again."));
        return true;
    }

    private static string CommitMessage(string plan)
    {
        string name = Path.GetFileNameWithoutExtension(plan);
        string slug = DatePrefix().Replace(name, string.Empty);
        return $"docs(plan): finalize {(slug.Length > 0 ? slug : name)} plan";
    }

    /// <summary>One line per task in the task file, then the tasks with no unmet dependency.</summary>
    private string? ReadyQueue(string taskFile)
    {
        try
        {
            return PresentFile.ReadIfPresent(_files.Absolute(taskFile)) is { } text ? ReadyQueueOf(text) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The queue for a task file's text. "(no tasks)" only for a file that is valid and lists an empty set of tasks;
    /// null for anything the task-file validator would refuse: not JSON, no or another schema version, no task list,
    /// or a null entry in it. A corrupt file must not read as a valid empty one.
    /// </summary>
    internal static string? ReadyQueueOf(string taskFileText)
    {
        try
        {
            PlanTaskFile? file = JsonSerializer.Deserialize<PlanTaskFile>(taskFileText, PlanTaskFileJson.ReadOptions);
            if (file is not { Tasks: { } tasks } || file.SchemaVersion != PlanTaskFileValidator.SchemaVersion || tasks.Any(task => task is null))
                return null;
            return tasks.Count > 0 ? PlanTaskPacket.ReadyQueue(tasks) : "(no tasks)";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ApprovalLine(string? approval, PlanApprovalMode mode, string planText, string plan, DiagnosticReport report)
    {
        string text = approval?.Trim() ?? string.Empty;
        string? problem = text.Length == 0 ? "No approval was given."
            : text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal) ? "The approval must be one line."
            : text.Length > MaxApprovalLength ? $"The approval is longer than {MaxApprovalLength} characters."
            : mode == PlanApprovalMode.Autonomous && SupervisedPlan().IsMatch(FrontmatterOf(planText))
                ? "The plan's frontmatter says 'autonomy: supervised', so only the owner can approve it."
                : null;
        if (problem is null)
            return text.Replace("|", "\\|", StringComparison.Ordinal);

        report.Add(new Diagnostic(NoApproval, Severity.Error, problem + " Nothing was changed.", plan, plan,
            Hint: "Finalize only once approval exists: the AUTONOMOUS_APPROVAL line in autonomous mode, the owner's answer at the approve-and-execute gate otherwise."));
        return null;
    }

    /// <summary>
    /// The plan text with the approval recorded: an open approval row (id starting <c>AP</c>, or a question asking to
    /// approve and execute) is answered in place, otherwise a new <c>AP</c> row is added. Under autonomy, other rows
    /// still marked <c>RECOMMENDED</c> are accepted as recommended. Null, with the reason reported, when the plan has
    /// no Decisions table or another decision is still open.
    /// </summary>
    private static string? WithApproval(string planText, PlanApprovalMode mode, string approval, DateOnly date, string plan, DiagnosticReport report)
    {
        string newline = planText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = [.. planText.Split('\n').Select(line => line.TrimEnd('\r'))];
        int heading = lines.FindIndex(line => DecisionsHeading().IsMatch(line));
        int header = heading < 0 ? -1 : lines.FindIndex(heading + 1, line => line.TrimStart().StartsWith('|'));
        if (header < 0 || header + 1 >= lines.Count || !lines[header + 1].TrimStart().StartsWith("|-", StringComparison.Ordinal))
        {
            report.Add(new Diagnostic(OpenDecisions, Severity.Error,
                "The plan has no '## Decisions' table to record the approval in. Nothing was changed.", plan, plan, Hint: ArchitectHint));
            return null;
        }

        int last = header + 1;
        while (last + 1 < lines.Count && lines[last + 1].TrimStart().StartsWith('|'))
            last++;

        string stamp = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string provenance = mode == PlanApprovalMode.Autonomous
            ? $"ANSWERED: autonomous: recommendation accepted, {approval}, {stamp}"
            : $"ANSWERED: owner approval, {approval}, {stamp}";

        List<string> open = [];
        List<int> accepted = [];
        int approvalRow = -1;
        int highest = 0;
        for (int index = header + 2; index <= last; index++)
        {
            string[] cells = Cells(lines[index]);
            if (cells.Length < 2)
                continue;

            string id = cells[0];
            bool isApproval = id.StartsWith("AP", StringComparison.OrdinalIgnoreCase);
            if (isApproval && int.TryParse(id.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                highest = Math.Max(highest, number);

            if (!OpenStatus().IsMatch(cells[^1]))
                continue;

            if ((isApproval || ApproveAndExecute().IsMatch(cells[1])) && approvalRow < 0)
                approvalRow = index;
            else if (mode == PlanApprovalMode.Autonomous && RecommendedStatus().IsMatch(cells[^1]))
                accepted.Add(index);
            else
                open.Add(id);
        }

        if (open.Count > 0)
        {
            report.Add(new Diagnostic(OpenDecisions, Severity.Error,
                $"Decisions still open: {string.Join(", ", open)}. Nothing was changed.", plan, plan, Hint: ArchitectHint));
            return null;
        }

        foreach (int index in accepted)
        {
            string[] cells = Cells(lines[index]);
            cells[^1] = $"autonomous: recommendation accepted, {stamp}";
            lines[index] = "| " + string.Join(" | ", cells) + " |";
        }

        if (approvalRow >= 0)
        {
            string[] cells = Cells(lines[approvalRow]);
            cells[^1] = provenance;
            lines[approvalRow] = "| " + string.Join(" | ", cells) + " |";
        }
        else
        {
            lines.Insert(last + 1,
                $"| AP{highest + 1} | Approve and execute | Approved for execution as planned. | " +
                $"Recorded by the finalize step, which changed no task, file scope, test row or command. | {provenance} |");
        }

        return string.Join(newline, lines);
    }

    private static string[] Cells(string row)
    {
        string trimmed = row.Trim();
        if (trimmed.StartsWith('|'))
            trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
            trimmed = trimmed[..^1];
        return [.. CellSeparator().Split(trimmed).Select(cell => cell.Trim())];
    }

    private static string FrontmatterOf(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal))
            return string.Empty;
        int end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? string.Empty : text[..end];
    }

    /// <summary>The plan text with its frontmatter status <c>current</c> and its body status line Ready.</summary>
    private static string WithReadyStatus(string text, DateOnly date)
    {
        string stamp = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string front = FrontmatterOf(text);
        string body = text[front.Length..];
        front = FrontmatterStatus().Replace(front, "status: current", 1);
        body = DraftValueLine().IsMatch(body)
            ? DraftValueLine().Replace(body, $"**Status:** Ready, {stamp}", 1)
            : DraftBold().Replace(body, $"**Status: Ready, {stamp}.**", 1);
        return front + body;
    }

    /// <summary>The plan index with the plan's row status set to Ready where its Status cell says Draft.</summary>
    private static string WithReadyIndexRow(string text, string name)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        Regex link = new($@"\]\((?:\./)?{Regex.Escape(name)}(?:#[^)]*)?\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        for (int index = 0; index < lines.Count; index++)
        {
            if (!lines[index].TrimStart().StartsWith('|') || !link.IsMatch(lines[index]))
                continue;

            int header = index;
            while (header > 0 && lines[header - 1].TrimStart().StartsWith('|'))
                header--;
            int column = Array.FindIndex(Cells(lines[header]), cell => string.Equals(cell, "Status", StringComparison.OrdinalIgnoreCase));
            string[] cells = Cells(lines[index]);
            if (column < 0 || column >= cells.Length || !cells[column].StartsWith("Draft", StringComparison.OrdinalIgnoreCase))
                continue;

            cells[column] = "Ready";
            lines[index] = "| " + string.Join(" | ", cells) + " |";
        }

        return string.Join(newline, lines);
    }

    private IEnumerable<Diagnostic> Validate(string plan, string taskFile, string? planIndex)
    {
        HashSet<string> own = new(StringComparer.OrdinalIgnoreCase) { plan, taskFile };
        if (planIndex is not null)
            own.Add(planIndex);

        DiagnosticReport validated = DocsSchemaGate.Validate(_repoRoot, _config, _loaderFactory().Load(), _loaderFactory, mergeReady: false);
        return validated.Items.Where(item => item.Severity == Severity.Error
                                             && (own.Contains(item.FilePath?.Replace('\\', '/') ?? string.Empty)
                                                 || own.Contains(item.Subject.Replace('\\', '/'))));
    }

    private void Restore(string plan, string planText, string? planIndex, string? indexText, string taskFile, string? taskFileBefore)
    {
        File.WriteAllText(_files.Absolute(plan), planText);
        if (planIndex is not null && indexText is not null)
            File.WriteAllText(_files.Absolute(planIndex), indexText);
        if (taskFileBefore is not null)
            File.WriteAllText(_files.Absolute(taskFile), taskFileBefore);
        else
            DeleteIfPresent(_files.Absolute(taskFile));
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // The folder is gone, so the file is too.
        }
    }

    private static void Say(DiagnosticReport report, string plan, string message) =>
        report.Add(new Diagnostic(Step, Severity.Info, message, plan, plan));

    [GeneratedRegex(@"^\*\*Status:(?:\*\*)?[ \t]*Ready(?![\w-])", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadyStatus();

    [GeneratedRegex(@"^\*\*Status:(?:\*\*)?[ \t]*Draft(?![\w-])", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DraftStatus();

    [GeneratedRegex(@"^\*\*Status:\*\*[ \t]*Draft[^\r\n]*", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DraftValueLine();

    [GeneratedRegex(@"^\*\*Status:[ \t]*Draft[^*\r\n]*\*\*", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DraftBold();

    [GeneratedRegex(@"^status:[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FrontmatterStatus();

    [GeneratedRegex(@"^autonomy:[ \t]*supervised[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SupervisedPlan();

    [GeneratedRegex(@"^##[ \t]+Decisions[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DecisionsHeading();

    [GeneratedRegex(@"^\W*(OPEN|RECOMMENDED)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenStatus();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-", RegexOptions.CultureInvariant)]
    private static partial Regex DatePrefix();

    [GeneratedRegex(@"^\W*RECOMMENDED\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RecommendedStatus();

    [GeneratedRegex(@"\bapprove\s+(?:and|&)\s+execute\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApproveAndExecute();

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex CellSeparator();
}
