using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Closeout;

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
    private readonly Func<IDocumentSource> _loaderFactory;
    private readonly ActivePlanFiles _files;
    private readonly IRepositoryFileStore _store;
    private readonly IProcessRunner _process;
    private readonly IDocsSchemaGate _gate;
    private readonly IPlanTaskAgentPicker? _picker;

    /// <summary>
    /// Test seam, called with the plan's absolute path once the plan has passed the active-plan check and before it is
    /// read: the moment a plan can vanish between the check and the read.
    /// </summary>
    internal Action<string>? AfterActivePlanCheck { get; init; }

    /// <summary>Creates the finalize step over a repository, its effective configuration and the loader validation reads with.</summary>
    public PlanFinalize(string repoRoot, KyberWeaveConfig config, Func<IDocumentSource> loaderFactory, IRepositoryFileStore files, IProcessRunner process, IDocsSchemaGate gate, IPathResolver paths, IProcessEnvironment environment)
        : this(repoRoot, config, loaderFactory, files, process, gate, paths, environment, null)
    {
    }

    /// <summary>Creates the finalize step with a picker that chooses each unassigned task's agent as the task file is written.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="loaderFactory">The loader validation reads with.</param>
    /// <param name="picker">The agent picker, or null when no decision model is configured.</param>
    /// <param name="files">The file store the plan, its index and its task file are read and written through.</param>
    /// <param name="process">Runs the git commands that stage and commit the plan.</param>
    /// <param name="gate">The schema gate the finalized plan is validated with.</param>
    /// <param name="paths">Resolves the repository root to a full path.</param>
    /// <param name="environment">Names the directory a relative repository root is resolved against.</param>
    public PlanFinalize(
        string repoRoot,
        KyberWeaveConfig config,
        Func<IDocumentSource> loaderFactory,
        IRepositoryFileStore files,
        IProcessRunner process,
        IDocsSchemaGate gate,
        IPathResolver paths,
        IProcessEnvironment environment,
        IPlanTaskAgentPicker? picker)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        _gate = gate;
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loaderFactory);
        _repoRoot = paths.GetFullPath(repoRoot, environment.CurrentDirectory);
        _config = config;
        _loaderFactory = loaderFactory;
        _store = files;
        _process = process;
        _files = new ActivePlanFiles(_repoRoot, config, files, process);
        _picker = picker;
    }

    /// <summary>Finalizes the approved Draft plan at <paramref name="planPath"/> without blocking a thread, or refuses and changes nothing.</summary>
    /// <param name="planPath">The plan, relative to the repository root.</param>
    /// <param name="mode">Who approved it.</param>
    /// <param name="approval">One line saying what approved it, recorded in the Decisions table.</param>
    /// <param name="date">The approval date.</param>
    /// <param name="cancellationToken">Cancels the model picks and git commands.</param>
    public async Task<PlanFinalizeOutcome> RunAsync(
        string planPath,
        PlanApprovalMode mode,
        string? approval,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planPath);
        cancellationToken.ThrowIfCancellationRequested();

        DiagnosticReport report = new();
        DraftPreparation prep = PrepareDraft(planPath, report);
        if (prep.Outcome is { } early)
            return early;
        if (prep.AlreadyReady)
            return await AlreadyReadyAsync(prep.Plan, report, prep.TaskFile, cancellationToken).ConfigureAwait(false);

        PlanFinalizeOutcome Refused() => new(report, false, false, prep.TaskFile);

        if (await HasOtherStagedAsync(prep.Touched, prep.Plan, report, cancellationToken).ConfigureAwait(false))
            return Refused();

        if (PrepareApproval(mode, approval, date, prep.PlanText, prep.Plan, report) is not { } withApproval)
            return Refused();

        PlanTaskFileGenerator generator = new(_repoRoot, _config, _store, _picker);
        PlanTaskFileOutcome preview = await generator.RunAsync(prep.Plan, PlanTaskFileMode.Preview, cancellationToken)
            .ConfigureAwait(false);
        if (!TryPrepareDraftWrite(preview, prep, withApproval, date, report, out DraftWritePlan write))
            return Refused();

        try
        {
            WriteDraftTargets(prep.Plan, write.NewPlan, prep.PlanIndex, write.NewIndex);
            PlanTaskFileOutcome written = await generator.RunAsync(prep.Plan, PlanTaskFileMode.Write, cancellationToken)
                .ConfigureAwait(false);
            if (!CompleteDraftValidation(prep, write.TaskFileBefore, written, report))
                return Refused();
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            HandleFinalizeFailure(exception, prep, write.TaskFileBefore, report);
            return Refused();
        }

        RecordDraftSuccess(report, prep, mode, write.NewIndex);
        await _files.StageAsync(prep.Touched, report, prep.Plan, Step, cancellationToken).ConfigureAwait(false);
        await _files.CommitOnlyAsync(prep.Touched, CommitMessage(prep.Plan), report, prep.Plan, Step, cancellationToken)
            .ConfigureAwait(false);
        return new PlanFinalizeOutcome(report, true, false, prep.TaskFile, ReadyQueue(prep.TaskFile, prep.Plan, report));
    }

    private readonly record struct DraftPreparation(
        string Plan,
        string PlanText,
        string? PlanIndex,
        string? IndexText,
        string TaskFile,
        List<string> Touched,
        bool AlreadyReady,
        PlanFinalizeOutcome? Outcome);

    private readonly record struct DraftWritePlan(string NewPlan, string? NewIndex, string? TaskFileBefore);

    private DraftPreparation PrepareDraft(string planPath, DiagnosticReport report)
    {
        string plan = planPath.Replace('\\', '/');
        string taskFile = PlanTaskFileGenerator.TaskFilePathOf(plan);
        PlanFinalizeOutcome Refused() => new(report, false, false, taskFile);

        DraftPreparation Fail() => new(plan, string.Empty, null, null, taskFile, [], false, Refused());

        if (_files.CheckActivePlan(plan) is { } notActive)
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, notActive, plan, plan,
                Hint: "Name a plan file directly under the plans folder, not its index and not an archived plan."));
            return Fail();
        }

        AfterActivePlanCheck?.Invoke(_files.Absolute(plan));
        string? planRead;
        try
        {
            planRead = _store.ReadTextIfPresent(_files.Absolute(plan));
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, $"'{plan}' could not be read: {exception.Message}", plan, plan,
                Hint: "Make the plan a readable file, then run the finalize again."));
            return Fail();
        }

        if (planRead is not { } planText)
        {
            report.Add(new Diagnostic(NotAnActivePlan, Severity.Error, $"'{plan}' does not exist.", plan, plan,
                Hint: "Name a plan file directly under the plans folder, not its index and not an archived plan."));
            return Fail();
        }

        string? planStatus = PlanStatus.ParseBodyStatus(planText);
        if (PlanStatus.IsReady(planStatus))
            return new DraftPreparation(plan, planText, null, null, taskFile, [], true, null);

        if (!PlanStatus.IsDraft(planStatus))
        {
            report.Add(new Diagnostic(NotDraft, Severity.Error,
                $"'{plan}' is not a Draft plan: its body has no '**Status: Draft**' line.", plan, plan,
                Hint: "Only an approved Draft plan is finalized. Ask architect to finish the Draft first."));
            return Fail();
        }

        string? planIndex = _files.PlanIndexPath();
        string? indexText;
        try
        {
            indexText = planIndex is null ? null : _store.ReadTextIfPresent(_files.Absolute(planIndex));
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                $"The plan index could not be read, so nothing was changed: {exception.Message}", plan, plan, Hint: ArchitectHint));
            return Fail();
        }

        List<string> touched = [plan, taskFile];
        if (indexText is not null)
            touched.Add(RequiredPlanIndex(planIndex));

        return new DraftPreparation(plan, planText, planIndex, indexText, taskFile, touched, false, null);
    }

    private static string? PrepareApproval(
        PlanApprovalMode mode,
        string? approval,
        DateOnly date,
        string planText,
        string plan,
        DiagnosticReport report)
    {
        string? line = ApprovalLine(approval, mode, planText, plan, report);
        return line is null ? null : WithApproval(planText, mode, line, date, plan, report);
    }

    private bool TryPrepareDraftWrite(
        PlanTaskFileOutcome preview,
        DraftPreparation prep,
        string withApproval,
        DateOnly date,
        DiagnosticReport report,
        out DraftWritePlan write)
    {
        write = default;
        if (preview.Report.HasErrors)
        {
            report.AddRange(preview.Report.Items);
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                "The task file cannot be written from this plan. Nothing was changed.", prep.Plan, prep.Plan, Hint: ArchitectHint));
            return false;
        }

        string? newIndex = prep.IndexText is null ? null : WithReadyIndexRow(prep.IndexText, Path.GetFileName(prep.Plan));
        string newPlan = WithReadyStatus(withApproval, date);
        if (!PlanStatus.IsReady(PlanStatus.ParseBodyStatus(newPlan)))
        {
            report.Add(new Diagnostic(NotDraft, Severity.Error,
                $"The Draft status line of '{prep.Plan}' is not in a form this step can rewrite. Nothing was changed.", prep.Plan, prep.Plan,
                Hint: "Write the body status line as '**Status: Draft.**' or '**Status:** Draft'."));
            return false;
        }

        string? taskFileBefore;
        try
        {
            // One read answers both "is it there" and "what does it hold": null means absent at this read, and only then
            // may a restore delete it.
            taskFileBefore = _store.ReadTextIfPresent(_files.Absolute(prep.TaskFile));
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            // Nothing has been written yet, so there is nothing to restore; restoring from an unread copy would delete the file.
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                $"The existing task file could not be read, so nothing was changed: {exception.Message}", prep.Plan, prep.Plan, Hint: ArchitectHint));
            return false;
        }

        foreach (string written in new[] { prep.Plan, prep.PlanIndex }.OfType<string>().Where(file => IsLink(_store, _files.Absolute(file))))
        {
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                $"'{written}' is a symbolic link, which this step does not write through. Nothing was changed.", prep.Plan, written, Hint: ArchitectHint));
            return false;
        }

        write = new DraftWritePlan(newPlan, newIndex, taskFileBefore);
        return true;
    }

    private void WriteDraftTargets(string plan, string newPlan, string? planIndex, string? newIndex)
    {
        _store.WriteAllText(_files.Absolute(plan), newPlan);
        if (newIndex is not null)
            _store.WriteAllText(_files.Absolute(RequiredPlanIndex(planIndex)), newIndex);
    }

    private bool CompleteDraftValidation(
        DraftPreparation prep,
        string? taskFileBefore,
        PlanTaskFileOutcome written,
        DiagnosticReport report)
    {
        report.AddRange(written.Report.Items.Where(item => item.Severity == Severity.Error));
        if (!report.HasErrors)
            report.AddRange(Validate(prep.Plan, prep.TaskFile, prep.PlanIndex));

        if (!report.HasErrors)
            return true;

        report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
            "The finalized plan failed validation, so every change was undone.", prep.Plan, prep.Plan, Hint: ArchitectHint));
        IReadOnlyList<string> notUndone = Restore(prep.Plan, prep.PlanText, prep.PlanIndex, prep.IndexText, prep.TaskFile, taskFileBefore);
        ReportNotUndone(report, prep.Plan, notUndone, taskFileBefore is null ? prep.TaskFile : null);
        return false;
    }

    private void HandleFinalizeFailure(
        Exception exception,
        DraftPreparation prep,
        string? taskFileBefore,
        DiagnosticReport report)
    {
        IReadOnlyList<string> notUndone = Restore(prep.Plan, prep.PlanText, prep.PlanIndex, prep.IndexText, prep.TaskFile, taskFileBefore);
        report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
            notUndone.Count == 0
                ? $"Finalizing failed and every change was undone: {exception.Message}"
                : $"Finalizing failed: {exception.Message}",
            prep.Plan, prep.Plan));
        ReportNotUndone(report, prep.Plan, notUndone, taskFileBefore is null ? prep.TaskFile : null);
    }

    private static void RecordDraftSuccess(DiagnosticReport report, DraftPreparation prep, PlanApprovalMode mode, string? newIndex)
    {
        Say(report, prep.Plan, $"Recorded the {(mode == PlanApprovalMode.Autonomous ? "autonomous" : "owner")} approval in the Decisions table and set {prep.Plan} to Ready.");
        Say(report, prep.Plan, newIndex is not null && !string.Equals(newIndex, prep.IndexText, StringComparison.Ordinal)
            ? $"Set its row in {prep.PlanIndex} to Ready."
            : $"{prep.PlanIndex} has no Draft row for it to change.");
        Say(report, prep.Plan, $"Wrote {prep.TaskFile}.");
    }

    private static string RequiredPlanIndex(string? planIndex) =>
        planIndex ?? throw new InvalidOperationException("The configuration registers no plan index.");

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

    private async Task<PlanFinalizeOutcome> AlreadyReadyAsync(
        string plan,
        DiagnosticReport report,
        string taskFile,
        CancellationToken cancellationToken)
    {
        PlanTaskFileOutcome check = await new PlanTaskFileGenerator(_repoRoot, _config, _store)
            .RunAsync(plan, PlanTaskFileMode.Check, cancellationToken)
            .ConfigureAwait(false);
        if (!TryPrepareAlreadyReady(plan, report, taskFile, check, out List<string> own))
            return new PlanFinalizeOutcome(report, false, false, taskFile);

        if (!await HasOtherStagedAsync(own, plan, report, cancellationToken).ConfigureAwait(false))
            await _files.CommitOnlyAsync(own, CommitMessage(plan), report, plan, Step, cancellationToken).ConfigureAwait(false);
        return new PlanFinalizeOutcome(report, false, true, taskFile, ReadyQueue(taskFile, plan, report));
    }

    private bool TryPrepareAlreadyReady(
        string plan,
        DiagnosticReport report,
        string taskFile,
        PlanTaskFileOutcome check,
        out List<string> own)
    {
        own = [plan, taskFile];
        if (check.Report.HasErrors)
        {
            report.AddRange(check.Report.Items);
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                $"'{plan}' is already Ready but its task file is not current with it. Nothing was changed.", plan, plan,
                Hint: ArchitectHint));
            return false;
        }

        Say(report, plan, $"{plan} is already Ready and {taskFile} is current; nothing changed.");
        string? index = _files.PlanIndexPath();
        try
        {
            if (index is not null && _store.ReadTextIfPresent(_files.Absolute(index)) is not null)
                own.Add(index);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
                $"The plan index could not be read, so nothing was committed: {exception.Message}", plan, plan, Hint: ArchitectHint));
            return false;
        }

        return true;
    }

    private async Task<bool> HasOtherStagedAsync(
        IReadOnlyList<string> own,
        string plan,
        DiagnosticReport report,
        CancellationToken cancellationToken) =>
        ReportOtherStaged(await _files.StagedOutsideAsync(own, cancellationToken).ConfigureAwait(false), plan, report);

    private static bool ReportOtherStaged(IReadOnlyList<string>? stagedOutside, string plan, DiagnosticReport report)
    {
        if (stagedOutside is not { Count: > 0 } others)
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
    internal string? ReadyQueue(string taskFile, string plan, DiagnosticReport report)
    {
        string? queue = null;
        string problem = "it is not there";
        try
        {
            if (_store.ReadTextIfPresent(_files.Absolute(taskFile)) is { } text)
            {
                queue = ReadyQueueOf(text);
                problem = "it is not a valid task file";
            }
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            problem = $"it could not be read ({exception.GetType().Name}: {exception.Message})";
        }

        if (queue is null)
        {
            report.Add(new Diagnostic(Step, Severity.Warning,
                $"The ready queue is not listed because {problem}: {taskFile}.", plan, taskFile,
                Hint: "Read the task file itself for the tasks that can start."));
        }

        return queue;
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
            PlanTaskFile? file;
            try
            {
                file = PlanTaskFileFormat.DeserializeJson(taskFileText);
            }
            catch (JsonException)
            {
                file = PlanTaskFileFormat.Deserialize(PlanTaskFileRules.SpecTaskFileName, taskFileText);
            }
            if (file is not { Tasks: { } tasks }
                || (file.SchemaVersion != PlanTaskFileRules.SchemaVersion
                    && file.SchemaVersion != PlanTaskFileRules.LegacySchemaVersion)
                || tasks.Any(task => task is null))
                return null;
            return tasks.Count > 0 ? PlanTaskPacket.ReadyQueue(tasks) : "(no tasks)";
        }
        catch (Exception exception) when (exception is JsonException or YamlDotNet.Core.YamlException)
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
            ? DraftValueLine().Replace(body, $"**Status:** {PlanStatus.Ready}, {stamp}", 1)
            : DraftBold().Replace(body, $"**Status: {PlanStatus.Ready}, {stamp}.**", 1);
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
            if (column < 0 || column >= cells.Length || !cells[column].StartsWith(PlanStatus.Draft, StringComparison.OrdinalIgnoreCase))
                continue;

            cells[column] = PlanStatus.Ready;
            lines[index] = "| " + string.Join(" | ", cells) + " |";
        }

        return string.Join(newline, lines);
    }

    private IEnumerable<Diagnostic> Validate(string plan, string taskFile, string? planIndex)
    {
        HashSet<string> own = new(StringComparer.OrdinalIgnoreCase) { plan, taskFile };
        if (planIndex is not null)
            own.Add(planIndex);

        DiagnosticReport validated = _gate.Validate(_repoRoot, _config, _loaderFactory().Load(), _loaderFactory, mergeReady: false);
        return validated.Items.Where(item => item.Severity == Severity.Error
                                             && (own.Contains(item.FilePath?.Replace('\\', '/') ?? string.Empty)
                                                 || own.Contains(item.Subject.Replace('\\', '/'))));
    }

    /// <summary>
    /// Puts back what finalizing changed, each file on its own so one that cannot be restored does not stop the
    /// others or crash the refusal that is being reported.
    /// </summary>
    /// <returns>The files that could not be restored, for the report to name.</returns>
    internal List<string> Restore(string plan, string planText, string? planIndex, string? indexText, string taskFile, string? taskFileBefore)
    {
        List<string> notUndone = [];
        if (!TryWrite(_store, _files.Absolute(plan), planText))
            notUndone.Add(plan);
        if (planIndex is not null && indexText is not null && !TryWrite(_store, _files.Absolute(planIndex), indexText))
            notUndone.Add(planIndex);
        // Absent before the step means "this step created it", except a link: a dangling one also reads as absent, but it
        // is the owner's, this step never creates one, and deleting it would remove the link rather than a written file.
        bool taskFileRestored = taskFileBefore is not null
            ? TryWrite(_store, _files.Absolute(taskFile), taskFileBefore)
            : IsLink(_store, _files.Absolute(taskFile)) || DeleteIfPresent(_store, _files.Absolute(taskFile));
        if (!taskFileRestored)
            notUndone.Add(taskFile);
        return notUndone;
    }

    /// <summary>
    /// Names the files a refusal could not put back, with the instruction that works for each: a file git tracks is
    /// restored with <c>git checkout --</c>; a task file this step created has no committed version, so git has nothing
    /// to restore and it is deleted by hand.
    /// </summary>
    /// <param name="report">The refusal being reported.</param>
    /// <param name="plan">The plan, which the diagnostic is filed against.</param>
    /// <param name="notUndone">The files that could not be restored.</param>
    /// <param name="createdTaskFile">The task file when this step created it (it was absent before); otherwise null.</param>
    internal static void ReportNotUndone(DiagnosticReport report, string plan, IReadOnlyList<string> notUndone, string? createdTaskFile)
    {
        if (notUndone.Count == 0)
            return;

        List<string> tracked = [.. notUndone.Where(file => !string.Equals(file, createdTaskFile, StringComparison.Ordinal))];
        bool createdLeft = createdTaskFile is not null && notUndone.Contains(createdTaskFile, StringComparer.Ordinal);
        List<string> advice = [];
        if (tracked.Count > 0)
            advice.Add($"Restore {string.Join(", ", tracked)} with 'git checkout -- <file>', or fix it by hand.");
        if (createdLeft)
            advice.Add($"'{createdTaskFile}' did not exist before this step and git has nothing to restore it from: delete it by hand.");

        report.Add(new Diagnostic(PlanNeedsArchitect, Severity.Error,
            $"Could not undo the change to: {string.Join(", ", notUndone)}.", plan, plan,
            Hint: string.Join(' ', advice)));
    }

    internal static bool IsLink(IWritableFileSystem files, string path)
    {
        try
        {
            return files.GetLinkTarget(path) is not null;
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return false;
        }
    }

    // A link is the owner's: writing through it would rewrite whatever it points at, so a restore treats it as one more
    // file it cannot undo, exactly as the generator refuses to write through one.
    private static bool TryWrite(IWritableFileSystem files, string path, string text)
    {
        try
        {
            if (IsLink(files, path))
                return false;

            files.WriteAllText(path, text);
            return true;
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/> if it is there. A file or folder that is already gone counts as deleted; a file
    /// that cannot be deleted (in use, no permission, a folder in its place) is reported, never thrown, because this
    /// runs while a refusal is being reported.
    /// </summary>
    /// <returns>True when the file is gone afterwards; false when it could not be deleted.</returns>
    internal static bool DeleteIfPresent(IWritableFileSystem files, string path)
    {
        try
        {
            files.DeleteFile(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            // The folder is gone, so the file is too.
            return true;
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return false;
        }
    }

    private static void Say(DiagnosticReport report, string plan, string message) =>
        report.Add(new Diagnostic(Step, Severity.Info, message, plan, plan));

    [GeneratedRegex(@"^\*\*Status:\*\*[ \t]*" + PlanStatus.Draft + @"[^\r\n]*", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DraftValueLine();

    [GeneratedRegex(@"^\*\*Status:[ \t]*" + PlanStatus.Draft + @"[^*\r\n]*\*\*", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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
