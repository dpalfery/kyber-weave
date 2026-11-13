using System.ComponentModel;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
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
    private const string GateNotRun = VerdictEngine.GateNotRun;
    private const string ReportWriteFailed = "KW-REVIEW-025";
    private const string GateSkipped = "KW-REVIEW-026";
    private const string NoGatesForAgent = "KW-REVIEW-027";
    private const string InvalidInput = "KW-REVIEW-028";
    private const string FixFailed = "KW-REVIEW-029";
    private const string GateNotStarted = "KW-REVIEW-033";
    private const string ChangedSetUnknown = "KW-REVIEW-034";
    private const string PriorFindingsUnreadable = "KW-REVIEW-023";
    private const string GateCarried = "KW-REVIEW-048";
    private const string RedAccepted = "KW-REVIEW-051";
    private const string RedRejected = "KW-REVIEW-052";
    private const string SiblingTestsMissingSymbols = "KW-REVIEW-053";
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

        // An option given an empty value names nothing: as a path it resolves to the current directory, and as
        // --agent or --config it would fall back silently. Refused before anything runs.
        string[] empty = new (string Option, string? Value)[]
            {
                ("[path]", settings.Path), ("--config", settings.Config), ("--out", settings.Out),
                ("--agent", settings.Agent)
            }
            .Where(entry => entry.Value is { } given && string.IsNullOrWhiteSpace(given))
            .Select(entry => entry.Option)
            .ToArray();
        if (empty.Length > 0)
        {
            WriteClientInputError(
                $"{InvalidInput}: {string.Join(", ", empty)} was given an empty value. Name a value, or leave the " +
                "option out.");
            return ClientInputExitCode;
        }

        bool scopesTheRun = settings.Agent is not null || settings.BaseRef is not null
                            || settings.Paths.Length > 0 || settings.Gate.Length > 0;
        if (settings.All && scopesTheRun)
        {
            WriteClientInputError(
                $"{InvalidInput}: --all runs all declared repository gates unscoped and cannot be combined with " +
                "--agent, --base, --paths, or --gate.");
            return ClientInputExitCode;
        }

        // --deadline bounds the council's whole run. A named gate is a deliberate re-check outside that budget, and a
        // task-gate run is one developer's check, so neither may be cut by it.
        if (settings.Deadline && (settings.Gate.Length > 0 || settings.Agent is not null))
        {
            WriteClientInputError(
                $"{InvalidInput}: --deadline bounds the full council run, so it cannot be combined with " +
                (settings.Gate.Length > 0 ? "--gate, which re-checks named gates outside that budget." :
                    "--agent, which runs one agent's task gates."));
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
                    "--task, --fix and --red apply only with --agent. Name the agent whose task " +
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

        // A council run in a cycle after the first is a verifier cycle. The recorded review folders say so, so the run
        // itself decides which gates still need to run and carries the rest, instead of relying on the reviewer to ask.
        // The header is printed before any gate starts, so the reviewer can re-check findings while the gates run.
        CouncilCycle? cycle = settings.Council && !settings.All && settings.Gate.Length == 0
            ? DetectCycle(root, config, settings, report)
            : null;
        if (cycle is not null)
        {
            if (cycle.Verifier)
                declared = [.. declared.Where(gate => cycle.Rerun.Contains(gate.Id))];

            if (ReportRenderer.IsHuman(settings.ParsedFormat))
                WriteCycleHeader(cycle);

            RecordCycleIdentity(cycle, report);
            report.AddMetric("cycle", cycle.Plan.Number);
            report.AddMetric("mode", cycle.Mode);
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
        // The run deadline is opt-in with --deadline, so CI, task gates and pre-push keep their behaviour: there, a
        // blocking gate stopped by its own timeout is a failure. --deadline is refused with --gate before this.
        TimeSpan? deadline = settings.Deadline
            ? TimeSpan.FromSeconds(config.Review.CouncilDeadlineSeconds)
            : null;
        GateReport gates = GateRunner.Run(
            new ReviewConfig { Gates = fullSuite.Admitted, Parallelism = config.Review.Parallelism },
            root,
            settings.StopOnFailure,
            changed,
            BaseRef(settings.BaseRef),
            deadline);

        foreach (GateResult gate in gates.Gates)
            AddGateDiagnostic(report, gate, settings.Deadline);

        GateReport written = gates;
        if (cycle is { Verifier: true })
        {
            written = WithCarriedGates(config.Review.Gates, gates, cycle, report);
            report.AddMetric("carried", written.Gates.Count - gates.Gates.Count);
        }

        // Gates named one run at a time into the same report add to it. Replacing the file would lose the earlier
        // runs' results, so a reviewer could never finish a verifier cycle gate by gate.
        if (settings.Gate.Length > 0 && settings.Out is not null)
        {
            written = MergedWithExisting(config.Review.Gates, written, settings.Out);
            report.AddMetric("kept", written.Gates.Count - gates.Gates.Count);
        }

        report.AddMetric("gates", gates.Gates.Count);
        report.AddMetric("failed", gates.Gates.Count(g => !g.Passed && g.NotApplicableReason is null
                                                         && !(settings.Deadline && VerdictEngine.IsStoppedForTime(g))));
        report.AddMetric("not-applicable", gates.Gates.Count(g => g.NotApplicableReason is not null));
        if (settings.Deadline)
            report.AddMetric("not-run", gates.Gates.Count(VerdictEngine.IsStoppedForTime));

        CommandHelpers.TryWriteReport(settings.Out, ReviewJson.Write(written), ReportWriteFailed, report);

        CommandHelpers.Finish(report, settings, "review gates", "Gate");
        if (cycle is not null && ReportRenderer.IsHuman(settings.ParsedFormat))
        {
            Console.WriteLine(
                $"cycle: {cycle.Plan.Number} mode: {cycle.Mode} ran: {gates.Gates.Count} carried: {written.Gates.Count - gates.Gates.Count} " +
                $"failed: {gates.Gates.Count(g => !g.Passed && g.NotApplicableReason is null && !(settings.Deadline && VerdictEngine.IsStoppedForTime(g)))}");
            Console.Out.Flush();
        }

        // Blocking failures are already Error diagnostics; a failed --out write is too.
        // Non-blocking gate failures stay Warning and do not fail the process. Gates that
        // did not apply are Info and never fail it either.
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>The cycle a council run found itself in, and what that decides for each declared gate.</summary>
    /// <param name="Plan">The cycle number, the prior cycle and the decision for each gate.</param>
    /// <param name="Recheck">The prior cycle's findings to re-check.</param>
    /// <param name="Rerun">The gates a verifier cycle runs again.</param>
    /// <param name="Ignored">The recorded cycles that do not belong to this change.</param>
    /// <param name="Tree">The git work tree the review belongs to.</param>
    /// <param name="Folder">The absolute folder this cycle writes to, when it is known.</param>
    /// <param name="Notes">Plain lines about where the review runs.</param>
    /// <param name="Verifier">Whether only the gates that need it run: a later cycle that was not asked for <c>--full</c>.</param>
    /// <param name="WritesIdentity">Whether this run records the change in the cycle folder.</param>
    /// <param name="BaseRef">The ref the change's base is resolved against, or null for the default.</param>
    private sealed record CouncilCycle(
        ReviewCyclePlan Plan,
        IReadOnlyList<ReviewFinding> Recheck,
        HashSet<string> Rerun,
        IReadOnlyList<IgnoredCycle> Ignored,
        string Tree,
        string? Folder,
        IReadOnlyList<string> Notes,
        bool Verifier,
        bool WritesIdentity,
        string? BaseRef)
    {
        public string Mode => Verifier ? "verifier" : "full";
    }

    /// <summary>
    ///     The cycle this council run is in, or null when no review folder can be named. The review folder comes from the
    ///     <c>--out</c> path when it sits in <c>review/cycle-&lt;n&gt;</c>, else from the task's only review folder in
    ///     the git work tree the command was run against. A cycle counts as prior only when it belongs to this change
    ///     (<see cref="ReviewCycles.Belonging"/>); one that is finished, this one included, or that belongs to another
    ///     change is never counted. When nothing says this is a later cycle, the run is a full one, which is the safe answer.
    /// </summary>
    private static CouncilCycle? DetectCycle(
        string root, KyberWeaveConfig config, ReviewGatesSettings settings, DiagnosticReport report)
    {
        string tree = ReviewWorkTree.Of(root);
        List<string> notes = [];
        if (ReviewWorkTree.LiveRunNote(tree, ReviewWorkTree.HomeDirectory()) is { } liveRun)
            notes.Add(liveRun);

        string reviewFolder;
        int? thisCycle = null;
        string? folder = null;
        if (settings.Out is not null && ReviewCycleView.TryFromOutPath(settings.Out, out string fromOut, out int number))
        {
            reviewFolder = fromOut;
            thisCycle = number;
            folder = Path.GetDirectoryName(Path.GetFullPath(settings.Out));
            if (!ReviewCycleView.IsUnder(tree, fromOut))
                notes.Add($"worktree: --out is outside {tree}, so this review writes to {folder}; use {ReviewWorkTree.Scratchpad(tree)} for this worktree's review folders.");
        }
        else if (ReviewCycleView.TryChooseTask(tree, ReviewWorkTree.Scratchpad(tree), null, out string task, out _, out _))
        {
            reviewFolder = Path.Combine(ReviewWorkTree.Scratchpad(tree), task, "review");
        }
        else
        {
            return null;
        }

        PriorCycles prior = ReviewWorkTree.Prior(
            tree,
            settings.BaseRef,
            [.. ReviewCycles.ReadFinished(reviewFolder).Where(record => thisCycle is null || record.Number < thisCycle)]);
        IReadOnlyList<ReviewCycleRecord> finished = prior.Valid;

        ReviewCyclePlan plan = ReviewCycles.Plan(
            finished, config.Review.Gates, CurrentOs(), commit => ReviewCycleView.TryChangedSince(tree, commit));
        folder ??= Path.Combine(reviewFolder, $"cycle-{plan.Number}");
        List<ReviewFinding> recheck = [];
        if (plan.Prior is { } priorCycle)
            ReviewCycleView.TryReadRecheck(priorCycle, report, recheck, PriorFindingsUnreadable, Severity.Warning, "review gates");

        return new CouncilCycle(
            plan,
            recheck,
            [.. plan.Gates.Where(gate => gate.Action == GateCycleAction.Rerun).Select(gate => gate.GateId)],
            prior.Ignored,
            tree,
            folder,
            notes,
            plan.IsVerifier && !settings.Full,
            thisCycle is not null,
            settings.BaseRef);
    }

    /// <summary>Records the branch, base and head this cycle reviews, so a later cycle can tell the cycle belongs to its change.</summary>
    private static void RecordCycleIdentity(CouncilCycle cycle, DiagnosticReport report)
    {
        if (!cycle.WritesIdentity || cycle.Folder is null || ReviewWorkTree.IdentityOf(cycle.Tree, cycle.BaseRef) is not { } identity)
            return;

        try
        {
            ReviewCycles.WriteIdentity(cycle.Folder, identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                ReportWriteFailed,
                Severity.Warning,
                $"Could not record the change in '{cycle.Folder}': {ex.Message}",
                "review gates",
                Hint: "Without it the next cycle cannot tell this folder belongs to the change, and runs in full."));
        }
    }

    /// <summary>The cycle, mode, folder, ignored cycles, findings to re-check and gate plan, at the top of the output a reviewer reads first.</summary>
    private static void WriteCycleHeader(CouncilCycle cycle)
    {
        Console.WriteLine($"cycle: {cycle.Plan.Number} mode: {cycle.Mode}");
        if (cycle.Folder is not null)
            Console.WriteLine($"folder: {cycle.Folder}");

        foreach (string note in cycle.Notes)
            Console.WriteLine(note);

        foreach (string line in ReviewCycleView.IgnoredLines(cycle.Tree, cycle.Ignored))
            Console.WriteLine(line);

        if (cycle.Verifier)
        {
            foreach (string line in ReviewCycleView.VerifierLines(cycle.Plan, cycle.Recheck))
                Console.WriteLine(line);
        }

        Console.Out.Flush();
    }

    /// <summary>
    ///     The gate report with an earlier passing result in place of each gate this cycle did not run, in declaration
    ///     order, so the report is complete and the verdict reads the same evidence it would from a full run.
    /// </summary>
    private static GateReport WithCarriedGates(
        IReadOnlyList<ReviewGate> declared, GateReport ran, CouncilCycle cycle, DiagnosticReport report)
    {
        List<GateResult> results = [];
        foreach (ReviewGate gate in declared)
        {
            if (ran.Gates.FirstOrDefault(result => string.Equals(result.Id, gate.Id, StringComparison.Ordinal)) is { } own)
            {
                results.Add(own);
                continue;
            }

            GateCycleDecision? decision = cycle.Plan.Gates.FirstOrDefault(
                candidate => candidate is { Action: GateCycleAction.Carry, Carried: not null }
                             && string.Equals(candidate.GateId, gate.Id, StringComparison.Ordinal));
            if (decision?.Carried is not { } carried)
                continue;

            results.Add(carried with { Summary = $"carried from cycle {decision.FromCycle}: {carried.Summary}" });
            report.Add(new Diagnostic(
                GateCarried,
                Severity.Info,
                $"{gate.Id}: carried from cycle {decision.FromCycle} ({decision.Reason}).",
                gate.Id));
        }

        results.AddRange(ran.Gates.Where(result => !results.Contains(result)));
        return ran with { Gates = results };
    }

    /// <summary>
    ///     The report already at <paramref name="path"/>, with each result this run produced in place of the earlier
    ///     result for the same gate and the rest kept, in declaration order. An unreadable or missing report is nothing
    ///     to keep.
    /// </summary>
    private static GateReport MergedWithExisting(IReadOnlyList<ReviewGate> declared, GateReport ran, string path)
    {
        GateReport existing;
        try
        {
            if (!File.Exists(path))
                return ran;

            existing = ReviewJson.ReadGates(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ran;
        }

        List<GateResult> results = [];
        foreach (ReviewGate gate in declared)
        {
            GateResult? pick = ran.Gates.FirstOrDefault(result => string.Equals(result.Id, gate.Id, StringComparison.Ordinal))
                               ?? existing.Gates.FirstOrDefault(result => string.Equals(result.Id, gate.Id, StringComparison.Ordinal));
            if (pick is not null)
                results.Add(pick);
        }

        results.AddRange(existing.Gates.Where(old => results.All(kept => !string.Equals(kept.Id, old.Id, StringComparison.Ordinal))));
        return ran with { Gates = results };
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

        if (settings.TaskSlug is not null && TaskSlug.Normalise(settings.TaskSlug) is { Length: > 0 } normalised)
            settings.TaskSlug = normalised;

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

        // The classifiers read what each verify command printed, so a build that fails on missing symbols is told
        // apart from one that fails on anything else.
        Dictionary<string, string> outputs = new(StringComparer.Ordinal);
        IReadOnlyList<string>? brokenBuildSymbols = null;
        TaskGateObserver observer = new()
        {
            Output = (id, output) => outputs[id] = output,
            After = (gate, result) =>
            {
                // A build accepted as RED on missing symbols leaves nothing for a later test gate to run against.
                if (settings.Red && !result.Passed && brokenBuildSymbols is null && outputs.TryGetValue(gate.Id, out string? output))
                    brokenBuildSymbols = RedClassifier.AcceptedMissingSymbols(
                        new RedGateObservation(gate.Id, string.Join(' ', gate.Run), result.ExitCode, output), changed);
            },
            SkipReason = gate => settings.Red && brokenBuildSymbols is not null && RedClassifier.IsTestCommand(string.Join(' ', gate.Run))
                ? RedClassifier.BuildSkipReason(brokenBuildSymbols)
                : null,
        };
        TaskGateRun run = GateRunner.RunTaskGates(
            plan.Gates, root, settings.Fix, settings.StopOnFailure && !settings.Red, observer);

        GateReport finalReport = run.Report;
        RedOutcome? redOutcome = null;
        if (settings.Red)
        {
            (finalReport, redOutcome) = ApplyRed(run.Report, plan.Gates, outputs, changed, repoRoot, report);
        }

        foreach (GateResult gate in finalReport.Gates)
            AddGateDiagnostic(report, gate, timeIsNotRun: false);

        if (!settings.Red)
        {
            AddSiblingNotes(report, plan.Gates, run.Report, outputs, changed);
        }

        foreach (GateResult fix in run.FixFailures)
            report.Add(new Diagnostic(
                FixFailed,
                Severity.Warning,
                $"{fix.Id}: fix did not succeed ({fix.Summary}). The verify step still decides.",
                fix.Id));

        report.AddMetric("gates", finalReport.Gates.Count);
        report.AddMetric("failed", finalReport.Gates.Count(g => !g.Passed && !g.NotApplicable));

        CommandHelpers.TryWriteReport(settings.Out, ReviewJson.Write(finalReport), ReportWriteFailed, report);

        CommandHelpers.Finish(report, settings, "review gates", "Gate");
        if (redOutcome is not null && ReportRenderer.IsHuman(settings.ParsedFormat))
            WriteRedLines(redOutcome, agent, finalReport, settings.Out);

        return report.HasErrors ? 1 : 0;
    }

    /// <summary>
    ///     The last lines of a RED run, ready to paste into the digest: the task-gate status for the report just written,
    ///     then <c>RED_EVIDENCE:</c> when the run is valid RED or <c>RED_REJECTED:</c> with the reason when it is not.
    /// </summary>
    private static void WriteRedLines(RedOutcome outcome, string agent, GateReport report, string? reportPath)
    {
        int skipped = report.Gates.Count(gate => gate.NotApplicable);
        string failed = outcome.Passed
            ? "none"
            : string.Join(",", report.Gates.Where(gate => !gate.Passed && !gate.NotApplicable).Select(gate => gate.Id).DefaultIfEmpty("none"));
        Console.WriteLine(
            $"TASK_GATE: {(outcome.Passed ? "PASS" : "FAIL")} — agent={agent} gates={report.Gates.Count} failed={failed} skipped={skipped} report={reportPath ?? "<none>"} runs=1");
        Console.WriteLine(outcome.Passed ? outcome.EvidenceLine : $"RED_REJECTED: {outcome.Reason}");
        Console.Out.Flush();
    }

    /// <summary>
    ///     Judges a RED task's gate runs. A failed gate the classifier accepts is reported as passed with the reason, one
    ///     it rejects keeps its failure, and the verdict is one diagnostic carrying the line for the digest or the next step.
    /// </summary>
    private static (GateReport Report, RedOutcome Outcome) ApplyRed(
        GateReport ran,
        IReadOnlyList<PlannedTaskGate> planned,
        Dictionary<string, string> outputs,
        IReadOnlyList<string> changed,
        string repositoryRoot,
        DiagnosticReport report)
    {
        List<RedGateObservation> observations = [];
        foreach (GateResult gate in ran.Gates)
        {
            PlannedTaskGate? plannedGate = planned.FirstOrDefault(candidate => string.Equals(candidate.Id, gate.Id, StringComparison.Ordinal));
            if (plannedGate is not null && outputs.TryGetValue(gate.Id, out string? output))
                observations.Add(new RedGateObservation(gate.Id, string.Join(' ', plannedGate.Run), gate.ExitCode, output));
        }

        RedOutcome outcome = RedClassifier.Evaluate(observations, changed, repositoryRoot);
        List<GateResult> results = [];
        foreach (GateResult gate in ran.Gates)
        {
            if (outcome.AcceptedGates.TryGetValue(gate.Id, out string? why))
                results.Add(gate with { ExitCode = 0, Summary = "RED accepted: " + why });
            else
                results.Add(gate);
        }

        if (outcome.Passed)
        {
            report.Add(new Diagnostic(
                RedAccepted,
                Severity.Info,
                outcome.EvidenceLine,
                "review gates --red",
                Hint: "The failing run is recorded in this run's report; now implement the task and run the task gate again without --red."));
        }
        else
        {
            report.Add(new Diagnostic(
                RedRejected,
                Severity.Error,
                $"Not valid RED evidence: {outcome.Reason}",
                "review gates --red",
                Hint: "Valid RED is tests that ran and failed on an assertion, or a build that failed only on missing-symbol "
                      + "errors in the files this task changed. Fix what the message names, then run the same command again."));
        }

        return (ran with { Gates = results }, outcome);
    }

    /// <summary>
    ///     Tells a developer whose build fails only because other tasks' tests name symbols nobody has created that this
    ///     is the cause, in the exact words to report, so it does not have to guess.
    /// </summary>
    private static void AddSiblingNotes(
        DiagnosticReport report,
        IReadOnlyList<PlannedTaskGate> planned,
        GateReport ran,
        Dictionary<string, string> outputs,
        IReadOnlyList<string> changed)
    {
        foreach (GateResult gate in ran.Gates.Where(result => !result.Passed))
        {
            PlannedTaskGate? plannedGate = planned.FirstOrDefault(candidate => string.Equals(candidate.Id, gate.Id, StringComparison.Ordinal));
            if (plannedGate is null || !outputs.TryGetValue(gate.Id, out string? output))
                continue;

            RedGateObservation observation = new(gate.Id, string.Join(' ', plannedGate.Run), gate.ExitCode, output);
            if (RedClassifier.SiblingSymbols(observation, changed) is not { Count: > 0 } symbols)
                continue;

            report.Add(new Diagnostic(
                SiblingTestsMissingSymbols,
                Severity.Warning,
                $"NOT VERIFIED: {observation.Command} — sibling RED tests reference symbols no task has created yet: {string.Join(", ", symbols)}",
                gate.Id,
                Hint: "Every compiler error is a missing symbol in a file this task did not change. Build the production projects "
                      + "this task touched, report the line above in the digest, and do not edit those tests."));
        }
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
            Hint: $"kyber-weave doctor --ensure {ShellQuote.Quote(root)}"));
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
    ///     Under <c>--deadline</c>, a gate that time stopped is <see cref="GateNotRun" />, a warning: it reached no
    ///     result, so it neither passed nor failed and does not decide the exit code. Without it, a timed-out gate
    ///     is a failed check, as it always was, so CI and task gates keep failing on a gate that hangs.
    /// </summary>
    private static void AddGateDiagnostic(DiagnosticReport report, GateResult gate, bool timeIsNotRun)
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

        if (timeIsNotRun && VerdictEngine.IsStoppedForTime(gate))
        {
            // A deadline cut is the council running out of budget, not a slow gate, so raising the gate's own
            // timeout would change nothing; the remedy is to run it alone.
            string hint = gate.Outcome == GateOutcome.DeadlinePassed
                ? $"Re-run it alone with 'kyber-weave review gates --gate {gate.Id}'."
                : $"Re-run it alone with 'kyber-weave review gates --gate {gate.Id}', or give it a longer time limit.";
            report.Add(new Diagnostic(
                GateNotRun,
                Severity.Warning,
                $"{detail}. NOT RUN to a result, so what it checks is unverified.",
                gate.Id,
                Hint: hint));
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
