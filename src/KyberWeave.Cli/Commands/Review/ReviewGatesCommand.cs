using System.ComponentModel;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     Runs the deterministic gates the host declares under <c>review.gates</c>, or, with <c>--agent</c>,
///     one developer agent's gates under <c>review.task-gates</c> against the change that agent made.
/// </summary>
public sealed class ReviewGatesCommand : Command<ReviewGatesSettings>
{
    private const string NoGatesDeclared = "KW-REVIEW-020";
    private const string ReportWriteFailed = "KW-REVIEW-025";
    private const string GateSkipped = "KW-REVIEW-026";
    private const string NoGatesForAgent = "KW-REVIEW-027";
    private const string InvalidInput = "KW-REVIEW-028";
    private const string FixFailed = "KW-REVIEW-029";
    private const string GateNotStarted = "KW-REVIEW-033";
    private const string ChangedSetUnknown = "KW-REVIEW-034";
    private const string CouncilDefaultBase = "origin/HEAD";
    private const int ClientInputExitCode = 2;

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewGatesSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.All && (settings.Agent is not null || settings.BaseRef is not null || settings.Paths.Length > 0))
        {
            WriteClientInputError(
                $"{InvalidInput}: --all runs all declared repository gates unscoped and cannot be combined with " +
                "--agent, --base, or --paths.");
            return ClientInputExitCode;
        }

        if (settings.Agent is null)
        {
            if (settings.HasAgentOnlyOptions)
            {
                WriteClientInputError(
                    "--task and --fix apply only with --agent. Name the agent whose task " +
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

        // Inputs are checked before any gate is considered, so an unsafe path or base is refused the same
        // way whether or not the host declares a gate that uses it.
        List<string> refusals = [];
        if (settings.BaseRef is not null && ChangedFiles.ValidateBase(settings.BaseRef) is { } baseProblem)
            refusals.Add(baseProblem);

        foreach (string path in settings.Paths)
            if (TaskGateSelection.ValidatePath(root, path) is { } pathProblem)
                refusals.Add(pathProblem);

        if (refusals.Count > 0)
            return RefuseInput(report, settings, refusals);

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

        // The changed set is only needed when a gate is scoped by include; it is never computed for gates alone.
        IReadOnlyList<string>? changed = null;
        string? unknownReason = null;
        if (!settings.All && config.Review.Gates.Any(gate => gate.Include.Count > 0))
        {
            (changed, unknownReason) = ResolveCouncilChangedSet(root, settings);
            if (changed is null)
            {
                // Fail closed: every gate runs, and the report says why none was skipped for include.
                report.Add(new Diagnostic(
                    ChangedSetUnknown,
                    Severity.Info,
                    $"changed set unknown ({unknownReason}); gates with include ran unscoped.",
                    "review.gates",
                    Hint: "Fetch the full history, or name a base that exists with --base."));
            }
            else
            {
                List<string> changedRefusals = [];
                foreach (string path in changed)
                    if (TaskGateSelection.ValidatePath(root, path) is { } changedProblem)
                        changedRefusals.Add(changedProblem);

                if (changedRefusals.Count > 0)
                    return RefuseInput(report, settings, changedRefusals);
            }
        }

        CouncilGatePlan plan = CouncilGateSelection.Plan(config.Review.Gates, changed, CurrentOs());
        foreach (SkippedTaskGate skip in plan.Skipped)
            report.Add(new Diagnostic(
                GateSkipped,
                Severity.Info,
                $"{skip.Id}: skipped, {skip.Reason}.",
                skip.Id));

        // GateRunner.Run reads only Gates from its configuration, so the selected gates travel in a
        // configuration of their own and the runner itself stays unchanged.
        GateReport gates = GateRunner.Run(new ReviewConfig { Gates = plan.Run }, root, settings.StopOnFailure);

        foreach (GateResult gate in gates.Gates)
            AddGateDiagnostic(report, gate);

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
            if (TaskGateSelection.ValidatePath(root, path) is { } pathProblem)
                refusals.Add(pathProblem);

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
                if (TaskGateSelection.ValidatePath(root, path) is { } gitProblem)
                    gitRefusals.Add(gitProblem);

            if (gitRefusals.Count > 0)
                return RefuseInput(report, settings, gitRefusals);
        }

        TaskGatePlan plan = TaskGateSelection.Plan(agentGates, changed, settings.TaskSlug, CurrentOs());
        if (plan.Refusals.Count > 0)
            return RefuseInput(report, settings, plan.Refusals);

        foreach (SkippedTaskGate skip in plan.Skipped)
            report.Add(new Diagnostic(
                GateSkipped,
                Severity.Info,
                $"{skip.Id}: skipped, {skip.Reason}.",
                skip.Id));

        TaskGateRun run = GateRunner.RunTaskGates(plan.Gates, root, settings.Fix, settings.StopOnFailure);

        foreach (GateResult gate in run.Report.Gates)
            AddGateDiagnostic(report, gate);

        foreach (GateResult fix in run.FixFailures)
            report.Add(new Diagnostic(
                FixFailed,
                Severity.Warning,
                $"{fix.Id}: fix did not succeed ({fix.Summary}). The verify step still decides.",
                fix.Id));

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
            report.Add(new Diagnostic(
                InvalidInput,
                Severity.Error,
                reason,
                "review gates",
                Hint: "Refused before any gate started. Pass repository-relative paths, and a task slug " +
                      "made of lowercase alphanumerics and single hyphens."));

        CommandHelpers.Finish(report, settings, "review gates", "Gate");
        return 1;
    }

    /// <summary>
    ///     The paths the council's include patterns are matched against: the <c>--paths</c> given, else the diff from
    ///     the merge base of <c>--base</c> (default <c>origin/HEAD</c>) to the working tree. When git cannot list the
    ///     changes, the set is null and the reason is returned, so the caller can run every gate and say why.
    /// </summary>
    private static (IReadOnlyList<string>? Changed, string? UnknownReason) ResolveCouncilChangedSet(
        string root, ReviewGatesSettings settings)
    {
        if (settings.Paths is { Length: > 0 })
            return (settings.Paths.Distinct(StringComparer.Ordinal).ToArray(), null);

        try
        {
            IReadOnlyList<string> changed = ChangedFiles
                .ResolveSinceMergeBase(root, settings.BaseRef ?? CouncilDefaultBase)
                .Select(change => change.Path)
                .ToArray();
            return (changed, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            return (null, ex.Message.Trim());
        }
    }

    /// <summary>
    ///     Reports one gate. A gate whose program never started is <see cref="GateNotStarted" />, kept apart from a
    ///     check that ran and failed (<see cref="ReviewGateOutcome.Failed" />), because they are fixed in different places.
    /// </summary>
    private static void AddGateDiagnostic(DiagnosticReport report, GateResult gate)
    {
        string detail = $"{gate.Id}: {gate.Summary} ({gate.DurationMilliseconds} ms)";

        if (gate.Outcome == GateOutcome.NotStarted)
        {
            report.Add(new Diagnostic(
                GateNotStarted,
                gate.Blocking ? Severity.Error : Severity.Warning,
                detail,
                gate.Id));
            return;
        }

        report.Add(new Diagnostic(
            gate.Passed ? ReviewGateOutcome.Passed : ReviewGateOutcome.Failed,
            gate.Passed ? Severity.Info : gate.Blocking ? Severity.Error : Severity.Warning,
            detail,
            gate.Id));
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
