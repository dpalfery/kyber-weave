using KyberWeave.Core;
using System.ComponentModel;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Application.Review;

/// <summary>
/// Prepares the council's seats in a few seconds, so they start while the gates run: indexes the tree, opens the cycle
/// folder, writes the diff and the change half of the evidence, writes every packet and prints each seat's dispatch
/// block, the lenses a catalogue file pattern rules out, and the gate command to run alongside.
/// </summary>
/// <param name="trees">Finds the git work tree the review belongs to.</param>
/// <param name="cycles">Reads and writes the recorded review cycle folders.</param>
/// <param name="git">Produces the change and the fix delta.</param>
/// <param name="evidenceWriter">Writes the diff and the evidence file of a cycle.</param>
/// <param name="notSeatedStore">Keeps the lenses this cycle did not seat.</param>
/// <param name="severityOverrides">Reads the host's severity overrides, which the packets cite.</param>
/// <param name="worktreeIndex">Makes sure the tree has a CodeGraph index before any seat starts.</param>
/// <param name="files">Writes the packets and probes the cycle folder.</param>
/// <param name="environment">Names the temporary directory the index lock lives in.</param>
/// <param name="selector">Finds the review folder and the prior cycle's findings.</param>
/// <param name="configs">Loads the host's configuration.</param>
/// <param name="paths">Resolves the path the command was run against.</param>
/// <param name="decisionSteps">Asks the decision model whether each judgement lens applies.</param>
public sealed class ReviewSeatsService(
    IReviewWorkTreeService trees,
    IReviewCycleStore cycles,
    IGitChangeReader git,
    IReviewEvidenceService evidenceWriter,
    INotSeatedStore notSeatedStore,
    ISeverityOverrideSource severityOverrides,
    IWorktreeIndexGuard worktreeIndex,
    IWritableFileSystem files,
    IProcessEnvironment environment,
    IReviewCycleSelector selector,
    IConfigLoadService configs,
    IReviewDecisionSteps decisionSteps,
    IPathResolver paths) : IReviewSeatsService
{
    private const string DefaultBase = "origin/HEAD";

    /// <inheritdoc />
    public async Task<ReviewCommandResult> PrepareAsync(
        ReviewSeatsRequest request,
        Func<string, KyberWeaveConfig, IReviewDecisionModel> decisionModel,
        IProcessRunner indexExecutor,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(indexExecutor);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(decisionModel);
        cancellationToken.ThrowIfCancellationRequested();

        string root = paths.GetFullPath(request.Path, environment.CurrentDirectory);
        DiagnosticReport report = new();
        if (!configs.TryLoad(root, request.Config, report, out KyberWeaveConfig config, out _))
            return ReviewCommandResultFactory.Reported(1, report);

        SeatSetup? setup = ResolveSetup(root, request, config, report, out ReviewCommandResult? failure);
        if (setup is null)
            return failure ?? ReviewCommandResultFactory.Reported(1, report);

        // The index is synced before any seat starts, so no seat reads an index older than the diff.
        WorktreeIndexOutcome index = await worktreeIndex.EnsureAsync(
            setup.Tree,
            DoctorOptions.Defaults,
            indexExecutor,
            environment.TempDirectory,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string folder = Path.Combine(setup.ReviewFolder, ScratchLayout.CycleFolder(setup.Plan.Number));
        string gatesPath = Path.Combine(folder, ScratchLayout.GatesFileName);
        string baseRef = request.BaseRef ?? DefaultBase;
        IReadOnlyList<string>? changed = ChangedPaths(root, baseRef);

        IReviewDecisionModel model = decisionModel(root, config);
        CouncilSeatPlan seats = setup.Plan.Prior is { } verified && !request.Full
            ? VerifierSeats(setup.Tree, verified, setup.Recheck)
            : await WithDecisionModelAsync(root, baseRef, changed, CouncilSeating.Plan(changed), model, cancellationToken)
                .ConfigureAwait(false);

        if (!WriteSeats(folder, root, baseRef, gatesPath, setup, changed, seats, index, report, out string evidence))
            return ReviewCommandResultFactory.Reported(1, report);

        PrintDispatch(output, request, setup, seats, model, index, root, folder, gatesPath, evidence);
        return new ReviewCommandResult(0);
    }

    /// <summary>The cycle a seats run found itself in, and where it writes.</summary>
    /// <param name="Tree">The git work tree the review belongs to.</param>
    /// <param name="Task">The task id whose review folder this cycle belongs to.</param>
    /// <param name="Config">The loaded host configuration.</param>
    /// <param name="ReviewFolder">The review folder the cycle belongs to.</param>
    /// <param name="Plan">The cycle number, the prior cycle and the decision for each gate.</param>
    /// <param name="Recheck">The prior cycle's findings to re-check.</param>
    private sealed record SeatSetup(
        string Tree,
        string Task,
        KyberWeaveConfig Config,
        string ReviewFolder,
        ReviewCyclePlan Plan,
        IReadOnlyList<ReviewFinding> Recheck);

    /// <summary>
    /// Names the cycle this run prepares: the task's review folder, the prior cycle and the findings to re-check.
    /// Returns null with the refusal in <paramref name="failure"/> when no cycle can be named.
    /// </summary>
    private SeatSetup? ResolveSetup(
        string root,
        ReviewSeatsRequest request,
        KyberWeaveConfig config,
        DiagnosticReport report,
        out ReviewCommandResult? failure)
    {
        failure = null;
        string tree = trees.Containing(root);
        string scratchpad = ReviewWorkTree.Scratchpad(tree);
        ReviewTaskChoice choice = selector.ChooseTask(tree, scratchpad, request.Task);
        if (choice.Error is { } error)
        {
            failure = ReviewCommandResultFactory.Refused(error);
            return null;
        }

        string reviewFolder = Path.Combine(scratchpad, choice.Task, ScratchLayout.ReviewFolderName);
        PriorCycles prior = trees.Prior(tree, request.BaseRef, cycles.ReadFinished(reviewFolder));
        ReviewCyclePlan plan = ReviewCyclePlanning.Plan(
            prior.Valid,
            config.Review.Gates,
            ReviewGateReporting.CurrentOs(environment),
            commit => selector.ChangedSince(tree, commit));
        IReadOnlyList<ReviewFinding> recheck = [];
        if (plan.Prior is { } priorCycle)
        {
            (bool ok, IReadOnlyList<ReviewFinding> found) = selector.ReadRecheck(
                priorCycle, config.Review.Policy, report, ReviewDiagnosticCodes.UnreadableInput, Severity.Error, "review seats");
            if (!ok)
            {
                failure = ReviewCommandResultFactory.Reported(1, report);
                return null;
            }

            recheck = found;
        }

        return new SeatSetup(tree, choice.Task, config, reviewFolder, plan, recheck);
    }

    /// <summary>
    /// Writes the evidence, the seat packets and the not-seated record into the cycle folder. Returns whether they were
    /// written; a folder this process may not write is reported and the run ends.
    /// </summary>
    private bool WriteSeats(
        string folder,
        string root,
        string baseRef,
        string gatesPath,
        SeatSetup setup,
        IReadOnlyList<string>? changed,
        CouncilSeatPlan seats,
        WorktreeIndexOutcome index,
        DiagnosticReport report,
        out string evidence)
    {
        evidence = string.Empty;
        try
        {
            evidence = evidenceWriter.Write(
                folder, root, baseRef, null, gatesPath, setup.Plan.Prior?.Head, setup.Config.Review.Coverage);
            CouncilSeatContext context = new(
                setup.Tree,
                evidence,
                index.Status is WorktreeIndexStatus.Ready or WorktreeIndexStatus.NotGoverned,
                StrictnessName(setup.Config.Review.Policy.Strictness),
                setup.Config.Review.Policy.MinConfidence,
                setup.Config.Review.Policy.FixLevelName,
                severityOverrides.GuidancePath(root, setup.Config.ConfigReg),
                Path.Combine(folder, "duplicates.json"),
                severityOverrides.Load(root, setup.Config.ConfigReg),
                changed);
            IReadOnlyList<string> plans = ChangePlanLookup.Find(
                changed,
                ChangePlanLookup.PlanFolder(setup.Config),
                relative => ReadPlanText(setup.Tree, relative));
            WritePackets(folder, seats, ChangePlanLookup.WithPlan(context, plans));
            notSeatedStore.Replace(folder, ReviewLensCatalogue.Names, seats.NotSeated);
            return true;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            report.Add(new Diagnostic(
                ReviewDiagnosticCodes.ReportWriteFailed,
                Severity.Error,
                $"Could not write the evidence or packets to '{folder}': {ex.Message}",
                "review seats"));
            return false;
        }
    }

    /// <summary>Prints the cycle header, the index state, the not-seated lines and every seat's dispatch block.</summary>
    private void PrintDispatch(
        TextWriter output,
        ReviewSeatsRequest request,
        SeatSetup setup,
        CouncilSeatPlan seats,
        IReviewDecisionModel model,
        WorktreeIndexOutcome index,
        string root,
        string folder,
        string gatesPath,
        string evidence)
    {
        ReviewCyclePlan plan = setup.Plan;
        string tree = setup.Tree;
        string reviewRun = ScratchLayout.ReviewRun(setup.Task, plan.Number);
        output.WriteLine($"cycle: {plan.Number} mode: {ReviewCycleView.Mode(plan)}");
        output.WriteLine($"folder: {folder}");
        output.WriteLine($"evidence: {evidence}");
        output.WriteLine(index.Status switch
        {
            WorktreeIndexStatus.Ready => $"index: {tree} ready",
            WorktreeIndexStatus.NotGoverned => $"index: {tree} not checked (no governed docs here)",
            _ => $"index: {tree} unavailable ({ReviewDiagnosticCodes.IndexState}: {index.Checks.FirstOrDefault(check => !check.Passed)?.Reason ?? "no reason reported"}); fix: kyber-weave doctor --ensure {tree}",
        });
        if (plan.Prior is not null)
        {
            foreach (string line in ReviewCycleView.VerifierLines(plan, setup.Recheck))
                output.WriteLine(line);
            string[] seated = [.. seats.Now.Concat(seats.AfterGates)];
            output.WriteLine(request.Full
                ? "verifier: --full seats every applicable lens of the whole change, as a person asked; the diff is still the fix delta."
                : $"verifier: seated {(seated.Length == 0 ? "none" : string.Join(", ", seated))} over the fix delta; every other lens is carried from the prior cycle. Send only the blocks below and widen nothing.");
        }

        output.WriteLine($"run: {ReviewCycleView.GatesCommand(root, gatesPath, request.BaseRef)} --deadline");
        foreach ((string lens, string reason) in seats.NotSeated.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            output.WriteLine($"not-seated: --not-seated \"{lens}={reason}\"");

        int ruledOut = model.Decisions.Count(decision => decision.Effect == "not seated");
        if (model.SummaryLine(ruledOut > 0 ? [$"{ruledOut} lens(es) not seated"] : []) is { } summaryLine)
            output.WriteLine(summaryLine);

        output.WriteLine($"now: {seats.Now.Count} seats; send them in one batch with the run: line");
        foreach (string lens in seats.Now)
            output.Write(Block(folder, lens, reviewRun, request.HandBack));

        output.WriteLine($"after-gates: {string.Join(", ", seats.AfterGates)}; send each one the gate run does not print as not-seated");
        foreach (string lens in seats.AfterGates)
            output.Write(Block(folder, lens, reviewRun, request.HandBack));

        output.WriteLine($"next: {ReviewSeatsCommandVerdictLine(request.Task)} --candidates");
        output.Flush();
    }

    /// <summary>The verdict command line the reviewer copies; it needs no other argument, since the cycle folder records the rest.</summary>
    /// <param name="task">The task id the verdict should read, or null when the cycle folder records it.</param>
    internal static string ReviewSeatsCommandVerdictLine(string? task) =>
        task is null ? "kyber-weave review verdict ." : $"kyber-weave review verdict . --task {task}";

    /// <summary>
    /// Leaves out each judgement lens the decision model confidently finds does not apply to the change, asking
    /// without blocking a thread. With no model configured, or no changed-file list, the seating is returned unchanged.
    /// </summary>
    private async Task<CouncilSeatPlan> WithDecisionModelAsync(
        string root,
        string baseRef,
        IReadOnlyList<string>? changed,
        CouncilSeatPlan seats,
        IReviewDecisionModel model,
        CancellationToken cancellationToken)
    {
        if (!model.Configured || changed is not { Count: > 0 })
            return seats;

        string patch;
        try
        {
            patch = git.DiffSinceMergeBase(root, baseRef).Patch;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            patch = string.Empty;
        }

        IReadOnlyDictionary<string, string> ruledOut = await decisionSteps.NotApplicableLensesAsync(
                model, seats.Now, ReviewLensRules.Summarize(changed, patch), cancellationToken)
            .ConfigureAwait(false);
        if (ruledOut.Count == 0)
            return seats;

        Dictionary<string, string> notSeated = new(seats.NotSeated, StringComparer.Ordinal);
        foreach ((string lens, string reason) in ruledOut)
            notSeated[lens] = reason;
        return new CouncilSeatPlan([.. seats.Now.Where(lens => !ruledOut.ContainsKey(lens))], seats.AfterGates, notSeated);
    }

    /// <summary>
    ///     The verifier cycle's seats, measured on the fix delta from the prior cycle's head: what the fix changed, and how
    ///     many lines. A head git cannot compare with leaves the files unknown, which still seats the narrow set.
    /// </summary>
    private CouncilSeatPlan VerifierSeats(string tree, ReviewCycleRecord prior, IReadOnlyList<ReviewFinding> recheck)
    {
        IReadOnlyList<string>? fixPaths = null;
        int fixLines = 0;
        if (prior.Head is { } head)
        {
            fixPaths = selector.ChangedSince(tree, head);
            try
            {
                fixLines = ChangedLineCount(git.DiffSince(tree, head));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
            {
                fixLines = 0;
            }
        }

        return CouncilSeating.VerifierPlan(recheck, fixPaths, fixLines);
    }

    /// <summary>Added plus removed lines in a unified diff, file headers excluded.</summary>
    /// <param name="patch">The unified diff.</param>
    internal static int ChangedLineCount(string patch) =>
        patch.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Count(line => (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
                           || (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)));

    private static string StrictnessName(ReviewStrictness strictness) => strictness switch
    {
        ReviewStrictness.Lenient => "lenient",
        ReviewStrictness.Strict => "strict",
        _ => "balanced",
    };

    private static string Block(string folder, string lens, string reviewRun, bool handBack) =>
        CouncilSeating.DispatchBlock(
            lens,
            reviewRun,
            Path.Combine(folder, "packets", lens + ".md"),
            handBack ? Path.Combine(folder, "seats", lens + ".md") : null);

    private void WritePackets(string folder, CouncilSeatPlan seats, CouncilSeatContext context)
    {
        string packets = Path.Combine(folder, "packets");
        files.CreateDirectory(packets);
        files.CreateDirectory(Path.Combine(folder, "seats"));
        foreach (string lens in seats.Now.Concat(seats.AfterGates))
            files.WriteAllText(Path.Combine(packets, lens + ".md"), CouncilSeating.Packet(context, lens));
    }

    private IReadOnlyList<string>? ChangedPaths(string root, string baseRef)
    {
        try
        {
            return [.. git.ResolveSinceMergeBase(root, baseRef).Select(change => change.Path)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The changed file's text through the file seam, or null when it cannot be read.</summary>
    /// <param name="tree">The work tree the changed paths are relative to.</param>
    /// <param name="relative">The changed path, repository-relative.</param>
    private string? ReadPlanText(string tree, string relative)
    {
        try
        {
            string path = Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar));
            return files.FileExists(path) ? files.ReadAllText(path) : null;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException)
        {
            return null;
        }
    }
}
