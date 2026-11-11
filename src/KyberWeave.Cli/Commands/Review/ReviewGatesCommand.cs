using System.ComponentModel;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Doctor;
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
    private const string IndexState = "KW-REVIEW-038";
    private const string ReportWriteFailed = "KW-REVIEW-025";
    private const string GateSkipped = "KW-REVIEW-026";
    private const string NoGatesForAgent = "KW-REVIEW-027";
    private const string InvalidInput = "KW-REVIEW-028";
    private const string FixFailed = "KW-REVIEW-029";
    private const string GateNotStarted = "KW-REVIEW-033";
    private const string ChangedSetUnknown = "KW-REVIEW-034";
    private const string CouncilDefaultBase = "origin/HEAD";
    private const int ClientInputExitCode = 2;

    /// <summary>The bound on the CodeGraph index run, the same default <c>doctor --ensure</c> uses.</summary>
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(DoctorSettings.DefaultIndexTimeoutSeconds);

    private static readonly TimeSpan McpTimeout = TimeSpan.FromSeconds(DoctorSettings.DefaultMcpTimeoutSeconds);

    /// <summary>
    ///     The process seam for the CodeGraph child that prepares the index. Null uses a process executor bounded by
    ///     the index timeout; tests pass a scripted one.
    /// </summary>
    internal IProcessExecutor? Executor { get; init; }

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewGatesSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool scopesTheRun = settings.Agent is not null || settings.BaseRef is not null
                            || settings.Paths.Length > 0 || settings.Gate.Length > 0;
        if (settings.All && scopesTheRun)
        {
            WriteClientInputError(
                $"{InvalidInput}: --all runs all declared repository gates unscoped and cannot be combined with " +
                "--agent, --base, --paths, or --gate.");
            return ClientInputExitCode;
        }

        // --gate selects from review.gates, and --agent selects from review.task-gates. A run cannot be both,
        // so it is refused rather than silently resolving to one of them.
        if (settings.Gate.Length > 0 && settings.Agent is not null)
        {
            WriteClientInputError(
                $"{InvalidInput}: --gate names a gate declared under review.gates, so it cannot be combined with " +
                "--agent, which runs one agent's task gates.");
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

    private int ExecuteRepositoryGates(ReviewGatesSettings settings)
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

        // --gate narrows the run to the gates named, so a reviewer re-checks one gate without paying for the whole
        // suite again. Selection happens before anything else reads the gate list, so include matching, the
        // full-suite admission rule and the report all see the same set.
        IReadOnlyList<ReviewGate> declared = config.Review.Gates;
        if (settings.Gate.Length > 0)
        {
            List<string> gateRefusals = [];
            declared = SelectNamedGates(config.Review.Gates, settings.Gate, gateRefusals);
            if (gateRefusals.Count > 0)
            {
                return RefuseInput(
                    report,
                    settings,
                    gateRefusals,
                    "Refused before any gate started. --gate takes the id of a gate declared under review.gates.");
            }
        }

        // Before any gate process starts, so a review in a fresh worktree gets an index instead of silently having
        // none. A repository without governed docs has no index to keep and is not checked.
        // The index belongs to the tree, so a run pointed at a subdirectory still checks the tree's root.
        string indexRoot = EnclosingRepositoryRoot(root);
        WorktreeIndexOutcome index = WorktreeIndexGuard.Ensure(
            indexRoot,
            new DoctorOptions(
                IndexTimeout,
                McpTimeout,
                DoctorCommand.CodeGraphExecutable,
                DoctorCommand.DefaultMcpExecutable),
            Executor ?? ProcessExecutor.WithTimeout(IndexTimeout),
            Path.GetTempPath());
        if (index.Status is not WorktreeIndexStatus.NotGoverned)
            AddIndexDiagnostic(report, indexRoot, index);

        // The changed set is only needed when a gate is scoped by include; it is never computed for gates alone.
        IReadOnlyList<string>? changed = null;
        string? unknownReason = null;
        if (!settings.All && NeedsChangedSet(declared))
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

        CouncilGatePlan plan = CouncilGateSelection.Plan(declared, changed, CurrentOs());
        foreach (SkippedTaskGate skip in plan.Skipped)
            report.Add(new Diagnostic(
                GateSkipped,
                Severity.Info,
                $"{skip.Id}: skipped, {skip.Reason}.",
                skip.Id));

        // Full-suite gates run only for the council or CI, or for a person at a terminal. A refusal is a blocking
        // error that never starts its gate, so it is reported as not started, never as a gate that ran and failed.
        FullSuiteSelection fullSuite = FullSuiteGate.Select(plan.Run, settings.Council, !Console.IsInputRedirected);
        foreach (FullSuiteRefusal refusal in fullSuite.Refused)
            report.Add(new Diagnostic(
                GateNotStarted,
                Severity.Error,
                $"{refusal.GateId}: refused. {refusal.Message}",
                refusal.GateId));

        // --all asks for the whole declared suite at once, so once one of its gates is refused the suite cannot be
        // delivered and no gate starts: running the rest would spend the run on a review the caller was just told
        // it may not have, and the report would read as a review that ran. A scoped run is the opposite case — the
        // pre-push self-review reads the other gates as its evidence — so it keeps running them.
        if (settings.All && fullSuite.Refused.Count > 0)
        {
            CommandHelpers.TryWriteReport(
                settings.Out,
                ReviewJson.Write(new GateReport(GateReport.CurrentSchema, [])),
                ReportWriteFailed,
                report);
            CommandHelpers.Finish(report, settings, "review gates", "Gate");
            return 1;
        }

        // GateRunner.Run reads only Gates from its configuration, so the selected gates travel in a
        // configuration of their own and the runner itself stays unchanged. The changed set and base
        // are still passed so applies-when is evaluated from the same facts include was selected on.
        GateReport gates = GateRunner.Run(
            new ReviewConfig { Gates = fullSuite.Admitted },
            root,
            settings.StopOnFailure,
            changed,
            BaseRef(settings.BaseRef));

        foreach (GateResult gate in gates.Gates)
            AddGateDiagnostic(report, gate);

        report.AddMetric("gates", gates.Gates.Count);
        report.AddMetric("failed", gates.Gates.Count(g => !g.Passed && g.NotApplicableReason is null));
        report.AddMetric("not-applicable", gates.Gates.Count(g => g.NotApplicableReason is not null));

        CommandHelpers.TryWriteReport(settings.Out, ReviewJson.Write(gates), ReportWriteFailed, report);

        CommandHelpers.Finish(report, settings, "review gates", "Gate");
        // Blocking failures are already Error diagnostics; a failed --out write is too.
        // Non-blocking gate failures stay Warning and do not fail the process. Gates that
        // did not apply are Info and never fail it either.
        return report.HasErrors ? 1 : 0;
    }

    private static string? BaseRef(string? baseRef) =>
        string.IsNullOrWhiteSpace(baseRef) ? null : baseRef.Trim();

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

        string repoRoot = EnclosingRepositoryRoot(root);
        TaskGatePlan plan = TaskGateSelection.Plan(
            agentGates, changed, settings.TaskSlug, CurrentOs(), repoRoot, config.Review.TestMap);
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

    /// <summary>
    ///     The declared gates the caller named with <c>--gate</c>, in declaration order, or an empty list with a
    ///     refusal naming what is wrong.
    /// </summary>
    /// <remarks>
    ///     Declaration order rather than the order the ids were given, because the gate report reads in the order the
    ///     host declares its suite. An id that names no declared gate is refused: a run that quietly executed nothing
    ///     would report zero gates, which a reader is entitled to take as a review that passed.
    /// </remarks>
    private static IReadOnlyList<ReviewGate> SelectNamedGates(
        IReadOnlyList<ReviewGate> gates, IReadOnlyList<string> named, List<string> refusals)
    {
        HashSet<string> wanted = new(named, StringComparer.Ordinal);
        List<string> unknown = [];
        foreach (string id in named)
        {
            if (!gates.Any(gate => string.Equals(gate.Id, id, StringComparison.Ordinal))
                && !unknown.Contains(id, StringComparer.Ordinal))
            {
                unknown.Add(id);
            }
        }

        if (unknown.Count > 0)
        {
            refusals.Add(
                $"--gate names no declared repository gate: {string.Join(", ", unknown)}. " +
                $"review.gates declares {string.Join(", ", gates.Select(gate => gate.Id))}.");
            return [];
        }

        return [.. gates.Where(gate => wanted.Contains(gate.Id))];
    }

    /// <summary>Reports every refusal, then ends the run before any gate process has started.</summary>
    private static int RefuseInput(
        DiagnosticReport report,
        ReviewGatesSettings settings,
        IEnumerable<string> reasons,
        string? hint = null)
    {
        string defaultHint = "Refused before any gate started. Pass repository-relative paths, and a task slug " +
                             "made of lowercase alphanumerics and single hyphens.";
        foreach (string reason in reasons)
            report.Add(new Diagnostic(
                InvalidInput,
                Severity.Error,
                reason,
                "review gates",
                Hint: hint ?? defaultHint));

        CommandHelpers.Finish(report, settings, "review gates", "Gate");
        return 1;
    }

    /// <summary>
    ///     Reports the index state under <see cref="IndexState" />. A failed index is a warning and never stops the
    ///     gates: a review is a read-only run, and ADR 0061 keeps read-only runs going without an index.
    /// </summary>
    private static void AddIndexDiagnostic(DiagnosticReport report, string root, WorktreeIndexOutcome index)
    {
        if (index.Status is WorktreeIndexStatus.Ready)
        {
            report.Add(new Diagnostic(
                IndexState,
                Severity.Info,
                $"The CodeGraph index for '{root}' was {(index.Created ? "created" : "synced")}.",
                "review.gates"));
            return;
        }

        report.Add(new Diagnostic(
            IndexState,
            Severity.Warning,
            $"The CodeGraph index for '{root}' could not be prepared ({IndexReason(index)}), so the gates run anyway.",
            "review.gates",
            Hint: $"kyber-weave doctor --ensure {root}"));
    }

    private static string IndexReason(WorktreeIndexOutcome index) =>
        index.Checks.FirstOrDefault(check => !check.Passed)?.Reason ?? "no reason was reported";

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

        if (gate.NotApplicableReason is not null)
        {
            report.Add(new Diagnostic(
                ReviewGateOutcome.NotApplicable,
                Severity.Info,
                detail,
                gate.Id));
            return;
        }

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

    /// <summary>
    /// Whether the changed set must be computed before gates run. A gate can be scoped
    /// either by <c>include</c> or by <c>applies-when.paths</c>; either one makes a null
    /// changed set mean "run everything".
    /// </summary>
    internal static bool NeedsChangedSet(IReadOnlyList<ReviewGate> gates) =>
        gates.Any(gate => gate.Include.Count > 0 || gate.AppliesWhen is not null);

    private static string EnclosingRepositoryRoot(string path)
    {
        for (DirectoryInfo? dir = new(path); dir is not null; dir = dir.Parent)
        {
            string gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath) || Directory.Exists(Path.Combine(dir.FullName, ".kyber-weave")))
                return dir.FullName;
        }
        return path;
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

    /// <summary>A gate that does not apply to the change and was not executed.</summary>
    public const string NotApplicable = "KW-REVIEW-026";
}
