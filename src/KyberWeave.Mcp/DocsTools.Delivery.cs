using System.ComponentModel;
using System.Globalization;
using System.Text;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Delivery;
using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// The deliver tool: push a finished run's branch and open or update its draft pull request, with the description
/// assembled from what the run recorded (ADR 0081). A thin wrapper over <see cref="RunDelivery"/>, which
/// <c>kyber-weave run deliver</c> also runs.
/// </summary>
public sealed partial class DocsTools
{
    /// <summary>The deliver tool's published name.</summary>
    public const string DeliverToolName = "docs_push_run_branch_and_open_draft_pull_request";

    private const string DeliverFallbackNext =
        "next: dispatch github-devops with the create-pull-request skill to open the pull request. Nothing was pushed.";

    /// <summary>Runs git and gh for the deliver tool; null for the bounded default. Tests set it so nothing leaves the machine.</summary>
    internal IProcessExecutor? DeliveryExecutor { get; set; }

    /// <summary>The home directory holding the run records; null for the user profile. Tests point it at a scratch folder.</summary>
    internal string? DeliveryHome { get; set; }

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
        if (string.IsNullOrWhiteSpace(projectPath))
            return Provenance() + "\nresult: refused\nprojectPath is required: pass the absolute path of the run's worktree.";
        if (Redirect(projectPath, tools => tools.DeliverHere(summary, notes, title, draft, documentationImpact, planFile)) is { } redirected)
            return redirected;
        return DeliverHere(summary, notes, title, draft, documentationImpact, planFile);
    }

    private string DeliverHere(string summary, string? notes, string? title, bool draft, string? documentationImpact, string? planFile)
    {
        string provenance = Provenance();
        DiagnosticReport report = new();
        if (!TryLoadHostConfiguration(report, out KyberWeaveConfig config))
            return LifecycleResponse(provenance, "refused", report, null);

        DeliveryOutcome outcome = new RunDelivery(_host.RepositoryRoot, config, DeliveryExecutor, DeliveryHome)
            .Run(new DeliveryRequest(summary, notes, title, draft, documentationImpact, planFile));
        report.AddRange(outcome.Report.Items);
        if (!outcome.Delivered)
            return LifecycleResponse(provenance, "refused", report, outcome.NeedsDeliveryAgent ? DeliverFallbackNext : null);

        StringBuilder next = new();
        next.AppendLine(CultureInfo.InvariantCulture, $"pull request: {outcome.Link}");
        next.AppendLine(CultureInfo.InvariantCulture, $"number: {outcome.Number}");
        next.AppendLine(CultureInfo.InvariantCulture, $"draft: {(outcome.IsDraft == true ? "yes" : "no")}");
        next.AppendLine(CultureInfo.InvariantCulture, $"base: {outcome.Base}");
        next.AppendLine(CultureInfo.InvariantCulture, $"head: {outcome.Head}");
        next.AppendLine(CultureInfo.InvariantCulture, $"checks: {outcome.Checks}");
        next.Append("next: report the pull request link and its checks. No re-check is needed: this was read back from GitHub.");
        return LifecycleResponse(provenance, outcome.Created ? "created" : "updated", report, next.ToString());
    }
}
