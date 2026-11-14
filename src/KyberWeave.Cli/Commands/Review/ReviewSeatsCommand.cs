using System.ComponentModel;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     Prepares the council's seats in a few seconds, so they start while the gates run: indexes the tree, opens the
///     cycle folder, writes the diff and the change half of the evidence, writes every packet and prints each seat's
///     dispatch block, the lenses a catalogue file pattern rules out, and the gate command to run alongside.
/// </summary>
public sealed class ReviewSeatsCommand : Command<ReviewSeatsSettings>
{
    /// <summary>The command description shared by the real registration and the help tests.</summary>
    public const string Description =
        "Prepare the review council's seats: index the tree, write the diff, evidence and packets, and print each seat's dispatch block.";

    private const string UnreadableInput = "KW-REVIEW-023";
    private const string ReportWriteFailed = "KW-REVIEW-025";
    private const string IndexState = "KW-REVIEW-038";
    private const string DefaultBase = "origin/HEAD";
    private const int ClientInputExitCode = 2;

    private static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(DoctorSettings.DefaultIndexTimeoutSeconds);
    private static readonly TimeSpan McpTimeout = TimeSpan.FromSeconds(DoctorSettings.DefaultMcpTimeoutSeconds);

    /// <summary>The process seam for the CodeGraph child that prepares the index; tests pass a scripted one.</summary>
    internal IProcessExecutor? Executor { get; init; }

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewSeatsSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string root = Path.GetFullPath(settings.Path);
        DiagnosticReport report = new();
        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(report, settings, "review seats", "Review");
            return 1;
        }

        if (settings.BaseRef is not null && ChangedFiles.ValidateBase(settings.BaseRef) is { } baseProblem)
        {
            Say($"kyber-weave review seats: error: {baseProblem}");
            return ClientInputExitCode;
        }

        string tree = ReviewWorkTree.Of(root);
        string scratchpad = ReviewWorkTree.Scratchpad(tree);
        if (!ReviewCycleView.TryChooseTask(tree, scratchpad, settings.Task, out string task, out _, out string? problem))
        {
            Say($"kyber-weave review seats: error: {problem}");
            return ClientInputExitCode;
        }

        string reviewFolder = Path.Combine(scratchpad, task, "review");
        PriorCycles prior = ReviewWorkTree.Prior(tree, settings.BaseRef, ReviewCycles.ReadFinished(reviewFolder));
        ReviewCyclePlan plan = ReviewCycles.Plan(
            prior.Valid,
            config.Review.Gates,
            OperatingSystem.IsWindows() ? "windows" : "unix",
            commit => ReviewCycleView.TryChangedSince(tree, commit));
        List<ReviewFinding> recheck = [];
        if (plan.Prior is { } priorCycle
            && !ReviewCycleView.TryReadRecheck(priorCycle, config.Review.Policy, report, recheck, UnreadableInput, Severity.Error, "review seats"))
        {
            CommandHelpers.Finish(report, settings, "review seats", "Review");
            return 1;
        }

        // The index is synced before any seat starts, so no seat reads an index older than the diff.
        WorktreeIndexOutcome index = WorktreeIndexGuard.Ensure(
            tree,
            new DoctorOptions(IndexTimeout, McpTimeout, DoctorCommand.CodeGraphExecutable, DoctorCommand.DefaultMcpExecutable),
            Executor ?? ProcessExecutor.WithTimeout(IndexTimeout),
            Path.GetTempPath());
        bool indexReady = index.Status is WorktreeIndexStatus.Ready or WorktreeIndexStatus.NotGoverned;

        string folder = Path.Combine(reviewFolder, $"cycle-{plan.Number}");
        string gatesPath = Path.Combine(folder, "gates.json");
        string baseRef = settings.BaseRef ?? DefaultBase;
        IReadOnlyList<string>? changed = ChangedPaths(root, baseRef);

        string evidence;
        ReviewDecisionModel model = ReviewDecisionModelFactory.Create(root, config, settings.NoDecisionModel, null);
        CouncilSeatPlan seats = plan.Prior is { } verified && !settings.Full
            ? VerifierSeats(tree, verified, recheck)
            : WithDecisionModel(root, baseRef, changed, CouncilSeating.Plan(changed), model, cancellationToken);
        try
        {
            evidence = ReviewEvidence.Write(
                folder, root, baseRef, null, gatesPath, plan.Prior?.Head, config.Review.Coverage);
            WritePackets(folder, seats, new CouncilSeatContext(
                tree,
                evidence,
                indexReady,
                StrictnessName(config.Review.Policy.Strictness),
                config.Review.Policy.MinConfidence,
                config.Review.Policy.FixLevelName,
                GuidancePath(root, config),
                Path.Combine(folder, "duplicates.json")));
            NotSeatedRecord.Replace(folder, ReviewLensCatalogue.Names, seats.NotSeated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                ReportWriteFailed,
                Severity.Error,
                $"Could not write the evidence or packets to '{folder}': {ex.Message}",
                "review seats"));
            CommandHelpers.Finish(report, settings, "review seats", "Review");
            return 1;
        }

        string reviewRun = $"{task}-cycle-{plan.Number}";
        Say($"cycle: {plan.Number} mode: {ReviewCycleView.Mode(plan)}");
        Say($"folder: {folder}");
        Say($"evidence: {evidence}");
        Say(index.Status switch
        {
            WorktreeIndexStatus.Ready => $"index: {tree} ready",
            WorktreeIndexStatus.NotGoverned => $"index: {tree} not checked (no governed docs here)",
            _ => $"index: {tree} unavailable ({IndexState}: {index.Checks.FirstOrDefault(check => !check.Passed)?.Reason ?? "no reason reported"}); fix: kyber-weave doctor --ensure {tree}",
        });
        if (plan.Prior is not null)
        {
            foreach (string line in ReviewCycleView.VerifierLines(plan, recheck))
                Say(line);
            string[] seated = [.. seats.Now.Concat(seats.AfterGates)];
            Say(settings.Full
                ? "verifier: --full seats every applicable lens of the whole change, as a person asked; the diff is still the fix delta."
                : $"verifier: seated {(seated.Length == 0 ? "none" : string.Join(", ", seated))} over the fix delta; every other lens is carried from the prior cycle. Send only the blocks below and widen nothing.");
        }

        Say($"run: {ReviewCycleView.GatesCommand(root, gatesPath, settings.BaseRef)} --deadline");
        foreach ((string lens, string reason) in seats.NotSeated.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            Say($"not-seated: --not-seated \"{lens}={reason}\"");

        int ruledOut = model.Decisions.Count(decision => decision.Effect == "not seated");
        if (model.SummaryLine(ruledOut > 0 ? [$"{ruledOut} lens(es) not seated"] : []) is { } summaryLine)
            Say(summaryLine);

        Say($"now: {seats.Now.Count} seats; send them in one batch with the run: line");
        foreach (string lens in seats.Now)
            Console.Write(Block(folder, lens, reviewRun, settings.HandBack));

        Say($"after-gates: {string.Join(", ", seats.AfterGates)}; send each one the gate run does not print as not-seated");
        foreach (string lens in seats.AfterGates)
            Console.Write(Block(folder, lens, reviewRun, settings.HandBack));

        Say($"next: {VerdictCommand(settings.Task)} --candidates");
        Console.Out.Flush();
        return 0;
    }

    /// <summary>The verdict command line the reviewer copies; it needs no other argument, since the cycle folder records the rest.</summary>
    internal static string VerdictCommand(string? task) =>
        task is null ? "kyber-weave review verdict ." : $"kyber-weave review verdict . --task {task}";

    private static void Say(string line) => Console.WriteLine(line);

    /// <summary>
    /// Leaves out each judgement lens the decision model confidently finds does not apply to the change. With no model
    /// configured, or no changed-file list, the seating is returned unchanged.
    /// </summary>
    private static CouncilSeatPlan WithDecisionModel(
        string root,
        string baseRef,
        IReadOnlyList<string>? changed,
        CouncilSeatPlan seats,
        ReviewDecisionModel model,
        CancellationToken cancellationToken)
    {
        if (!model.Configured || changed is not { Count: > 0 })
            return seats;

        string patch;
        try
        {
            patch = ChangedFiles.DiffSinceMergeBase(root, baseRef).Patch;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            patch = string.Empty;
        }

        IReadOnlyDictionary<string, string> ruledOut = ReviewLensApplicability.NotApplicableAsync(
                model, seats.Now, ReviewLensApplicability.Summarize(changed, patch), cancellationToken)
            .GetAwaiter()
            .GetResult();
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
    private static CouncilSeatPlan VerifierSeats(string tree, ReviewCycleRecord prior, IReadOnlyList<ReviewFinding> recheck)
    {
        IReadOnlyList<string>? fixPaths = null;
        int fixLines = 0;
        if (prior.Head is { } head)
        {
            fixPaths = ReviewCycleView.TryChangedSince(tree, head);
            try
            {
                fixLines = ChangedLineCount(ChangedFiles.DiffSince(tree, head));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
            {
                fixLines = 0;
            }
        }

        return CouncilSeating.VerifierPlan(recheck, fixPaths, fixLines);
    }

    /// <summary>Added plus removed lines in a unified diff, file headers excluded.</summary>
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

    private static void WritePackets(string folder, CouncilSeatPlan seats, CouncilSeatContext context)
    {
        string packets = Path.Combine(folder, "packets");
        Directory.CreateDirectory(packets);
        Directory.CreateDirectory(Path.Combine(folder, "seats"));
        foreach (string lens in seats.Now.Concat(seats.AfterGates))
            File.WriteAllText(Path.Combine(packets, lens + ".md"), CouncilSeating.Packet(context, lens));
    }

    private static IReadOnlyList<string>? ChangedPaths(string root, string baseRef)
    {
        try
        {
            return [.. ChangedFiles.ResolveSinceMergeBase(root, baseRef).Select(change => change.Path)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The review guidance file the registry declares (default <c>REVIEW.md</c>), or null when it is missing.</summary>
    private static string? GuidancePath(string root, KyberWeaveConfig config)
    {
        string declared = config.ConfigReg.Additions
            .FirstOrDefault(entry => string.Equals(entry.Name, "review-guidance", StringComparison.Ordinal))?.Path
            ?? "REVIEW.md";
        string path = Path.GetFullPath(Path.Combine(root, declared));
        return File.Exists(path) ? path : null;
    }
}

/// <summary>Options for <c>review seats</c>.</summary>
public sealed class ReviewSeatsSettings : AnalysisSettings
{
    /// <summary>The task id whose review folder holds the run's cycles.</summary>
    [CommandOption("--task <ID>")]
    [Description("The task id whose review folder, .agent_scratchpad/<task-id>/review, holds the run's cycles. Omitted: the only review folder, or the one named for the current branch.")]
    public string? Task { get; set; }

    /// <summary>The ref the change is measured from.</summary>
    [CommandOption("--base <REF>")]
    [Description("Git ref the change is measured from. Defaults to origin/HEAD.")]
    public string? BaseRef { get; set; }

    /// <summary>Whether each block names the seat file the seat writes, for the review-council skill.</summary>
    [CommandOption("--hand-back")]
    [Description("Add a 'Seat file:' line to every block, so each seat writes its output there (the review-council skill's path).")]
    public bool HandBack { get; set; }

    /// <summary>Whether a later cycle seats every applicable lens instead of only those the fix needs.</summary>
    [CommandOption("--full")]
    [Description("In a later cycle, seat every applicable lens instead of only the lenses that raised the findings being fixed. A person's choice (the full mode); the review loop never passes it.")]
    public bool Full { get; set; }

    /// <summary>Whether the decision model is left out of the lens-applicability step.</summary>
    [CommandOption("--no-decision-model")]
    [Description("Do not ask the decision model (arbiter.provider) whether each judgement lens applies. Without a configured model nothing is asked anyway.")]
    public bool NoDecisionModel { get; set; }
}
