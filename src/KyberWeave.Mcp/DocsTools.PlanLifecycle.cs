using System.ComponentModel;
using System.Globalization;
using System.Text;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Parsing;
using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// The two plan lifecycle tools: finalize an approved Draft plan, and archive a plan whose closeout evidence is
/// complete. They are the docs server's only tools that change files, and each changes only a plan, its task file,
/// the plan index and the archive (ADR 0078).
/// </summary>
public sealed partial class DocsTools
{
    /// <summary>The finalize tool's published name.</summary>
    public const string FinalizeToolName = "docs_finalize_approved_plan_and_write_task_file";

    /// <summary>The archive tool's published name.</summary>
    public const string ArchiveToolName = "docs_archive_plan_after_closeout_is_complete";

    private const string ArchiveRefusalNext =
        "next: send these findings to docs-dev as one closeout dispatch (harvest and Closeout mapping). " +
        "No other role fixes them, and no file is moved by hand. Call this tool again when docs-dev returns.";

    /// <summary>
    /// Finalizes an approved Draft plan: records the approval in its Decisions table, sets the plan and its index row
    /// to Ready, writes its task file and commits those files on the current branch (never pushing). Refuses, changing
    /// nothing, without an approval, on a plan that is not Draft, with open decisions, with other files staged, or when
    /// the task file cannot be written or the result fails validation.
    /// </summary>
    [McpServerTool(Name = FinalizeToolName, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Use when a Draft plan has been approved for execution and needs finalizing: it records
        the approval in the plan's Decisions table (in autonomous mode it also accepts decisions
        still marked RECOMMENDED), sets the plan and its plan-index row to Ready, writes the
        plan's task file, and commits those three files. Call it once approval exists — right
        after the Draft returns in autonomous mode, only after the owner approves in supervised
        mode. Changes only those files, never pushes, and refuses with findings, changing
        nothing, when the plan is not Draft, a decision is open, other files are staged, or the
        task file cannot be written. On success it lists the ready queue to dispatch from; a
        plan already Ready with a current task file reports done.
        """)]
    public string FinalizePlan(
        [Description("Absolute path of the run's worktree that holds the plan. Required.")] string projectPath,
        [Description("The plan file, as a path or its file name under the plans folder.")] string planFile,
        [Description("'autonomous' when the conductor accepted the recommendation, 'supervised' when the owner approved.")] string approvalMode,
        [Description("One line saying what approved it, such as 'AUTONOMOUS_APPROVAL: approve-and-execute' or the owner's answer.")] string approval)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return Provenance() + "\nresult: refused\nprojectPath is required: pass the absolute path of the run's worktree.";
        if (Redirect(projectPath, tools => tools.FinalizeHere(planFile, approvalMode, approval)) is { } redirected)
            return redirected;
        return FinalizeHere(planFile, approvalMode, approval);
    }

    private string FinalizeHere(string planFile, string approvalMode, string approval)
    {
        string provenance = Provenance();
        DiagnosticReport report = new();
        if (!TryResolveLifecyclePlan(planFile, report, out KyberWeaveConfig config, out string plan))
            return LifecycleResponse(provenance, "refused", report, null);

        if (!PlanFinalize.TryParseMode(approvalMode, out PlanApprovalMode mode))
        {
            return LifecycleResponse(provenance, "refused", report,
                $"approvalMode '{approvalMode}' is not 'autonomous' or 'supervised'. Nothing was changed.");
        }

        PlanFinalizeOutcome outcome = new PlanFinalize(_host.RepositoryRoot, config, () => new DocumentLoader(_host.RepositoryRoot, config.Ontology))
            .Run(plan, mode, approval, DateOnly.FromDateTime(DateTime.UtcNow));
        report.AddRange(outcome.Report.Items);
        if (outcome.Finalized)
            return LifecycleResponse(provenance, "finalized", report, ReadyNext(outcome));
        if (outcome.AlreadyReady)
            return LifecycleResponse(provenance, "already-ready", report, ReadyNext(outcome));
        return LifecycleResponse(provenance, "refused", report, null);
    }

    private static string ReadyNext(PlanFinalizeOutcome outcome) =>
        $"next: TASK_FILE: {outcome.TaskFilePath}\nready queue (dispatch each worker by task id; it fetches its own packet):\n" +
        $"{outcome.ReadyQueue ?? "(unreadable: validate the task file)"}\ndispatch the first ready tasks now.";

    /// <summary>
    /// Archives a plan whose closeout evidence is complete, through the same step as
    /// <c>kyber-weave docs closeout &lt;plan&gt; --archive</c>. Refuses, changing nothing, with closeout's own findings
    /// when the evidence is incomplete.
    /// </summary>
    [McpServerTool(Name = ArchiveToolName, ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("""
        Use when review has approved a plan's change and docs-dev has finished its harvest and
        Closeout mapping: it archives the plan the same way as kyber-weave docs closeout --archive
        — sets it Archived, moves it to the archive, removes its task file, and updates the plan
        index and the archived plan inventory. When the closeout evidence is incomplete it refuses
        with the same findings as docs closeout, changing nothing; those findings go to docs-dev.
        After it archives, run docs_validate_governed_document_schema with mergeReady true.
        """)]
    public string ArchivePlan(
        [Description("Absolute path of the run's worktree that holds the plan. Required.")] string projectPath,
        [Description("The plan file, as a path or its file name under the plans folder.")] string planFile)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return Provenance() + "\nresult: refused\nprojectPath is required: pass the absolute path of the run's worktree.";
        if (Redirect(projectPath, tools => tools.ArchiveHere(planFile)) is { } redirected)
            return redirected;
        return ArchiveHere(planFile);
    }

    private string ArchiveHere(string planFile)
    {
        string provenance = Provenance();
        DiagnosticReport report = new();
        if (!TryResolveLifecyclePlan(planFile, report, out KyberWeaveConfig config, out string plan))
            return LifecycleResponse(provenance, "refused", report, null);

        PlanCloseoutOutcome outcome = new PlanCloseout(_host.RepositoryRoot, config, () => new DocumentLoader(_host.RepositoryRoot, config.Ontology))
            .Run(plan, DateOnly.FromDateTime(DateTime.UtcNow), archive: true);
        report.AddRange(outcome.Report.Items);
        return outcome.Archived
            ? LifecycleResponse(provenance, "archived", report,
                "next: call docs_validate_governed_document_schema with mergeReady true and docs_detect_drift_between_documents_and_code, with the same projectPath.")
            : LifecycleResponse(provenance, "refused", report, ArchiveRefusalNext);
    }

    private bool TryResolveLifecyclePlan(string planFile, DiagnosticReport report, out KyberWeaveConfig config, out string plan)
    {
        plan = string.Empty;
        if (!TryLoadHostConfiguration(report, out config))
            return false;

        if (string.IsNullOrWhiteSpace(planFile))
        {
            report.Add(new Diagnostic(PlanFinalize.NotAnActivePlan, Severity.Error, "planFile is required.", "plan"));
            return false;
        }

        PlanPathResolution resolution = new PlanPathResolver(_host.RepositoryRoot, config).Resolve(planFile, _host.RepositoryRoot);
        if (!resolution.Found)
        {
            string message = resolution.Ambiguous
                ? $"'{planFile}' matches several plans: {string.Join(", ", resolution.Candidates)}. Name one."
                : $"'{planFile}' names no plan under {string.Join(", ", resolution.SearchedFolders)}.";
            report.Add(new Diagnostic(PlanFinalize.NotAnActivePlan, Severity.Error, message, planFile));
            return false;
        }

        plan = resolution.Path!;
        return true;
    }

    private static string LifecycleResponse(string provenance, string result, DiagnosticReport report, string? next)
    {
        StringBuilder sb = new();
        sb.AppendLine(provenance);
        sb.AppendLine(CultureInfo.InvariantCulture, $"result: {result}");
        foreach (Diagnostic item in report.Items.OrderByDescending(item => item.Severity))
            sb.AppendLine(FormatGateFinding(item));
        if (next is not null)
            sb.AppendLine(next);
        return sb.ToString().TrimEnd();
    }
}
