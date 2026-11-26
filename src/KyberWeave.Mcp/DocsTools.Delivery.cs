using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// The deliver tool: push a finished run's branch and open or update its draft pull request, with the description
/// assembled from what the run recorded (ADR 0081). An adapter over <see cref="KyberWeave.Contracts.Services.IRunDeliveryService"/>,
/// which <c>kyber-weave run deliver</c> also runs through the same engine.
/// </summary>
public sealed partial class DocsTools
{
    /// <summary>The deliver tool's published name.</summary>
    public const string DeliverToolName = "docs_push_run_branch_and_open_draft_pull_request";

    /// <summary>
    /// Delivers a finished run: commits the archived plan move and the filed todos if still pending, pushes the run branch (never
    /// forced), and creates its draft pull request on GitHub, or updates the open one, with a description assembled from
    /// the plan, the task gates and the review. Refuses, changing nothing, off a run branch, with uncommitted changes it
    /// does not own, with a plan not yet archived, or when GitHub's CLI is missing or signed out.
    /// </summary>
    [McpServerTool(Name = DeliverToolName, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("""
        Use when a run is finished and ready to leave the machine — its plan archived and the
        merge-ready checks passed: it commits the archived plan and filed todos if still pending, pushes
        the run branch (never forced, never another branch), and opens a draft pull request
        into the run's base on GitHub, or updates the open one, with a title and description
        assembled from the plan, the per-task checks and the review. Call it once; it reads the
        pull request back and returns its link, number, draft state, base and head, and the CI
        checks reported. It never marks the pull request ready, merges or requests reviewers.
        It refuses with findings, changing nothing, off a run branch, with uncommitted changes
        it does not own, with the plan not archived, or when the GitHub CLI is missing or
        signed out; on a provider refusal, dispatch the delivery agent instead.
        """)]
    public string DeliverRun(
        [Description("Absolute path of the run's worktree, on the run branch. Required.")] string projectPath,
        [Description("One to three plain sentences for a human reviewer: the problem and the outcome.")] string summary,
        [Description("Extra notes for reviewers, such as follow-ups. Optional.")] string? notes = null,
        [Description("A title to use instead of the composed '<type>(<scope>): <summary> [<harness>]'. Optional.")] string? title = null,
        [Description("Open a new pull request as a draft. Default true; an existing pull request keeps its state.")] bool draft = true,
        [Description("The documentation-impact outcome as docs-dev returned it (its DOCS_REVIEW line and recommendations). Optional.")] string? documentationImpact = null,
        [Description("The archived plan's file name, only when the branch archived more than one. Optional.")] string? planFile = null)
    {
        if (Redirect(projectPath, tools => tools.Bound.Delivery.DeliverRun(summary, notes, title, draft, documentationImpact, planFile)) is { } redirected)
            return redirected;
        if (string.IsNullOrWhiteSpace(projectPath))
            return ProvenanceLine() + "\nresult: refused\nprojectPath is required: pass the absolute path of the run's worktree.";
        return Bound.Delivery.DeliverRun(summary, notes, title, draft, documentationImpact, planFile);
    }
}
