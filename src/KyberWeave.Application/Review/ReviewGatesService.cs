using System.ComponentModel;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
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
    private const string UnreadableInput = "KW-REVIEW-023";

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
            ? ExecuteRepositoryGates(request, indexExecutor, output)
            : ExecuteAgentGates(request, output);
    }

    private ReviewCommandResult ExecuteRepositoryGates(
        ReviewGatesRequest request, IProcessExecutor indexExecutor, TextWriter output)
    {
        DiagnosticReport report = new();
        string root = FullPath(request.Path);

        if (!configs.TryLoad(root, request.Config, report, out KyberWeaveConfig config, out _))
            return ReviewCommandResultFactory.Reported(1, report);

        // Inputs are checked before any gate is considered, so an unsafe path or base is refused the same
        // way whether or not the host declares a gate that uses it.
        List<string> refusals = [];
        if (request.BaseRef is not null && GitChangeParsing.ValidateBase(request.BaseRef) is { } baseProblem)
            refusals.Add(baseProblem);

        foreach (string path in request.Paths)
            if (taskGates.ValidatePath(root, path) is { } pathProblem)
                refusals.Add(pathProblem);

        if (refusals.Count > 0)
            return RefuseInput(report, refusals);

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
            return ReviewCommandResultFactory.Reported(0, report);
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
                return RefuseInput(
                    report,
                    gateRefusals,
                    "Refused before any gate started. --gate takes the id of a gate declared under review.gates.");
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

        // Before any gate process starts, so a review in a fresh worktree gets an index instead of silently having
        // none. A repository without governed docs has no index to keep and is not checked.
        // The index belongs to the tree, so a run pointed at a subdirectory still checks the tree's root.
        string indexRoot = EnclosingRepositoryRoot(root);
        WorktreeIndexOutcome index = worktreeIndex.Ensure(
            indexRoot,
            DoctorOptions.Defaults,
            indexExecutor,
            environment.TempDirectory);
        if (index.Status is not WorktreeIndexStatus.NotGoverned)
            ReviewGateReporting.AddIndexDiagnostic(report, indexRoot, index);

        // The changed set is only needed when a gate is scoped by include; it is never computed for gates alone.
        IReadOnlyList<string>? changed = null;
        string? unknownReason = null;
        if (!request.All && ReviewGateReporting.NeedsChangedSet(declared))
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
                    return RefuseInput(report, changedRefusals);
            }
        }

        CouncilGatePlan plan = CouncilGateSelection.Plan(declared, changed, ReviewGateReporting.CurrentOs());
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
            ReviewReportWriter.TryWrite(
                files,
                pathResolver,
                environment,
                request.Out,
                ReviewJson.Write(new GateReport(GateReport.CurrentSchema, [])),
                ReviewGateReporting.ReportWriteFailed,
                report);
            return ReviewCommandResultFactory.Reported(1, report);
        }

        // GateRunner.Run reads only Gates from its configuration, so the selected gates travel in a
        // configuration of their own and the runner itself stays unchanged. The changed set and base
        // are still passed so applies-when is evaluated from the same facts include was selected on.
        // The run deadline is opt-in with --deadline, so CI, task gates and pre-push keep their behaviour: there, a
        // blocking gate stopped by its own timeout is a failure. --deadline is refused with --gate before this.
        TimeSpan? deadline = request.Deadline
            ? TimeSpan.FromSeconds(config.Review.CouncilDeadlineSeconds)
            : null;
        GateReport gates = gateRunner.Run(
            new ReviewConfig { Gates = fullSuite.Admitted, Parallelism = config.Review.Parallelism },
            root,
            request.StopOnFailure,
            changed,
            BaseRef(request.BaseRef),
            deadline);

        foreach (GateResult gate in gates.Gates)
            ReviewGateReporting.AddGateDiagnostic(report, gate, request.Deadline);

        GateReport written = gates;
        if (cycle is { Verifier: true })
        {
            written = ReviewGateReporting.WithCarriedGates(config.Review.Gates, gates, cycle.Plan, report);
            report.AddMetric("carried", written.Gates.Count - gates.Gates.Count);
        }

        // Gates named one run at a time into the same report add to it. Replacing the file would lose the earlier
        // runs' results, so a reviewer could never finish a verifier cycle gate by gate.
        if (request.Gates.Count > 0 && request.Out is not null)
        {
            written = MergedWithExisting(config.Review.Gates, written, request.Out);
            report.AddMetric("kept", written.Gates.Count - gates.Gates.Count);
        }

        report.AddMetric("gates", gates.Gates.Count);
        report.AddMetric("failed", gates.Gates.Count(g => !g.Passed && g.NotApplicableReason is null
                                                         && !(request.Deadline && VerdictEngine.IsStoppedForTime(g))));
        report.AddMetric("not-applicable", gates.Gates.Count(g => g.NotApplicableReason is not null));
        if (request.Deadline)
            report.AddMetric("not-run", gates.Gates.Count(VerdictEngine.IsStoppedForTime));

        ReviewReportWriter.TryWrite(
            files, pathResolver, environment, request.Out, ReviewJson.Write(written), ReviewGateReporting.ReportWriteFailed, report);
        string? evidence = cycle is { WritesIdentity: true } && request.Out is not null
            ? WriteEvidence(cycle, root, request, written, config, report)
            : null;
        IReadOnlyDictionary<string, string>? gateProven = evidence is not null
            ? GateProvenSkips(cycle!, root, config, request, written, report)
            : null;

        List<string> epilogue = [];
        if (cycle is not null && request.HumanOutput)
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

        // Blocking failures are already Error diagnostics; a failed --out write is too.
        // Non-blocking gate failures stay Warning and do not fail the process. Gates that
        // did not apply are Info and never fail it either.
        return ReviewCommandResultFactory.Reported(report.HasErrors ? 1 : 0, report, epilogue);
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
            folder = Path.GetDirectoryName(FullPath(request.Out));
            if (!ReviewCycleView.IsUnder(pathResolver, environment, tree, fromOut))
                notes.Add($"worktree: --out is outside {tree}, so this review writes to {folder}; use {ReviewWorkTree.Scratchpad(tree)} for this worktree's review folders.");
        }
        else if (choice.Chosen)
        {
            reviewFolder = Path.Combine(scratchpad, choice.Task, "review");
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
            finished, config.Review.Gates, ReviewGateReporting.CurrentOs(), commit => selector.ChangedSince(tree, commit));
        folder ??= Path.Combine(reviewFolder, $"cycle-{plan.Number}");
        IReadOnlyList<ReviewFinding> recheck = [];
        if (plan.Prior is { } priorCycle)
        {
            (_, IReadOnlyList<ReviewFinding> found) = selector.ReadRecheck(
                priorCycle, config.Review.Policy, report, UnreadableInput, Severity.Warning, "review gates");
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
                ReviewGateReporting.ReportWriteFailed,
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
            ReviewGateReporting.ReportWriteFailed,
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
                ReviewGateReporting.ReportWriteFailed,
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
    ///     The report already at <paramref name="path"/>, with each result this run produced in place of the earlier
    ///     result for the same gate and the rest kept, in declaration order. An unreadable or missing report is nothing
    ///     to keep.
    /// </summary>
    private GateReport MergedWithExisting(IReadOnlyList<ReviewGate> declared, GateReport ran, string path)
    {
        GateReport existing;
        try
        {
            if (!files.FileExists(path))
                return ran;

            existing = ReviewJson.ReadGates(files.ReadAllText(path));
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return ran;
        }

        return ReviewGateReporting.MergedWithExisting(declared, ran, existing);
    }

    private string FullPath(string path) => pathResolver.GetFullPath(path, environment.CurrentDirectory);

    private static string? BaseRef(string? baseRef) =>
        string.IsNullOrWhiteSpace(baseRef) ? null : baseRef.Trim();

    private ReviewCommandResult ExecuteAgentGates(ReviewGatesRequest request, TextWriter output)
    {
        DiagnosticReport report = new();
        string root = FullPath(request.Path);

        if (!configs.TryLoad(root, request.Config, report, out KyberWeaveConfig config, out _))
            return ReviewCommandResultFactory.Reported(1, report);

        // Every input is checked before the agent is looked up, so an unsafe path or slug is refused
        // the same way whether or not the agent declares gates.
        List<string> refusals = [];
        string baseRef = request.BaseRef ?? GitChangeParsing.DefaultBase;
        if (request.BaseRef is not null && GitChangeParsing.ValidateBase(request.BaseRef) is { } baseProblem)
            refusals.Add(baseProblem);

        // With --task and no --paths, the task's own files and declared test classes come from the active plan, the
        // lookup docs packet prints its test command from, so the worker passes nothing it could get wrong.
        string? targetMissing = null;
        PlanTaskTargetMatch? target = request.TaskSlug is not null && request.Paths.Count == 0
            ? targets.Find(EnclosingRepositoryRoot(root), config, request.TaskSlug, out targetMissing)
            : null;

        string? taskSlug = request.TaskSlug;
        if (taskSlug is not null && TaskSlug.Normalise(taskSlug) is { Length: > 0 } normalised)
            taskSlug = normalised;

        IReadOnlyList<string> paths = request.Paths;
        if (target is not null)
            paths = [.. targets.Files(root, target.Entry, target.Row)];

        // The report goes where the Arbiter reads the evidence; a --fix run never counts, so it writes none by default.
        string? outPath = request.Out;
        if (outPath is null && taskSlug is not null && !request.Fix)
            outPath = Path.Combine(root, request.Red ? TaskGateNextStep.FailingReportPath(taskSlug) : TaskGateNextStep.PassingReportPath(taskSlug));

        if (taskGates.ValidateTask(taskSlug) is { } taskProblem)
            refusals.Add(taskProblem);

        foreach (string path in paths)
            if (taskGates.ValidatePath(root, path) is { } pathProblem)
                refusals.Add(pathProblem);

        if (refusals.Count > 0)
            return RefuseInput(report, refusals);

        List<string> unreadableStandards = [];
        string? parentTechnology = TaskGateResolver.ParentTechnologyOrNone(
            EnclosingRepositoryRoot(root), config, target?.Entry.Technology, unreadableStandards);
        if (unreadableStandards.Count > 0)
        {
            // The task's gates may be keyed on the parent technology, so a standard this process may not read
            // makes the run's evidence unreliable. Refuse before any gate starts and name the file.
            return RefuseInput(
                report,
                unreadableStandards,
                "Make the standard readable, then run the command again.");
        }

        TaskGateResolution resolved = TaskGateResolver.Resolve(
            config.Review,
            request.Agent!,
            target?.Entry.Technology,
            parentTechnology);

        IReadOnlyList<ReviewTaskGate> agentGates = resolved.Gates;
        if (agentGates.Count == 0)
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
                ReviewGateReporting.ReportWriteFailed,
                report);
            return ReviewCommandResultFactory.Reported(
                report.HasErrors ? 1 : 0,
                report,
                request.HumanOutput && !resolved.IsRefused ? TaskGateNextStep.NoneDeclared(request.Agent!, paths, outPath) : []);
        }

        IReadOnlyList<string> changed;
        if (paths.Count > 0)
        {
            changed = paths.Distinct(StringComparer.Ordinal).ToArray();
        }
        else
        {
            try
            {
                changed = git.Resolve(root, baseRef);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
            {
                return RefuseInput(report, [$"Could not list the changed files with git: {ex.Message}"
                    + (targetMissing is null ? string.Empty : $" The task was not found in a plan either ({targetMissing}); pass --paths.")]);
            }

            // Paths git reports are checked with the same rules as --paths, because they reach argv the same way.
            List<string> gitRefusals = [];
            foreach (string path in changed)
                if (taskGates.ValidatePath(root, path) is { } gitProblem)
                    gitRefusals.Add(gitProblem);

            if (gitRefusals.Count > 0)
                return RefuseInput(report, gitRefusals);
        }

        string repoRoot = EnclosingRepositoryRoot(root);
        TaskGatePlan plan = taskGates.Plan(
            agentGates, changed, taskSlug, ReviewGateReporting.CurrentOs(), repoRoot, config.Review.TestMap,
            target is null ? null : targets.DeclaredClasses(target.Row));
        if (plan.Refusals.Count > 0)
            return RefuseInput(report, plan.Refusals);

        ReviewGateReporting.AddSkips(report, plan.Skipped);

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
                        new RedGateObservation(gate.Id, string.Join(' ', gate.Run), result.ExitCode, gateOutput), changed);
            },
            SkipReason = gate => request.Red && brokenBuildSymbols is not null && RedClassifier.IsTestGate(gate.Id, string.Join(' ', gate.Run))
                ? RedClassifier.BuildSkipReason(brokenBuildSymbols)
                : null,
        };
        TaskGateRun run = gateRunner.RunTaskGates(
            plan.Gates, root, request.Fix, request.StopOnFailure && !request.Red, observer);

        GateReport finalReport = run.Report;
        RedOutcome? redOutcome = null;
        if (request.Red)
        {
            (finalReport, redOutcome) = ReviewGateReporting.ApplyRed(
                run.Report, plan.Gates, outputs, changed, file => projectFiles.NearestProjectFile(repoRoot, file), report);
        }

        foreach (GateResult gate in finalReport.Gates)
            ReviewGateReporting.AddGateDiagnostic(report, gate, timeIsNotRun: false);

        if (!request.Red)
        {
            ReviewGateReporting.AddSiblingNotes(report, plan.Gates, run.Report, outputs, changed);
        }

        foreach (GateResult fix in run.FixFailures)
            report.Add(new Diagnostic(
                ReviewGateReporting.FixFailed,
                Severity.Warning,
                $"{fix.Id}: fix did not succeed ({fix.Summary}). The verify step still decides.",
                fix.Id));

        report.AddMetric("gates", finalReport.Gates.Count);
        report.AddMetric("failed", finalReport.Gates.Count(g => !g.Passed && !g.NotApplicable));

        ReviewReportWriter.TryWrite(
            files, pathResolver, environment, outPath, ReviewJson.Write(finalReport), ReviewGateReporting.ReportWriteFailed, report);

        // The run as it happened, not as --red re-judged it: an accepted compile failure is still a failed build to read.
        IReadOnlyList<string> digest = TestDigest(root, taskSlug, request.Red, plan.Gates, run.Report, outputs);
        List<string> epilogue = [];
        if (request.HumanOutput)
        {
            // Which files were gated, and from where, so a lookup that fell back to the git change is never silent.
            epilogue.Add(target is not null
                ? $"task: {target.Entry.Id} from {target.Plan} — files: {string.Join(", ", changed)}"
                : targetMissing is not null
                    ? $"task: not found in a plan ({targetMissing}), so the files git reports changed were gated; pass --paths to name them"
                    : $"files: {string.Join(", ", changed)}");
            epilogue.AddRange(digest);
            if (redOutcome is not null)
            {
                epilogue.AddRange(ReviewGateReporting.RedLines(redOutcome, request.Agent!, finalReport, outPath));
                epilogue.AddRange(TaskGateNextStep.AfterFailingRun(redOutcome.Passed, request.Agent!, changed, taskSlug, fromPlan: target is not null));
            }
            else
            {
                IReadOnlyList<ReviewTaskGateView> views =
                [
                    .. agentGates.Select(gate => new ReviewTaskGateView(
                        gate.Id, !gate.Run.Any(arg => arg.Contains("{changed}", StringComparison.Ordinal) || arg.Contains("{tests}", StringComparison.Ordinal)))),
                ];
                epilogue.AddRange(TaskGateNextStep.AfterRun(finalReport, views, request.Fix, request.Agent!, changed, outPath));
            }
        }

        return ReviewCommandResultFactory.Reported(report.HasErrors ? 1 : 0, report, epilogue);
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

            string? log = taskSlug is null ? null : $".agent_scratchpad/{taskSlug}/logs/{gate.Id}{(red ? "-red" : string.Empty)}.log";
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

    /// <summary>Reports every refusal, then ends the run before any gate process has started.</summary>
    private static ReviewCommandResult RefuseInput(
        DiagnosticReport report,
        IEnumerable<string> reasons,
        string? hint = null)
    {
        ReviewGateReporting.AddRefusals(report, reasons, hint);
        return ReviewCommandResultFactory.Reported(1, report);
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

    private string EnclosingRepositoryRoot(string path)
    {
        for (DirectoryInfo? dir = new(path); dir is not null; dir = dir.Parent)
        {
            string gitPath = Path.Combine(dir.FullName, ".git");
            if (files.DirectoryExists(gitPath) || files.FileExists(gitPath) || files.DirectoryExists(Path.Combine(dir.FullName, ".kyber-weave")))
                return dir.FullName;
        }
        return path;
    }

    /// <summary>The verdict command line the reviewer copies; it needs no other argument, since the cycle folder records the rest.</summary>
    internal static string VerdictCommandLine(string? task) =>
        task is null ? "kyber-weave review verdict ." : $"kyber-weave review verdict . --task {task}";
}
