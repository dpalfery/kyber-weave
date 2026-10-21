using System.ComponentModel;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
/// Runs the deterministic gates the host declares under <c>review.gates</c>, or, with <c>--agent</c>,
/// one developer agent's gates under <c>review.task-gates</c> against the change that agent made.
/// </summary>
public sealed class ReviewGatesCommand : Command<ReviewGatesSettings>
{
    private const string NoGatesDeclared = "KW-REVIEW-020";
    private const string ReportWriteFailed = "KW-REVIEW-025";
    private const string GateSkipped = "KW-REVIEW-026";
    private const string NoGatesForAgent = "KW-REVIEW-027";
    private const string InvalidInput = "KW-REVIEW-028";
    private const string FixFailed = "KW-REVIEW-029";
    private const int ClientInputExitCode = 2;

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewGatesSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Agent is null)
        {
            if (settings.HasAgentOnlyOptions)
            {
                WriteClientInputError(
                    "--paths, --base, --task and --fix apply only with --agent. Name the agent whose task " +
                    "gates to run, or drop those options.");
                return ClientInputExitCode;
            }

            return ExecuteRepositoryGates(settings);
        }

        return ExecuteAgentGates(settings, settings.Agent);
    }

    private static int ExecuteRepositoryGates(ReviewGatesSettings settings)
    {
        DiagnosticReport report = new();
        string root = Path.GetFullPath(settings.Path);

        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(report, settings, "review gates", "Gate");
            return 1;
        }

        if (config.Review.Gates.Count == 0)
        {
            // Not an error, and deliberately not silent. A review that runs no gates is a
            // review with no evidence, and the reviewer needs to know that is the situation
            // rather than reading an empty gate report as "everything passed".
            report.Add(new Diagnostic(
                NoGatesDeclared,
                Severity.Warning,
                "No gates are declared, so this review has no executed evidence behind it.",
                "review.gates",
                Hint: "Declare the repository's build, test, and analysis commands under " +
                      "review.gates in kyber-weave.yml."));
            CommandHelpers.Finish(report, settings, "review gates", "Gate");
            return 0;
        }

        GateReport gates = GateRunner.Run(config.Review, root, settings.StopOnFailure);

        foreach (GateResult gate in gates.Gates)
        {
            report.Add(new Diagnostic(
                gate.Passed ? ReviewGateOutcome.Passed : ReviewGateOutcome.Failed,
                gate.Passed ? Severity.Info : gate.Blocking ? Severity.Error : Severity.Warning,
                $"{gate.Id}: {gate.Summary} ({gate.DurationMilliseconds} ms)",
                gate.Id));
        }

        report.AddMetric("gates", gates.Gates.Count);
        report.AddMetric("failed", gates.Gates.Count(g => !g.Passed));

        CommandHelpers.TryWriteReport(settings.Out, ReviewJson.Write(gates), ReportWriteFailed, report);

        CommandHelpers.Finish(report, settings, "review gates", "Gate");
        // Blocking failures are already Error diagnostics; a failed --out write is too.
        // Non-blocking gate failures stay Warning and do not fail the process.
        return report.HasErrors ? 1 : 0;
    }

    private static int ExecuteAgentGates(ReviewGatesSettings settings, string agent)
    {
        DiagnosticReport report = new();
        string root = Path.GetFullPath(settings.Path);

        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(report, settings, "review gates", "Gate");
            return 1;
        }

        // Every input is checked before the agent is looked up, so an unsafe path or slug is refused
        // the same way whether or not the agent declares gates.
        List<string> refusals = [];
        string baseRef = settings.BaseRef ?? ChangedFiles.DefaultBase;
        if (settings.BaseRef is not null && ChangedFiles.ValidateBase(settings.BaseRef) is { } baseProblem)
            refusals.Add(baseProblem);

        if (TaskGateSelection.ValidateTask(settings.TaskSlug) is { } taskProblem)
            refusals.Add(taskProblem);

        foreach (string path in settings.Paths)
        {
            if (TaskGateSelection.ValidatePath(root, path) is { } pathProblem)
                refusals.Add(pathProblem);
        }

        if (refusals.Count > 0)
            return RefuseInput(report, settings, refusals);

        if (!config.Review.TaskGates.TryGetValue(agent, out IReadOnlyList<ReviewTaskGate>? agentGates) ||
            agentGates.Count == 0)
        {
            // Like the repository-wide case, an agent with no gates is reported rather than read as a pass.
            report.Add(new Diagnostic(
                NoGatesForAgent,
                Severity.Warning,
                $"No task gates are declared for agent '{agent}', so its change has no executed evidence behind it.",
                "review.task-gates",
                Hint: "Declare the agent's gates under review.task-gates in kyber-weave.yml."));
            CommandHelpers.TryWriteReport(
                settings.Out,
                ReviewJson.Write(new GateReport(GateReport.CurrentSchema, [])),
                ReportWriteFailed,
                report);
            CommandHelpers.Finish(report, settings, "review gates", "Gate");
            return report.HasErrors ? 1 : 0;
        }

        IReadOnlyList<string> changed;
        if (settings.Paths is { Length: > 0 })
        {
            changed = settings.Paths.Distinct(StringComparer.Ordinal).ToArray();
        }
        else
        {
            try
            {
                changed = ChangedFiles.Resolve(root, baseRef);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
            {
                return RefuseInput(report, settings, [$"Could not list the changed files with git: {ex.Message}"]);
            }

            // Paths git reports are checked with the same rules as --paths, because they reach argv the same way.
            List<string> gitRefusals = [];
            foreach (string path in changed)
            {
                if (TaskGateSelection.ValidatePath(root, path) is { } gitProblem)
                    gitRefusals.Add(gitProblem);
            }

            if (gitRefusals.Count > 0)
                return RefuseInput(report, settings, gitRefusals);
        }

        TaskGatePlan plan = TaskGateSelection.Plan(agentGates, changed, settings.TaskSlug, CurrentOs());
        if (plan.Refusals.Count > 0)
            return RefuseInput(report, settings, plan.Refusals);

        foreach (SkippedTaskGate skip in plan.Skipped)
        {
            report.Add(new Diagnostic(
                GateSkipped,
                Severity.Info,
                $"{skip.Id}: skipped, {skip.Reason}.",
                skip.Id));
        }

        TaskGateRun run = GateRunner.RunTaskGates(plan.Gates, root, settings.Fix, settings.StopOnFailure);

        foreach (GateResult gate in run.Report.Gates)
        {
            report.Add(new Diagnostic(
                gate.Passed ? ReviewGateOutcome.Passed : ReviewGateOutcome.Failed,
                gate.Passed ? Severity.Info : gate.Blocking ? Severity.Error : Severity.Warning,
                $"{gate.Id}: {gate.Summary} ({gate.DurationMilliseconds} ms)",
                gate.Id));
        }

        foreach (GateResult fix in run.FixFailures)
        {
            report.Add(new Diagnostic(
                FixFailed,
                Severity.Warning,
                $"{fix.Id}: fix did not succeed ({fix.Summary}). The verify step still decides.",
                fix.Id));
        }

        report.AddMetric("gates", run.Report.Gates.Count);
        report.AddMetric("failed", run.Report.Gates.Count(g => !g.Passed));

        CommandHelpers.TryWriteReport(settings.Out, ReviewJson.Write(run.Report), ReportWriteFailed, report);

        CommandHelpers.Finish(report, settings, "review gates", "Gate");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Reports every refusal, then ends the run before any gate process has started.</summary>
    private static int RefuseInput(DiagnosticReport report, ReviewGatesSettings settings, IEnumerable<string> reasons)
    {
        foreach (string reason in reasons)
        {
            report.Add(new Diagnostic(
                InvalidInput,
                Severity.Error,
                reason,
                "review gates",
                Hint: "Refused before any gate started. Pass repository-relative paths, and a task slug " +
                      "made of lowercase alphanumerics and single hyphens."));
        }

        CommandHelpers.Finish(report, settings, "review gates", "Gate");
        return 1;
    }

    private static void WriteClientInputError(string message)
    {
        AnsiConsole.MarkupLine($"[red]kyber-weave review gates: error: {Markup.Escape(message)}[/]");
    }

    private static string CurrentOs()
    {
        return OperatingSystem.IsWindows() ? "windows" : "unix";
    }
}

/// <summary>Rule identifiers for individual gate outcomes.</summary>
public static class ReviewGateOutcome
{
    /// <summary>A gate that succeeded.</summary>
    public const string Passed = "KW-REVIEW-021";

    /// <summary>A gate that did not succeed.</summary>
    public const string Failed = "KW-REVIEW-022";
}
