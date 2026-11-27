using KyberWeave.Core;
using System.ComponentModel;
using System.Globalization;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Application.Review;

/// <summary>
/// Runs the deterministic gates a review executes: the repository's declared suite, or one developer agent's task gates
/// against the change that agent made.
/// </summary>
/// <param name="trees">Finds the git work tree the review belongs to.</param>
/// <param name="cycles">Reads and writes the recorded review cycle folders.</param>
/// <param name="git">Lists the changed files and names the commits the change is measured from.</param>
/// <param name="evidenceWriter">Writes the diff and the gate results into the cycle folder.</param>
/// <param name="notSeatedStore">Keeps the gate-proven lenses a cycle did not seat.</param>
/// <param name="projectFiles">Names the project file a changed file belongs to.</param>
/// <param name="gateRunner">Runs the gates.</param>
/// <param name="duplicateDetection">Reports duplicated bodies, which decides the gate-proven seats.</param>
/// <param name="graphs">Reads the CodeGraph index the duplicate report is computed over.</param>
/// <param name="worktreeIndex">Makes sure the tree has a CodeGraph index before any gate starts.</param>
/// <param name="files">Reads and writes the report, the logs and the cycle folder.</param>
/// <param name="environment">Names the temporary directory the index lock lives in, and the working directory a relative path resolves against.</param>
/// <param name="pathResolver">Resolves the paths the caller spelled, relative ones against the working directory.</param>
/// <param name="selector">Finds the review folder and the prior cycle's findings.</param>
/// <param name="configs">Loads the host's configuration.</param>
/// <param name="taskGates">Checks and expands one agent's task gates.</param>
/// <param name="targets">Finds the task a <c>--task</c> value names.</param>
public sealed class ReviewGatesService(
    IReviewWorkTreeService trees,
    IReviewCycleStore cycles,
    IGitChangeReader git,
    IReviewEvidenceService evidenceWriter,
    INotSeatedStore notSeatedStore,
    IProjectFileLocator projectFiles,
    IGateRunService gateRunner,
    IDuplicateDetectionService duplicateDetection,
    ICodeGraphPortsFactory graphs,
    IWorktreeIndexGuard worktreeIndex,
    IWritableFileSystem files,
    IProcessEnvironment environment,
    IPathResolver pathResolver,
    IReviewCycleSelector selector,
    IConfigLoadService configs,
    ITaskGateSelectionService taskGates,
    IPlanTaskTargetService targets) : IReviewGatesService
{
    /// <inheritdoc />
    public ReviewCommandResult Run(
        ReviewGatesRequest request,
        IProcessExecutor indexExecutor,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(indexExecutor);
        ArgumentNullException.ThrowIfNull(output);

        return request.Agent is null
            ? new RepositoryGateRun(
                trees,
                cycles,
                git,
                evidenceWriter,
                notSeatedStore,
                gateRunner,
                duplicateDetection,
                graphs,
                worktreeIndex,
                files,
                environment,
                pathResolver,
                selector,
                configs,
                taskGates).Run(request, indexExecutor, output)
            : new AgentGateRun(
                configs,
                git,
                gateRunner,
                projectFiles,
                files,
                environment,
                pathResolver,
                taskGates,
                targets).Run(request, output);
    }

    /// <summary>
    ///     The report already at <paramref name="path"/>, merged into this run's, or this run's alone when the file is
    ///     missing or unreadable. The path is resolved the same way it is written, so a relative <c>--out</c> reads
    ///     back what an earlier run wrote.
    /// </summary>
    /// <param name="files">Reads the earlier report.</param>
    /// <param name="paths">Resolves the path the caller spelled.</param>
    /// <param name="environment">Names the working directory a relative path resolves against.</param>
    /// <param name="declared">The declared gates, in declaration order.</param>
    /// <param name="ran">The report this run produced.</param>
    /// <param name="path">Where the earlier report was written.</param>
    internal static GateReport MergedWithExisting(
        IWritableFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IReadOnlyList<ReviewGate> declared,
        GateReport ran,
        string path)
    {
        GateReport existing;
        try
        {
            string full = paths.GetFullPath(path, environment.CurrentDirectory);
            if (!files.FileExists(full))
                return ran;

            existing = ReviewJson.ReadGates(files.ReadAllText(full));
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is System.Text.Json.JsonException or InvalidOperationException
                                       or ArgumentException or NotSupportedException)
        {
            return ran;
        }

        return ReviewGateReporting.MergedWithExisting(declared, ran, existing);
    }

    private string FullPath(string path) => GatePaths.FullPath(pathResolver, environment, path);

    private static string? BaseRef(string? baseRef) =>
        string.IsNullOrWhiteSpace(baseRef) ? null : baseRef.Trim();

    /// <summary>Reports every refusal, then ends the run before any gate process has started.</summary>
    private static ReviewCommandResult RefuseInput(
        DiagnosticReport report,
        IEnumerable<string> reasons,
        string? hint = null)
    {
        ReviewGateReporting.AddRefusals(report, reasons, hint);
        return ReviewCommandResultFactory.Reported(1, report);
    }

    private string EnclosingRepositoryRoot(string path) =>
        GatePaths.EnclosingRepositoryRoot(files, pathResolver, environment, path);

    /// <summary>Runs the repository's declared gate suite against the whole change.</summary>
    /// <param name="trees">Finds the git work tree the review belongs to.</param>
    /// <param name="cycles">Reads and writes the recorded review cycle folders.</param>
    /// <param name="git">Lists the changed files and names the commits the change is measured from.</param>
    /// <param name="evidenceWriter">Writes the diff and the gate results into the cycle folder.</param>
    /// <param name="notSeatedStore">Keeps the gate-proven lenses a cycle did not seat.</param>
    /// <param name="gateRunner">Runs the gates.</param>
    /// <param name="duplicateDetection">Reports duplicated bodies, which decides the gate-proven seats.</param>
    /// <param name="graphs">Reads the CodeGraph index the duplicate report is computed over.</param>
    /// <param name="worktreeIndex">Makes sure the tree has a CodeGraph index before any gate starts.</param>
    /// <param name="files">Reads and writes the report, the logs and the cycle folder.</param>
    /// <param name="environment">Names the temporary directory the index lock lives in, and the working directory a relative path resolves against.</param>
    /// <param name="pathResolver">Resolves the paths the caller spelled, relative ones against the working directory.</param>
    /// <param name="selector">Finds the review folder and the prior cycle's findings.</param>
    /// <param name="configs">Loads the host's configuration.</param>
    /// <param name="taskGates">Checks and expands one agent's task gates.</param>
    private sealed class RepositoryGateRun(
        IReviewWorkTreeService trees,
        IReviewCycleStore cycles,
        IGitChangeReader git,
        IReviewEvidenceService evidenceWriter,
        INotSeatedStore notSeatedStore,
        IGateRunService gateRunner,
        IDuplicateDetectionService duplicateDetection,
        ICodeGraphPortsFactory graphs,
        IWorktreeIndexGuard worktreeIndex,
        IWritableFileSystem files,
        IProcessEnvironment environment,
        IPathResolver pathResolver,
        IReviewCycleSelector selector,
        IConfigLoadService configs,
        ITaskGateSelectionService taskGates)
    {
        /// <summary>What the gates are run against, resolved before any process starts.</summary>
        /// <param name="Root">The absolute path the run was pointed at.</param>
        /// <param name="Config">The loaded host configuration.</param>
        /// <param name="Declared">The declared gates the run selected.</param>
        /// <param name="Cycle">The cycle the run is in, or null.</param>
        private sealed record Inputs(
            string Root,
            KyberWeaveConfig Config,
            IReadOnlyList<ReviewGate> Declared,
            CouncilCycle? Cycle);

        /// <summary>The gates this run will start, and the changed set selected against.</summary>
        /// <param name="Inputs">The resolved inputs.</param>
        /// <param name="Changed">The changed files the gates are scoped by, or null when unknown.</param>
        /// <param name="Admitted">The gates the full-suite admission rule lets run.</param>
        private sealed record Execution(
            Inputs Inputs,
            IReadOnlyList<string>? Changed,
            IReadOnlyList<ReviewGate> Admitted);

        /// <summary>The gate report as it ran, and as it was written after carrying or merging.</summary>
        /// <param name="Ran">The gates this run produced.</param>
        /// <param name="Written">The report written to the file, with carried or kept gates.</param>
        private sealed record Executed(GateReport Ran, GateReport Written);

        public ReviewCommandResult Run(
            ReviewGatesRequest request, IProcessExecutor indexExecutor, TextWriter output)
        {
            DiagnosticReport report = new();
            string root = GatePaths.FullPath(pathResolver, environment, request.Path);

            Inputs? inputs = Prepare(root, request, report, output, out ReviewCommandResult? failure);
            if (inputs is null)
                return failure ?? ReviewCommandResultFactory.Reported(1, report);

            Execution? execution = Plan(inputs, request, indexExecutor, report, out failure);
            if (execution is null)
                return failure ?? ReviewCommandResultFactory.Reported(1, report);

            Executed executed = Execute(execution, request, report);
            return Report(inputs, executed, request, output, report);
        }

        /// <summary>
        /// Checks the inputs, selects the gates and finds the cycle before any gate starts. Returns null with the
        /// refusal in <paramref name="failure"/> when the run cannot continue.
        /// </summary>
        private Inputs? Prepare(
            string root,
            ReviewGatesRequest request,
            DiagnosticReport report,
            TextWriter output,
            out ReviewCommandResult? failure)
        {
            failure = null;
            if (!configs.TryLoad(root, request.Config, report, out KyberWeaveConfig config, out _))
            {
                failure = ReviewCommandResultFactory.Reported(1, report);
                return null;
            }

            // Inputs are checked before any gate is considered, so an unsafe path or base is refused the same
            // way whether or not the host declares a gate that uses it.
            List<string> refusals = [];
            if (request.BaseRef is not null && GitChangeParsing.ValidateBase(request.BaseRef) is { } baseProblem)
                refusals.Add(baseProblem);

            foreach (string path in request.Paths)
                if (taskGates.ValidatePath(root, path) is { } pathProblem)
                    refusals.Add(pathProblem);

            if (refusals.Count > 0)
            {
                failure = ReviewGatesService.RefuseInput(report, refusals);
                return null;
            }

            if (config.Review.Gates.Count == 0)
            {
                // Not an error, and deliberately not silent. A review that runs no gates is a
                // review with no evidence, and the reviewer needs to know that is the situation
                // rather than reading an empty gate report as "everything passed".
                report.Add(new Diagnostic(
                    ReviewGateReporting.NoGatesDeclared,
                    Severity.Warning,
                    "No gates are declared, so this review has no executed evidence behind it.",
                    "review.gates",
                    Hint: "Declare the repository's build, test, and analysis commands under " +
                          "review.gates in kyber-weave.yml."));
                failure = ReviewCommandResultFactory.Reported(0, report);
                return null;
            }

            // --gate narrows the run to the gates named, so a reviewer re-checks one gate without paying for the whole
            // suite again. Selection happens before anything else reads the gate list, so include matching, the
            // full-suite admission rule and the report all see the same set.
            IReadOnlyList<ReviewGate> declared = config.Review.Gates;
            if (request.Gates.Count > 0)
            {
                List<string> gateRefusals = [];
                declared = ReviewGateReporting.SelectNamedGates(config.Review.Gates, request.Gates, gateRefusals);
                if (gateRefusals.Count > 0)
                {
                    failure = ReviewGatesService.RefuseInput(
                        report,
                        gateRefusals,
                        "Refused before any gate started. --gate takes the id of a gate declared under review.gates.");
                    return null;
                }
            }

            // A council run in a cycle after the first is a verifier cycle. The recorded review folders say so, so the run
            // itself decides which gates still need to run and carries the rest, instead of relying on the reviewer to ask.
            // The header is printed before any gate starts, so the reviewer can re-check findings while the gates run.
            CouncilCycle? cycle = request.Council && !request.All && request.Gates.Count == 0
                ? DetectCycle(root, config, request, report)
                : null;
            if (cycle is not null)
            {
                if (cycle.Verifier)
                    declared = [.. declared.Where(gate => cycle.Rerun.Contains(gate.Id))];

                if (request.HumanOutput)
                    WriteCycleHeader(cycle, output);

                RecordCycleIdentity(cycle, report);
                report.AddMetric("cycle", cycle.Plan.Number);
                report.AddMetric("mode", cycle.Mode);
            }

            return new Inputs(root, config, declared, cycle);
        }

        /// <summary>
        /// Resolves the changed set the include patterns are matched against, admits the full-suite gates, refuses a run
        /// that cannot start and syncs the index. Returns null with the refusal in <paramref name="failure"/>.
        /// </summary>
        private Execution? Plan(
            Inputs inputs,
            ReviewGatesRequest request,
            IProcessExecutor indexExecutor,
            DiagnosticReport report,
            out ReviewCommandResult? failure)
        {
            failure = null;
            string root = inputs.Root;
            IReadOnlyList<string>? changed = null;
            string? unknownReason = null;

            // The changed set is only needed when a gate is scoped by include; it is never computed for gates alone.
            if (!request.All && ReviewGateReporting.NeedsChangedSet(inputs.Declared))
            {
                (changed, unknownReason) = ResolveCouncilChangedSet(root, request);
                if (changed is null)
                {
                    // Fail closed: every gate runs, and the report says why none was skipped for include.
                    report.Add(new Diagnostic(
                        ReviewGateReporting.ChangedSetUnknown,
                        Severity.Info,
                        $"changed set unknown ({unknownReason}); gates with include ran unscoped.",
                        "review.gates",
                        Hint: "Fetch the full history, or name a base that exists with --base."));
                }
                else
                {
                    List<string> changedRefusals = [];
                    foreach (string path in changed)
                        if (taskGates.ValidatePath(root, path) is { } changedProblem)
                            changedRefusals.Add(changedProblem);

                    if (changedRefusals.Count > 0)
                    {
                        failure = ReviewGatesService.RefuseInput(report, changedRefusals);
                        return null;
                    }
                }
            }

            CouncilGatePlan plan = CouncilGateSelection.Plan(inputs.Declared, changed, ReviewGateReporting.CurrentOs(environment));
            ReviewGateReporting.AddSkips(report, plan.Skipped);

            // Full-suite gates run only for the council or CI, or for a person at a terminal. A refusal is a blocking
            // error that never starts its gate, so it is reported as not started, never as a gate that ran and failed.
            FullSuiteSelection fullSuite = FullSuiteGate.Select(plan.Run, request.Council, request.Interactive);
            foreach (FullSuiteRefusal refusal in fullSuite.Refused)
                report.Add(new Diagnostic(
                    ReviewGateReporting.GateNotStarted,
                    Severity.Error,
                    $"{refusal.GateId}: refused. {refusal.Message}",
                    refusal.GateId));

            // --all asks for the whole declared suite at once, so once one of its gates is refused the suite cannot be
            // delivered and no gate starts: running the rest would spend the run on a review the caller was just told
            // it may not have, and the report would read as a review that ran. A scoped run is the opposite case — the
            // pre-push self-review reads the other gates as its evidence — so it keeps running them.
            if (request.All && fullSuite.Refused.Count > 0)
            {
                failure = RefuseWithEmptyReport(request, report);
                return null;
            }

            // A council or --all run is the whole review: a gate that cannot start for a missing path leaves a report the
            // caller would have to read as incomplete, after paying for the index sync and every other gate. The runner
            // itself says which required paths are missing (same applies-when, same path resolution), so the refusal
            // lists exactly what the run would have reported as not started. A scoped run keeps going: the pre-push
            // self-review reads the other gates as its evidence, and the missing one is reported not started as before.
            if (request.Council || request.All)
            {
                IReadOnlyList<MissingGateRequirement> missing = gateRunner.MissingRequirements(
                    new ReviewConfig { Gates = fullSuite.Admitted, Parallelism = inputs.Config.Review.Parallelism },
                    root,
                    changed);
                if (missing.Count > 0)
                {
                    foreach (MissingGateRequirement requirement in missing)
                        report.Add(new Diagnostic(
                            ReviewGateReporting.GateNotStarted,
                            Severity.Error,
                            $"{requirement.GateId}: required path '{requirement.Path}' is missing, so the gate cannot start. {requirement.Fix}, then run the review again.",
                            requirement.GateId));

                    // Only the empty report is written here: no evidence and no finished-cycle record. The cycle folder and
                    // its identity.json were already written when the cycle was detected above; that record names this
                    // change's branch, base and head, so a rerun with the same --out reads it as the same cycle, in the
                    // same mode.
                    failure = RefuseWithEmptyReport(request, report);
                    return null;
                }
            }

            // Before any gate process starts, so a review in a fresh worktree gets an index instead of silently having
            // none. A repository without governed docs has no index to keep and is not checked.
            // The index belongs to the tree, so a run pointed at a subdirectory still checks the tree's root.
            string indexRoot = GatePaths.EnclosingRepositoryRoot(files, pathResolver, environment, root);
            WorktreeIndexOutcome index = worktreeIndex.Ensure(
                indexRoot,
                DoctorOptions.Defaults,
                indexExecutor,
                environment.TempDirectory);
            if (index.Status is not WorktreeIndexStatus.NotGoverned)
                ReviewGateReporting.AddIndexDiagnostic(report, indexRoot, index);

            return new Execution(inputs, changed, fullSuite.Admitted);
        }

        /// <summary>Runs the admitted gates, carries a verifier cycle's gates and merges an earlier report.</summary>
        private Executed Execute(
            Execution execution, ReviewGatesRequest request, DiagnosticReport report)
        {
            string root = execution.Inputs.Root;
            ReviewConfig config = new()
            {
                Gates = execution.Admitted,
                Parallelism = execution.Inputs.Config.Review.Parallelism,
            };

            // The run deadline is opt-in with --deadline, so CI, task gates and pre-push keep their behaviour: there, a
            // blocking gate stopped by its own timeout is a failure. --deadline is refused with --gate before this.
            TimeSpan? deadline = request.Deadline
                ? TimeSpan.FromSeconds(execution.Inputs.Config.Review.CouncilDeadlineSeconds)
                : null;
            GateReport gates = gateRunner.Run(
                config,
                root,
                request.StopOnFailure,
                execution.Changed,
                ReviewGatesService.BaseRef(request.BaseRef),
                deadline);

            foreach (GateResult gate in gates.Gates)
                ReviewGateReporting.AddGateDiagnostic(report, gate, request.Deadline);

            GateReport written = gates;
            CouncilCycle? cycle = execution.Inputs.Cycle;
            if (cycle is { Verifier: true })
            {
                written = ReviewGateReporting.WithCarriedGates(execution.Inputs.Config.Review.Gates, gates, cycle.Plan, report);
                report.AddMetric("carried", written.Gates.Count - gates.Gates.Count);
            }

            // Gates named one run at a time into the same report add to it. Replacing the file would lose the earlier
            // runs' results, so a reviewer could never finish a verifier cycle gate by gate.
            if (request.Gates.Count > 0 && request.Out is not null)
            {
                written = ReviewGatesService.MergedWithExisting(
                    files, pathResolver, environment, execution.Inputs.Config.Review.Gates, written, request.Out);
                report.AddMetric("kept", written.Gates.Count - gates.Gates.Count);
            }

            report.AddMetric("gates", gates.Gates.Count);
            report.AddMetric("failed", gates.Gates.Count(g => !g.Passed && g.NotApplicableReason is null
                                                             && !(request.Deadline && VerdictEngine.IsStoppedForTime(g))));
            report.AddMetric("not-applicable", gates.Gates.Count(g => g.NotApplicableReason is not null));
            if (request.Deadline)
                report.AddMetric("not-run", gates.Gates.Count(VerdictEngine.IsStoppedForTime));

            return new Executed(gates, written);
        }

        /// <summary>Writes the report, the evidence and the gate-proven skips, and builds the reviewer-facing epilogue.</summary>
        private ReviewCommandResult Report(
            Inputs inputs,
            Executed executed,
            ReviewGatesRequest request,
            TextWriter output,
            DiagnosticReport report)
        {
            GateReport gates = executed.Ran;
            GateReport written = executed.Written;
            CouncilCycle? cycle = inputs.Cycle;

            ReviewReportWriter.TryWrite(
                files, pathResolver, environment, request.Out, ReviewJson.Write(written), ReviewDiagnosticCodes.ReportWriteFailed, report);
            string? evidence = cycle is { WritesIdentity: true } && request.Out is not null
                ? WriteEvidence(cycle, inputs.Root, request, written, inputs.Config, report)
                : null;
            IReadOnlyDictionary<string, string>? gateProven = evidence is not null
                ? GateProvenSkips(cycle!, inputs.Root, inputs.Config, request, written, report)
                : null;

            List<string> epilogue = [];
            if (cycle is not null && request.HumanOutput)
                AddEpilogue(epilogue, cycle, gates, written, evidence, gateProven, request);

            // Blocking failures are already Error diagnostics; a failed --out write is too.
            // Non-blocking gate failures stay Warning and do not fail the process. Gates that
            // did not apply are Info and never fail it either.
            return ReviewCommandResultFactory.Reported(report.HasErrors ? 1 : 0, report, epilogue);
        }

        /// <summary>The plain lines a person reads after the run: the evidence, the seats still to send and the next command.</summary>
        private void AddEpilogue(
            List<string> epilogue,
            CouncilCycle cycle,
            GateReport gates,
            GateReport written,
            string? evidence,
            IReadOnlyDictionary<string, string>? gateProven,
            ReviewGatesRequest request)
        {
            if (evidence is not null)
                epilogue.Add($"evidence: {evidence}");
            if (gateProven is not null)
            {
                foreach ((string lens, string reason) in gateProven.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    epilogue.Add($"not-seated: --not-seated \"{lens}={reason}\"");
                // Only lenses review seats wrote a packet for: a verifier cycle seats fewer, and none is widened here.
                string packets = Path.Combine(cycle.Folder!, "packets");
                string[] send = [.. CouncilSeating.AfterGates.Where(lens => !gateProven.ContainsKey(lens)
                    && (!files.DirectoryExists(packets) || files.FileExists(Path.Combine(packets, lens + ".md"))))];
                epilogue.Add($"after-gates: send {(send.Length == 0 ? "none" : string.Join(", ", send))}");
                epilogue.Add($"next: {VerdictCommandLine(null)}");
            }

            epilogue.Add(
                $"cycle: {cycle.Plan.Number} mode: {cycle.Mode} ran: {gates.Gates.Count} carried: {written.Gates.Count - gates.Gates.Count} " +
                $"failed: {gates.Gates.Count(g => !g.Passed && g.NotApplicableReason is null && !(request.Deadline && VerdictEngine.IsStoppedForTime(g)))}");
        }

        /// <summary>
        /// Ends a run that cannot deliver the whole suite: an empty report goes to <c>--out</c>, so no earlier report is
        /// read as this run's, and the exit code is 1. Nothing else is written.
        /// </summary>
        private ReviewCommandResult RefuseWithEmptyReport(ReviewGatesRequest request, DiagnosticReport report)
        {
            ReviewReportWriter.TryWrite(
                files,
                pathResolver,
                environment,
                request.Out,
                ReviewJson.Write(new GateReport(GateReport.CurrentSchema, [])),
                ReviewDiagnosticCodes.ReportWriteFailed,
                report);
            return ReviewCommandResultFactory.Reported(1, report);
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
        ///     (<c>ReviewCyclePlanning.Belonging</c>); one that is finished, this one included, or that belongs to another
        ///     change is never counted. When nothing says this is a later cycle, the run is a full one, which is the safe answer.
        /// </summary>
        private CouncilCycle? DetectCycle(
            string root, KyberWeaveConfig config, ReviewGatesRequest request, DiagnosticReport report)
        {
            string tree = trees.Containing(root);
            List<string> notes = [];
            if (trees.LiveRunNote(tree, trees.HomeDirectory()) is { } liveRun)
                notes.Add(liveRun);

            string reviewFolder;
            int? thisCycle = null;
            string? folder = null;
            string scratchpad = ReviewWorkTree.Scratchpad(tree);
            ReviewTaskChoice choice = selector.ChooseTask(tree, scratchpad, null);
            if (request.Out is not null
                && ReviewCycleView.TryFromOutPath(pathResolver, environment, request.Out, out string fromOut, out int number))
            {
                reviewFolder = fromOut;
                thisCycle = number;
                folder = Path.GetDirectoryName(GatePaths.FullPath(pathResolver, environment, request.Out));
                if (!ReviewCycleView.IsUnder(pathResolver, environment, tree, fromOut))
                    notes.Add($"worktree: --out is outside {tree}, so this review writes to {folder}; use {ReviewWorkTree.Scratchpad(tree)} for this worktree's review folders.");
            }
            else if (choice.Chosen)
            {
                reviewFolder = Path.Combine(scratchpad, choice.Task, ScratchLayout.ReviewFolderName);
            }
            else
            {
                return null;
            }

            PriorCycles prior = trees.Prior(
                tree,
                request.BaseRef,
                [.. cycles.ReadFinished(reviewFolder).Where(record => thisCycle is null || record.Number < thisCycle)]);
            IReadOnlyList<ReviewCycleRecord> finished = prior.Valid;

            ReviewCyclePlan plan = ReviewCyclePlanning.Plan(
                finished, config.Review.Gates, ReviewGateReporting.CurrentOs(environment), commit => selector.ChangedSince(tree, commit));
            folder ??= Path.Combine(reviewFolder, ScratchLayout.CycleFolder(plan.Number));
            IReadOnlyList<ReviewFinding> recheck = [];
            if (plan.Prior is { } priorCycle)
            {
                (_, IReadOnlyList<ReviewFinding> found) = selector.ReadRecheck(
                    priorCycle, config.Review.Policy, report, ReviewDiagnosticCodes.UnreadableInput, Severity.Warning, "review gates");
                recheck = found;
            }

            return new CouncilCycle(
                plan,
                recheck,
                [.. plan.Gates.Where(gate => gate.Action == GateCycleAction.Rerun).Select(gate => gate.GateId)],
                prior.Ignored,
                tree,
                folder,
                notes,
                plan.IsVerifier && !request.Full,
                thisCycle is not null,
                request.BaseRef);
        }

        /// <summary>
        ///     Writes the diff, the changed files and the gate results into the cycle folder, so the seats and the lead
        ///     reviewer start from them instead of re-running a gate or rediscovering the change.
        /// </summary>
        /// <returns>The evidence file's path, or null when it could not be written.</returns>
        private string? WriteEvidence(
            CouncilCycle cycle,
            string root,
            ReviewGatesRequest request,
            GateReport written,
            KyberWeaveConfig config,
            DiagnosticReport report)
        {
            try
            {
                string path = evidenceWriter.Write(
                    cycle.Folder!,
                    root,
                    request.BaseRef ?? ReviewGateReporting.CouncilDefaultBase,
                    written,
                    request.Out!,
                    cycle.Verifier ? cycle.Plan.Prior?.Head : null,
                    config.Review.Coverage);
                report.AddMetric("evidence", path);
                return path;
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                report.Add(new Diagnostic(
                    ReviewDiagnosticCodes.ReportWriteFailed,
                    Severity.Warning,
                    $"Could not write the review evidence to '{cycle.Folder}': {ex.Message}",
                    "review gates",
                    Hint: "The seats then have no diff or gate summary to start from; fix the folder and run the gates again."));
                return null;
            }
        }

        /// <summary>
        ///     Writes the duplicates report into the cycle folder and decides the two gate-proven skips from it and the gate
        ///     report, so the reviewer seats <c>static-analysis-triage</c> and <c>duplicate-implementation</c> only when a gate
        ///     leaves them something to say.
        /// </summary>
        private IReadOnlyDictionary<string, string> GateProvenSkips(
            CouncilCycle cycle,
            string root,
            KyberWeaveConfig config,
            ReviewGatesRequest request,
            GateReport written,
            DiagnosticReport report)
        {
            CodeGraphPorts graph = graphs.ForRepository(root);
            DuplicateReport duplicates = duplicateDetection.Analyze(root, graph.Resolver, graph.Enumerator, config.Review.Duplicates);
            ReviewReportWriter.TryWrite(
                files,
                pathResolver,
                environment,
                Path.Combine(cycle.Folder!, "duplicates.json"),
                ReviewJson.Write(duplicates),
                ReviewDiagnosticCodes.ReportWriteFailed,
                report);
            (IReadOnlyList<string>? changed, _) = ResolveCouncilChangedSet(root, request);
            IReadOnlyDictionary<string, string> skips = CouncilSeating.GateProvenSkips(written, duplicates, changed);
            try
            {
                notSeatedStore.Replace(cycle.Folder!, CouncilSeating.GateProvenLenses, skips);
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                // The printed not-seated lines still carry the decisions; the verdict asks for any it cannot read.
            }

            return skips;
        }

        /// <summary>Records the branch, base and head this cycle reviews, so a later cycle can tell the cycle belongs to its change.</summary>
        private void RecordCycleIdentity(CouncilCycle cycle, DiagnosticReport report)
        {
            if (!cycle.WritesIdentity || cycle.Folder is null || trees.IdentityOf(cycle.Tree, cycle.BaseRef) is not { } identity)
                return;

            try
            {
                cycles.WriteIdentity(cycle.Folder, identity);
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                report.Add(new Diagnostic(
                    ReviewDiagnosticCodes.ReportWriteFailed,
                    Severity.Warning,
                    $"Could not record the change in '{cycle.Folder}': {ex.Message}",
                    "review gates",
                    Hint: "Without it the next cycle cannot tell this folder belongs to the change, and runs in full."));
            }
        }

        /// <summary>The cycle, mode, folder, ignored cycles, findings to re-check and gate plan, at the top of the output a reviewer reads first.</summary>
        private static void WriteCycleHeader(CouncilCycle cycle, TextWriter output)
        {
            output.WriteLine($"cycle: {cycle.Plan.Number} mode: {cycle.Mode}");
            output.WriteLine($"next: read {ReviewGateReporting.CycleReference(cycle.Verifier)} in the code-review skill");
            if (cycle.Folder is not null)
                output.WriteLine($"folder: {cycle.Folder}");

            foreach (string note in cycle.Notes)
                output.WriteLine(note);

            foreach (string line in ReviewCycleView.IgnoredLines(cycle.Tree, cycle.Ignored))
                output.WriteLine(line);

            if (cycle.Verifier)
            {
                foreach (string line in ReviewCycleView.VerifierLines(cycle.Plan, cycle.Recheck))
                    output.WriteLine(line);
            }

            output.Flush();
        }

        /// <summary>
        ///     The paths the council's include patterns are matched against: the <c>--paths</c> given, else the diff from
        ///     the merge base of <c>--base</c> (default <c>origin/HEAD</c>) to the working tree. When git cannot list the
        ///     changes, the set is null and the reason is returned, so the caller can run every gate and say why.
        /// </summary>
        private (IReadOnlyList<string>? Changed, string? UnknownReason) ResolveCouncilChangedSet(
            string root, ReviewGatesRequest request)
        {
            if (request.Paths.Count > 0)
                return ([.. request.Paths.Distinct(StringComparer.Ordinal)], null);

            try
            {
                IReadOnlyList<string> changed = git
                    .ResolveSinceMergeBase(root, request.BaseRef ?? ReviewGateReporting.CouncilDefaultBase)
                    .Select(change => change.Path)
                    .ToArray();
                return (changed, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
            {
                return (null, ex.Message.Trim());
            }
        }

        /// <summary>The verdict command line the reviewer copies; it needs no other argument, since the cycle folder records the rest.</summary>
        internal static string VerdictCommandLine(string? task) =>
            task is null ? "kyber-weave review verdict ." : $"kyber-weave review verdict . --task {task}";
    }


    /// <summary>Where a path and the enclosing repository root are resolved, shared by both gate runs.</summary>
    private static class GatePaths
    {
        internal static string FullPath(IPathResolver paths, IProcessEnvironment environment, string path) =>
            paths.GetFullPath(path, environment.CurrentDirectory);

        internal static string EnclosingRepositoryRoot(
            IWritableFileSystem files, IPathResolver paths, IProcessEnvironment environment, string path)
        {
            string full = FullPath(paths, environment, path);
            for (string? dir = full; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                string gitPath = Path.Combine(dir, ".git");
                if (files.DirectoryExists(gitPath)
                    || files.FileExists(gitPath)
                    || files.DirectoryExists(Path.Combine(dir, UserStatePaths.DirectoryName)))
                    return dir;
            }

            return full;
        }
    }

    /// <summary>Runs one developer agent's task gates against the change that agent made.</summary>
    /// <param name="configs">Loads the host's configuration.</param>
    /// <param name="git">Lists the changed files when the task's own files were not passed.</param>
    /// <param name="gateRunner">Runs the task gates.</param>
    /// <param name="projectFiles">Names the project file a changed file belongs to.</param>
    /// <param name="files">Reads and writes the report, the logs and the snapshot.</param>
    /// <param name="environment">Names the working directory a relative path resolves against.</param>
    /// <param name="pathResolver">Resolves the paths the caller spelled.</param>
    /// <param name="taskGates">Checks and expands one agent's task gates.</param>
    /// <param name="targets">Finds the task a <c>--task</c> value names.</param>
    private sealed class AgentGateRun(
        IConfigLoadService configs,
        IGitChangeReader git,
        IGateRunService gateRunner,
        IProjectFileLocator projectFiles,
        IWritableFileSystem files,
        IProcessEnvironment environment,
        IPathResolver pathResolver,
        ITaskGateSelectionService taskGates,
        IPlanTaskTargetService targets)
    {
        /// <summary>The validated request and the gates it resolved to, before any process starts.</summary>
        /// <param name="Root">The absolute path the run was pointed at.</param>
        /// <param name="Config">The loaded host configuration.</param>
        /// <param name="BaseRef">The ref the change is measured from.</param>
        /// <param name="Target">The task the <c>--task</c> value names, or null.</param>
        /// <param name="TargetMissing">Why the task was not found in a plan, or null.</param>
        /// <param name="TaskSlug">The normalised task id, or null.</param>
        /// <param name="Paths">The files to gate, repository-relative.</param>
        /// <param name="OutPath">Where the report goes, or null for the task's own report path.</param>
        /// <param name="AgentGates">The agent's resolved task gates.</param>
        /// <param name="RepoRoot">The enclosing repository root.</param>
        private sealed record AgentSetup(
            string Root,
            KyberWeaveConfig Config,
            string BaseRef,
            PlanTaskTargetMatch? Target,
            string? TargetMissing,
            string? TaskSlug,
            IReadOnlyList<string> Paths,
            string? OutPath,
            IReadOnlyList<ReviewTaskGate> AgentGates,
            string RepoRoot);

        /// <summary>The planned gates and the observation state their run reports through.</summary>
        /// <param name="Plan">The selected gates, skips and refusals.</param>
        /// <param name="RunCount">The ordinal this run gets.</param>
        /// <param name="CanonicalRoot">The canonical repository root, or null when it is already canonical.</param>
        /// <param name="CaseInsensitivePaths">Whether this volume folds path case.</param>
        /// <param name="Outputs">Each gate's output, by gate id.</param>
        /// <param name="Observer">The callbacks the runner reports through.</param>
        private sealed record AgentPlan(
            TaskGatePlan Plan,
            int RunCount,
            string? CanonicalRoot,
            bool CaseInsensitivePaths,
            Dictionary<string, string> Outputs,
            TaskGateObserver Observer);

        public ReviewCommandResult Run(ReviewGatesRequest request, TextWriter output)
        {
            DiagnosticReport report = new();
            AgentSetup? setup = Prepare(request, report, out ReviewCommandResult? failure);
            if (setup is null)
                return failure ?? ReviewCommandResultFactory.Reported(1, report);

            if (!TryResolveChanged(setup, report, out IReadOnlyList<string> changed, out failure))
                return failure ?? ReviewCommandResultFactory.Reported(1, report);

            AgentPlan? plan = PlanGates(setup, changed, request, report, out failure);
            if (plan is null)
                return failure ?? ReviewCommandResultFactory.Reported(1, report);

            return Execute(setup, plan, changed, request, output, report);
        }

        /// <summary>
        /// Checks every input and resolves the agent's gates before any process starts. Returns null with the refusal
        /// in <paramref name="failure"/> when the run cannot start.
        /// </summary>
        private AgentSetup? Prepare(ReviewGatesRequest request, DiagnosticReport report, out ReviewCommandResult? failure)
        {
            failure = null;
            string root = GatePaths.FullPath(pathResolver, environment, request.Path);

            if (!configs.TryLoad(root, request.Config, report, out KyberWeaveConfig config, out _))
            {
                failure = ReviewCommandResultFactory.Reported(1, report);
                return null;
            }

            // Every input is checked before the agent is looked up, so an unsafe path or slug is refused
            // the same way whether or not the agent declares gates.
            List<string> refusals = [];
            string baseRef = request.BaseRef ?? GitChangeParsing.DefaultBase;
            if (request.BaseRef is not null && GitChangeParsing.ValidateBase(request.BaseRef) is { } baseProblem)
                refusals.Add(baseProblem);

            string repoRoot = GatePaths.EnclosingRepositoryRoot(files, pathResolver, environment, root);

            // With --task and no --paths, the task's own files and declared test classes come from the active plan, the
            // lookup docs packet prints its test command from, so the worker passes nothing it could get wrong.
            string? targetMissing = null;
            PlanTaskTargetMatch? target = request.TaskSlug is not null && request.Paths.Count == 0
                ? targets.Find(repoRoot, config, request.TaskSlug, out targetMissing)
                : null;

            string? taskSlug = request.TaskSlug;
            if (taskSlug is not null && TaskSlug.Normalise(taskSlug) is { Length: > 0 } normalised)
                taskSlug = normalised;

            IReadOnlyList<string> paths = request.Paths;
            if (target is not null)
                paths = [.. targets.Files(root, target.Entry, target.Row)];

            // The report goes where the Arbiter reads the evidence. A --fix run that changes a task file never counts,
            // so it writes none by default; one that changes nothing counts, and writes the passing report below.
            // The task is validated before its report path is built: a punctuation-only id normalises to nothing,
            // so building the path first would throw instead of refusing the run with the bad --task message.
            string? taskProblem = taskGates.ValidateTask(taskSlug);
            if (taskProblem is not null)
                refusals.Add(taskProblem);

            string? outPath = request.Out;
            if (outPath is null && taskSlug is not null && !request.Fix && taskProblem is null)
                outPath = Path.Combine(root, request.Red ? TaskGateNextStep.FailingReportPath(taskSlug) : TaskGateNextStep.PassingReportPath(taskSlug));

            foreach (string path in paths)
                if (taskGates.ValidatePath(root, path) is { } pathProblem)
                    refusals.Add(pathProblem);

            if (refusals.Count > 0)
            {
                failure = ReviewGatesService.RefuseInput(report, refusals);
                return null;
            }

            List<string> unreadableStandards = [];
            string? parentTechnology = TaskGateResolver.ParentTechnologyOrNone(
                files, repoRoot, config, target?.Entry.Technology, unreadableStandards);
            if (unreadableStandards.Count > 0)
            {
                // The task's gates may be keyed on the parent technology, so a standard this process may not read
                // makes the run's evidence unreliable. Refuse before any gate starts and name the file.
                failure = ReviewGatesService.RefuseInput(
                    report,
                    unreadableStandards,
                    "Make the standard readable, then run the command again.");
                return null;
            }

            TaskGateResolution resolved = TaskGateResolver.Resolve(
                config.Review,
                request.Agent!,
                target?.Entry.Technology,
                parentTechnology);

            IReadOnlyList<ReviewTaskGate> agentGates = resolved.Gates;
            if (agentGates.Count == 0)
            {
                failure = NoGates(request, root, taskSlug, paths, outPath, resolved, report);
                return null;
            }

            return new AgentSetup(root, config, baseRef, target, targetMissing, taskSlug, paths, outPath, agentGates, repoRoot);
        }

        /// <summary>
        /// Reports the run whose agent resolves no gate, writes an empty report, and returns the result: an error when the
        /// task names a technology no gate covers, a warning when it names none.
        /// </summary>
        private ReviewCommandResult NoGates(
            ReviewGatesRequest request,
            string root,
            string? taskSlug,
            IReadOnlyList<string> paths,
            string? outPath,
            TaskGateResolution resolved,
            DiagnosticReport report)
        {
            // No route names a gate: not the task's technology, not its parent, not the agent. A task that names a
            // technology is an error, because a developer change with no gate has no evidence behind it; a task that
            // names none keeps the warning this command gave before technology-keyed gates existed.
            report.Add(new Diagnostic(
                ReviewGateReporting.NoGatesForAgent,
                resolved.IsRefused ? Severity.Error : Severity.Warning,
                resolved.Refusal
                ?? $"No task gates resolve for agent '{request.Agent}': the task names no technology this host declares " +
                "gates for, and review.task-gates has no entry for the agent, so its change has no executed evidence behind it.",
                "review",
                Hint: "Declare gates under review.technology-gates for the task's technology, or its parent, or under " +
                      "review.task-gates for the agent."));
            ReviewReportWriter.TryWrite(
                files,
                pathResolver,
                environment,
                outPath,
                ReviewJson.Write(new GateReport(GateReport.CurrentSchema, [])),
                ReviewDiagnosticCodes.ReportWriteFailed,
                report);
            return ReviewCommandResultFactory.Reported(
                report.HasErrors ? 1 : 0,
                report,
                request.HumanOutput && !resolved.IsRefused
                    ? TaskGateNextStep.NoneDeclared(request.Agent!, paths, outPath, PeekRunCount(root, taskSlug))
                    : []);
        }

        /// <summary>
        /// The files this run gates: those the request named, else the ones git lists from the base. A git failure or an
        /// unsafe path is reported as a refusal and no gate starts.
        /// </summary>
        private bool TryResolveChanged(
            AgentSetup setup,
            DiagnosticReport report,
            out IReadOnlyList<string> changed,
            out ReviewCommandResult? failure)
        {
            failure = null;
            if (setup.Paths.Count > 0)
            {
                changed = setup.Paths.Distinct(StringComparer.Ordinal).ToArray();
                return true;
            }

            IReadOnlyList<string> resolved;
            try
            {
                resolved = git.Resolve(setup.Root, setup.BaseRef);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
            {
                changed = [];
                failure = ReviewGatesService.RefuseInput(report, [$"Could not list the changed files with git: {ex.Message}"
                    + (setup.TargetMissing is null ? string.Empty : $" The task was not found in a plan either ({setup.TargetMissing}); pass --paths.")]);
                return false;
            }

            // Paths git reports are checked with the same rules as --paths, because they reach argv the same way.
            List<string> gitRefusals = [];
            foreach (string path in resolved)
                if (taskGates.ValidatePath(setup.Root, path) is { } gitProblem)
                    gitRefusals.Add(gitProblem);

            if (gitRefusals.Count > 0)
            {
                changed = [];
                failure = ReviewGatesService.RefuseInput(report, gitRefusals);
                return false;
            }

            changed = resolved;
            return true;
        }

        /// <summary>Selects and expands the gates and records the skips, before any process starts.</summary>
        private AgentPlan? PlanGates(
            AgentSetup setup,
            IReadOnlyList<string> changed,
            ReviewGatesRequest request,
            DiagnosticReport report,
            out ReviewCommandResult? failure)
        {
            failure = null;
            TaskGatePlan plan = taskGates.Plan(
                setup.AgentGates, changed, setup.TaskSlug, ReviewGateReporting.CurrentOs(environment), setup.RepoRoot,
                setup.Config.Review.TestMap,
                setup.Target is null ? null : targets.DeclaredClasses(setup.Target.Row));
            if (plan.Refusals.Count > 0)
            {
                failure = ReviewGatesService.RefuseInput(report, plan.Refusals);
                return null;
            }

            int runCount = NextRunCount(setup.Root, setup.TaskSlug);
            ReviewGateReporting.AddSkips(report, plan.Skipped);

            string? canonicalRoot = CanonicalRootOrNull(setup.Root);
            bool caseInsensitivePaths = environment.IsFileSystemCaseInsensitive;

            // The classifiers read what each verify command printed, so a build that fails on missing symbols is told
            // apart from one that fails on anything else.
            Dictionary<string, string> outputs = new(StringComparer.Ordinal);
            IReadOnlyList<string>? brokenBuildSymbols = null;
            TaskGateObserver observer = new()
            {
                Output = (id, gateOutput) => outputs[id] = gateOutput,
                After = (gate, result) =>
                {
                    // A build accepted as RED on missing symbols leaves nothing for a later test gate to run against.
                    if (request.Red && !result.Passed && brokenBuildSymbols is null && outputs.TryGetValue(gate.Id, out string? gateOutput))
                        brokenBuildSymbols = RedClassifier.AcceptedMissingSymbols(
                            new RedGateObservation(
                                gate.Id, string.Join(' ', gate.Run), result.ExitCode, gateOutput, gate.WorkingDirectory, setup.Root, canonicalRoot, caseInsensitivePaths),
                            changed);
                },
                SkipReason = gate => request.Red && brokenBuildSymbols is not null && RedClassifier.IsTestGate(gate.Id, string.Join(' ', gate.Run))
                    ? RedClassifier.BuildSkipReason(brokenBuildSymbols)
                    : null,
            };

            return new AgentPlan(plan, runCount, canonicalRoot, caseInsensitivePaths, outputs, observer);
        }

        /// <summary>Runs the gates, applies the RED judgement, and writes the report.</summary>
        private ReviewCommandResult Execute(
            AgentSetup setup,
            AgentPlan plan,
            IReadOnlyList<string> changed,
            ReviewGatesRequest request,
            TextWriter output,
            DiagnosticReport report)
        {
            string root = setup.Root;

            // A --fix run whose fix commands changed no task file is the verification run, so it is judged as one: the
            // content of each changed file is taken before the gates run and compared after.
            Dictionary<string, string?> before = request.Fix ? Snapshot(root, changed) : new(StringComparer.Ordinal);
            TaskGateRun run = gateRunner.RunTaskGates(
                plan.Plan.Gates, root, request.Fix, request.StopOnFailure && !request.Red, plan.Observer);
            bool fixChangedFiles = request.Fix && Snapshot(root, changed).Any(file => file.Value != before[file.Key]);

            // The counting --fix run writes the report a plain run writes, because the report reader finds the task's
            // evidence there. An explicit --out names an extra file, not a replacement, so the rule holds either way.
            string? countingReport = !fixChangedFiles && setup.TaskSlug is not null && !request.Red
                ? Path.Combine(root, TaskGateNextStep.PassingReportPath(setup.TaskSlug))
                : null;
            string? outPath = setup.OutPath ?? countingReport;

            GateReport finalReport = run.Report;
            RedOutcome? redOutcome = null;
            if (request.Red)
            {
                (finalReport, redOutcome) = ReviewGateReporting.ApplyRed(
                    run.Report, plan.Plan.Gates, plan.Outputs, changed, file => projectFiles.NearestProjectFile(setup.RepoRoot, file), report, root, plan.CanonicalRoot, plan.CaseInsensitivePaths);
            }

            foreach (GateResult gate in finalReport.Gates)
                ReviewGateReporting.AddGateDiagnostic(report, gate, timeIsNotRun: false);

            if (!request.Red)
            {
                ReviewGateReporting.AddSiblingNotes(report, plan.Plan.Gates, run.Report, plan.Outputs, changed, root, plan.CanonicalRoot, plan.CaseInsensitivePaths);
            }

            return Report(setup, plan, changed, request, output, report, run, finalReport, redOutcome, fixChangedFiles, countingReport, outPath);
        }

        /// <summary>Writes the report and build the reviewer-facing epilogue.</summary>
        private ReviewCommandResult Report(
            AgentSetup setup,
            AgentPlan plan,
            IReadOnlyList<string> changed,
            ReviewGatesRequest request,
            TextWriter output,
            DiagnosticReport report,
            TaskGateRun run,
            GateReport finalReport,
            RedOutcome? redOutcome,
            bool fixChangedFiles,
            string? countingReport,
            string? outPath)
        {
            string root = setup.Root;
            foreach (GateResult fix in run.FixFailures)
                report.Add(new Diagnostic(
                    ReviewGateReporting.FixFailed,
                    Severity.Warning,
                    $"{fix.Id}: fix did not succeed ({fix.Summary}). The verify step still decides.",
                    fix.Id));

            report.AddMetric("gates", finalReport.Gates.Count);
            report.AddMetric("failed", finalReport.Gates.Count(g => !g.Passed && !g.NotApplicable));

            // A --fix run whose fixes changed a task file is not the verification run, so its report must not land where a
            // plain run's report is read as the task's evidence, even when --out names that path.
            string? evidenceReport = setup.TaskSlug is not null && !request.Red
                ? Path.Combine(root, TaskGateNextStep.PassingReportPath(setup.TaskSlug))
                : null;
            bool withheldEvidence = fixChangedFiles && evidenceReport is not null && SamePath(outPath, evidenceReport);

            string content = ReviewJson.Write(finalReport);
            if (!withheldEvidence)
                ReviewReportWriter.TryWrite(
                    files, pathResolver, environment, outPath, content, ReviewDiagnosticCodes.ReportWriteFailed, report);
            if (countingReport is not null && !SamePath(outPath, countingReport))
                ReviewReportWriter.TryWrite(
                    files, pathResolver, environment, countingReport, content, ReviewDiagnosticCodes.ReportWriteFailed, report);

            // The run as it happened, not as --red re-judged it: an accepted compile failure is still a failed build to read.
            IReadOnlyList<string> digest = TestDigest(root, setup.TaskSlug, request.Red, plan.Plan.Gates, run.Report, plan.Outputs);
            List<string> epilogue = [];
            if (request.HumanOutput)
                AddEpilogue(epilogue, setup, plan, changed, request, finalReport, redOutcome, fixChangedFiles, outPath, digest, evidenceReport, withheldEvidence);

            return ReviewCommandResultFactory.Reported(report.HasErrors ? 1 : 0, report, epilogue);
        }

        /// <summary>The plain lines a person reads after the run: which files were gated and what to do next.</summary>
        private void AddEpilogue(
            List<string> epilogue,
            AgentSetup setup,
            AgentPlan plan,
            IReadOnlyList<string> changed,
            ReviewGatesRequest request,
            GateReport finalReport,
            RedOutcome? redOutcome,
            bool fixChangedFiles,
            string? outPath,
            IReadOnlyList<string> digest,
            string? evidenceReport,
            bool withheldEvidence)
        {
            // Which files were gated, and from where, so a lookup that fell back to the git change is never silent.
            epilogue.Add(setup.Target is not null
                ? $"task: {setup.Target.Entry.Id} from {setup.Target.Plan} — files: {string.Join(", ", changed)}"
                : setup.TargetMissing is not null
                    ? $"task: not found in a plan ({setup.TargetMissing}), so the files git reports changed were gated; pass --paths to name them"
                    : $"files: {string.Join(", ", changed)}");
            if (withheldEvidence)
                epilogue.Add($"the fix run's report was not written to {evidenceReport} because a plain run must verify");
            epilogue.AddRange(digest);
            if (redOutcome is not null)
            {
                epilogue.AddRange(ReviewGateReporting.RedLines(redOutcome, request.Agent!, finalReport, outPath, plan.RunCount));
                epilogue.AddRange(TaskGateNextStep.AfterFailingRun(redOutcome.Passed, request.Agent!, changed, setup.TaskSlug, fromPlan: setup.Target is not null));
            }
            else
            {
                IReadOnlyList<ReviewTaskGateView> views =
                [
                    .. setup.AgentGates.Select(gate => new ReviewTaskGateView(
                        gate.Id, !gate.Run.Any(arg => arg.Contains("{changed}", StringComparison.Ordinal) || arg.Contains("{tests}", StringComparison.Ordinal)))),
                ];
                epilogue.AddRange(TaskGateNextStep.AfterRun(finalReport, views, fixChangedFiles, request.Agent!, changed, outPath, plan.RunCount));
            }

        }

        /// <summary>
        ///     The short form of each test gate and each failed gate (<see cref="TaskTestDigest" />), after writing every
        ///     gate's full output to a log beside the report, so a worker reads a few lines instead of the raw output.
        /// </summary>
        private List<string> TestDigest(
            string root, string? taskSlug, bool red, IReadOnlyList<PlannedTaskGate> planned, GateReport report, Dictionary<string, string> outputs)
        {
            List<string> lines = [];
            foreach (GateResult gate in report.Gates.Where(result => !result.NotApplicable))
            {
                PlannedTaskGate? plannedGate = planned.FirstOrDefault(candidate => string.Equals(candidate.Id, gate.Id, StringComparison.Ordinal));
                if (plannedGate is null || !outputs.TryGetValue(gate.Id, out string? output)
                    || (gate.Passed && !RedClassifier.IsTestGate(gate.Id, string.Join(' ', plannedGate.Run))))
                    continue;

                string? log = taskSlug is null ? null : $"{ScratchLayout.LogsDir(taskSlug)}/{gate.Id}{(red ? "-red" : string.Empty)}.log";
                if (log is not null)
                {
                    try
                    {
                        string full = Path.Combine(root, log);
                        files.CreateDirectory(Path.GetDirectoryName(full)!);
                        files.WriteAllText(full, output);
                    }
                    catch (Exception ex) when (FileFailure.Is(ex))
                    {
                        log = null;
                    }
                }

                lines.AddRange(TaskTestDigest.Lines(gate.Id, gate.Passed, output, log));
            }

            return lines;
        }

        private static string RunCountPath(string root, string taskSlug) =>
            Path.Combine(root, ScratchLayout.TaskGateRuns(taskSlug));

        /// <summary>
        ///     The ordinal the next task-gate run gets without advancing or writing the counter, so a no-gate run neither
        ///     consumes an ordinal nor rewinds past prior gated runs.
        /// </summary>
        private int PeekRunCount(string root, string? taskSlug)
        {
            if (taskSlug is null)
                return 1;

            string path = RunCountPath(root, taskSlug);
            int previous = 0;
            try
            {
                if (files.FileExists(path)
                    && int.TryParse(files.ReadAllText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out int read)
                    && read >= 0)
                    previous = read;
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                previous = 0;
            }

            return previous == int.MaxValue ? int.MaxValue : previous + 1;
        }

        /// <summary>
        ///     The ordinal this task-gate run gets, and the counter it leaves for the next run.
        /// </summary>
        /// <remarks>
        ///     A counter file in the task's scratch folder names how many task-gate runs the task has had. The scratch
        ///     folder is already the per-task evidence home, and one invocation cannot know the count any other way. A run
        ///     with no task id names no folder, so it is always run 1. A counter that cannot be read or written counts as
        ///     0: the run still reports its own number rather than failing on its own bookkeeping.
        /// </remarks>
        private int NextRunCount(string root, string? taskSlug)
        {
            int count = PeekRunCount(root, taskSlug);
            if (taskSlug is null)
                return count;

            string path = RunCountPath(root, taskSlug);
            try
            {
                files.CreateDirectory(Path.GetDirectoryName(path)!);
                files.WriteAllText(path, count.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                // The run still reports the number it counted; only the next run's starting point is lost.
            }

            return count;
        }

        private string? CanonicalRootOrNull(string root)
        {
            try
            {
                string canonical = pathResolver.Canonicalize(root);
                return string.Equals(canonical, root, StringComparison.Ordinal) ? null : canonical;
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                return null;
            }
        }

        /// <summary>
        ///     Whether two report paths name the same file, so a run that writes both the file <c>--out</c> names and the
        ///     task's own report writes each one once. A path that cannot be resolved is compared as spelled.
        /// </summary>
        private bool SamePath(string? left, string right)
        {
            if (left is null)
                return false;

            StringComparison comparison = environment.IsFileSystemCaseInsensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            try
            {
                return string.Equals(
                    pathResolver.GetFullPath(left, environment.CurrentDirectory),
                    pathResolver.GetFullPath(right, environment.CurrentDirectory),
                    comparison);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return string.Equals(left, right, comparison);
            }
        }

        /// <summary>
        ///     The content of every changed file, read before and after a <c>--fix</c> run to tell whether a fix command
        ///     changed one. A missing file, and one this process cannot read, is recorded as null.
        /// </summary>
        private Dictionary<string, string?> Snapshot(string root, IReadOnlyList<string> paths)
        {
            Dictionary<string, string?> snapshot = new(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                try
                {
                    string full = pathResolver.GetFullPath(path, root);
                    snapshot[path] = files.FileExists(full) ? files.ReadAllText(full) : null;
                }
                catch (Exception ex) when (FileFailure.Is(ex))
                {
                    snapshot[path] = null;
                }
            }

            return snapshot;
        }
    }


}
