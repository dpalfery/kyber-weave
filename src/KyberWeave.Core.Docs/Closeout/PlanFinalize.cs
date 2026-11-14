using System.Globalization;
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
public sealed record PlanFinalizeOutcome(DiagnosticReport Report, bool Finalized, bool AlreadyReady, string TaskFilePath);

/// <summary>
/// Does the mechanical half of finalizing an approved Draft plan: records the approval in its Decisions table, sets
/// the frontmatter, body and index status to Ready, and writes its task file.
/// </summary>
/// <remarks>
/// The judgement half stays with the architect: a plan with open decisions, one the task file cannot be derived from,
/// or one that then fails validation is refused, naming what is wrong, and nothing is changed. The class edits only the
/// plan, the plan index and the task file, and refuses any path that is not a plan file directly under the plans
/// folder.
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

    /// <summary>Creates the finalize step over a repository, its effective configuration and the loader validation reads with.</summary>
    public PlanFinalize(string repoRoot, KyberWeaveConfig config, Func<DocumentLoader> loaderFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loaderFactory);
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _loaderFactory = loaderFactory;
        _files = new ActivePlanFiles(_repoRoot, config);
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

        string planText = File.ReadAllText(_files.Absolute(plan));
        if (ReadyStatus().IsMatch(planText))
            return AlreadyReady(plan, report, taskFile);

        if (!DraftStatus().IsMatch(planText))
        {
            report.Add(new Diagnostic(NotDraft, Severity.Error,
                $"'{plan}' is not a Draft plan: its body has no '**Status: Draft**' line.", plan, plan,
                Hint: "Only an approved Draft plan is finalized. Ask architect to finish the Draft first."));
            return Refused();
        }

        string? line = ApprovalLine(approval, mode, planText, plan, report);
        if (line is null)
            return Refused();

        string? withApproval = WithApproval(planText, mode, line, date, plan, report);
        if (withApproval is null)
            return Refused();

        PlanTaskFileGenerator generator = new(_repoRoot, _config);
        PlanTaskFileOutcome preview = generator.Run(plan, PlanTaskFileMode.Preview);
        if (preview.Report.HasErrors)
        {
            report.AddRange(preview.Report.Items);
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                "The task file cannot be written from this plan. Nothing was changed.", plan, plan, Hint: ArchitectHint));
            return Refused();
        }

        string? planIndex = _files.PlanIndexPath();
        string? indexText = planIndex is not null && File.Exists(_files.Absolute(planIndex))
            ? File.ReadAllText(_files.Absolute(planIndex))
            : null;
        string? newIndex = indexText is null ? null : WithReadyIndexRow(indexText, Path.GetFileName(plan));
        string newPlan = WithReadyStatus(withApproval, date);

        string? taskFileBefore = File.Exists(_files.Absolute(taskFile)) ? File.ReadAllText(_files.Absolute(taskFile)) : null;
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
        List<string> touched = [plan, taskFile];
        if (newIndex is not null)
            touched.Add(planIndex!);
        _files.Stage(touched, report, plan, Step);
        return new PlanFinalizeOutcome(report, true, false, taskFile);
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
        return new PlanFinalizeOutcome(report, false, true, taskFile);
    }

    private static string? ApprovalLine(string? approval, PlanApprovalMode mode, string planText, string plan, DiagnosticReport report)
    {
        string text = approval?.Trim() ?? string.Empty;
        string? problem = text.Length == 0 ? "No approval was given."
            : text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal) ? "The approval must be one line."
            : text.Length > MaxApprovalLength ? $"The approval is longer than {MaxApprovalLength} characters."
            : mode == PlanApprovalMode.Autonomous && !AutonomousPlan().IsMatch(FrontmatterOf(planText))
                ? "An autonomous approval needs a plan whose frontmatter says 'autonomy: autonomous'."
                : null;
        if (problem is null)
            return text.Replace("|", "\\|", StringComparison.Ordinal);

        report.Add(new Diagnostic(NoApproval, Severity.Error, problem + " Nothing was changed.", plan, plan,
            Hint: "Finalize only once approval exists: the AUTONOMOUS_APPROVAL line in autonomous mode, the owner's answer at the approve-and-execute gate otherwise."));
        return null;
    }

    /// <summary>
    /// The plan text with the approval recorded: an open approval row (id starting <c>AP</c>) is answered in place,
    /// otherwise a new <c>AP</c> row is added. Null, with the reason reported, when the plan has no Decisions table or
    /// a decision is still open.
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

            if (isApproval && approvalRow < 0)
                approvalRow = index;
            else
                open.Add(id);
        }

        if (open.Count > 0)
        {
            report.Add(new Diagnostic(OpenDecisions, Severity.Error,
                $"Decisions still open: {string.Join(", ", open)}. Nothing was changed.", plan, plan, Hint: ArchitectHint));
            return null;
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
        else if (File.Exists(_files.Absolute(taskFile)))
            File.Delete(_files.Absolute(taskFile));
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

    [GeneratedRegex(@"^autonomy:[ \t]*autonomous[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AutonomousPlan();

    [GeneratedRegex(@"^##[ \t]+Decisions[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DecisionsHeading();

    [GeneratedRegex(@"^\W*(OPEN|RECOMMENDED)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenStatus();

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex CellSeparator();
}
