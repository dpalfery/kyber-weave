using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// The two plan lifecycle tools: finalize an approved Draft plan, and archive a plan whose closeout evidence is
/// complete. They are the docs server's only tools that change files, and each changes only a plan, its task file,
/// the plan index and the archive (ADR 0078). Adapters over <see cref="KyberWeave.Contracts.Services.IPlanLifecycleService"/>.
/// </summary>
public sealed partial class DocsTools
{
    /// <summary>The finalize tool's published name.</summary>
    public const string FinalizeToolName = "docs_finalize_approved_plan_and_write_task_file";

    /// <summary>The archive tool's published name.</summary>
    public const string ArchiveToolName = "docs_archive_plan_after_closeout_is_complete";

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
    public async Task<string> FinalizePlanAsync(
        [Description("Absolute path of the run's worktree that holds the plan. Required.")] string projectPath,
        [Description("The plan file, as a path or its file name under the plans folder.")] string planFile,
        [Description("'autonomous' when the conductor accepted the recommendation, 'supervised' when the owner approved.")] string approvalMode,
        [Description("One line saying what approved it, such as 'AUTONOMOUS_APPROVAL: approve-and-execute' or the owner's answer.")] string approval,
        CancellationToken cancellationToken = default)
    {
        if (await RedirectAsync(
                projectPath,
                tools => tools.Bound.PlanLifecycle.FinalizePlanAsync(planFile, approvalMode, approval, cancellationToken),
                cancellationToken).ConfigureAwait(false) is { } redirected)
        {
            return redirected;
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return await ProvenanceLineAsync(cancellationToken).ConfigureAwait(false)
                + "\nresult: refused\nprojectPath is required: pass the absolute path of the run's worktree.";
        }

        return await Bound.PlanLifecycle.FinalizePlanAsync(planFile, approvalMode, approval, cancellationToken).ConfigureAwait(false);
    }

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
    public async Task<string> ArchivePlanAsync(
        [Description("Absolute path of the run's worktree that holds the plan. Required.")] string projectPath,
        [Description("The plan file, as a path or its file name under the plans folder.")] string planFile,
        CancellationToken cancellationToken = default)
    {
        if (await RedirectAsync(
                projectPath,
                tools => tools.Bound.PlanLifecycle.ArchivePlanAsync(planFile, cancellationToken),
                cancellationToken).ConfigureAwait(false) is { } redirected)
        {
            return redirected;
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return await ProvenanceLineAsync(cancellationToken).ConfigureAwait(false)
                + "\nresult: refused\nprojectPath is required: pass the absolute path of the run's worktree.";
        }

        return await Bound.PlanLifecycle.ArchivePlanAsync(planFile, cancellationToken).ConfigureAwait(false);
    }
}
