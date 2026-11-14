using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
/// Computes the review verdict from the council's seat output, or a findings document, and the gate report.
/// </summary>
/// <remarks>
/// <para>
/// The seat folder replaced the hand-built findings document because reviewers kept writing that document
/// wrong: most hand-built files were refused as unreadable, and each refusal cost a council cycle. A seat's own
/// output is parsed instead, and the command, not the reviewer, accounts for every catalogue lens.
/// </para>
/// <para>
/// Nothing here changes what a seat said about a finding. A finding can be dropped only with a stated reason,
/// or moved only when its cited line cannot exist, and both are reported; severity and confidence pass
/// through untouched to <see cref="VerdictEngine"/>, which stays the only judge.
/// </para>
/// </remarks>
public sealed partial class ReviewVerdictCommand : Command<ReviewVerdictSettings>
{
    /// <summary>The command description shared by the real registration and the help tests.</summary>
    public const string Description = "Compute approve / request-changes from findings and gates.";

    private const string UnreadableInput = "KW-REVIEW-023";
    private const string Verdict = "KW-REVIEW-024";
    private const string ReportWriteFailed = "KW-REVIEW-025";
    private const string SeatUnreadable = "KW-REVIEW-040";
    private const string LensCoverageCode = "KW-REVIEW-041";
    private const string FindingDropped = "KW-REVIEW-042";
    private const string CitedLine = "KW-REVIEW-043";
    private const string DiscoveryGap = "KW-REVIEW-044";
    private const string HandBuiltFindings = "KW-REVIEW-045";
    private const string RefutationCandidates = "KW-REVIEW-046";
    private const string InputSitesMissing = "KW-REVIEW-047";
    private const string SeatBudgetReached = "KW-REVIEW-054";
    private const string GateCarried = "KW-REVIEW-048";
    private const string GateNotRerun = "KW-REVIEW-049";
    private const string PriorNotThisChange = "KW-REVIEW-050";
    private const string FixRoundCode = "KW-REVIEW-056";
    private const string CorrectnessLens = "correctness";
    private const string ReviewerSeat = "reviewer";
    private const string PriorFindingsFile = "findings.json";
    private const string Subject = "review";

    /// <summary>The <c>Index fallback:</c> modes the seat contract defines; a wrong-branch mode carries the branches after it.</summary>
    private static readonly string[] KnownIndexFallbackModes =
        ["projectPath: error", "projectPath: not-indexed", "projectPath: wrong-branch"];

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewVerdictSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();

        // An option given an empty value names nothing. Read as "absent" it would fall back silently, and as a path
        // it resolves to the current directory (`--prior ""` read ./findings.json), so every one is refused first.
        if (RefuseEmptyValues(settings, report))
            return Fail(report, settings);

        string root = Path.GetFullPath(settings.Path);

        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
            return Fail(report, settings);

        if (settings.Seats is null && settings.Findings is null && !UseCurrentCycle(root, settings, report))
            return Fail(report, settings);

        if (settings.Prior is not null && !PriorBelongsToThisChange(root, settings, report))
            return Fail(report, settings);

        if (!TryReadInput(settings, config.Review.Policy, report, out FindingsReport? input) || input is null)
            return Fail(report, settings);

        // A missing gate report is not an error: a change may legitimately be reviewed
        // before any gate exists. It does mean nothing was executed, and the absence of a
        // blocking gate result is visible to the engine rather than mistaken for a pass.
        GateReport? gates = null;
        if (settings.Gates is not null &&
            !TryRead(settings.Gates, "--gates", ReviewJson.ReadGates, report, out gates))
        {
            return Fail(report, settings);
        }

        IReadOnlyList<GateResult> gateResults = WithCarriedGates(root, config, settings, gates, report);
        if (report.HasErrors)
            return Fail(report, settings);

        List<DroppedFinding> dropped = [];
        List<ReviewFinding> findings = ApplyDrops(input.Findings, settings.Drop, report, dropped);
        if (report.HasErrors)
            return Fail(report, settings);

        findings = CheckCitedLines(root, findings, report, dropped);
        findings = DropNitpicksBelowTheFixLevel(findings, config.Review.Policy, report, dropped);

        ReviewDecisionModel model = ReviewDecisionModelFactory.Create(
            root, config, settings.NoDecisionModel, DecisionCachePath(settings));
        List<string> modelEffects = [];
        findings = ApplyDecisionModel(
            model, findings, settings.Candidates, config.Review.Policy.FixLevel == ReviewFixLevel.Nitpick, report, dropped, modelEffects, cancellationToken);

        ReviewOutcome outcome = VerdictEngine.Evaluate(
            findings,
            gateResults,
            config.Review,
            DateOnly.FromDateTime(DateTime.UtcNow),
            gates?.Coverage);
        outcome = outcome with { Dropped = [.. dropped, .. outcome.Dropped] };

        report.AddRange(outcome.Diagnostics);
        ReportRefutationCandidates(root, model, outcome, gateResults, report, modelEffects, cancellationToken);
        if (!settings.Candidates)
            ReportSeverityNotes(model, outcome.Accepted, report, modelEffects, cancellationToken);
        if (model.SummaryLine(modelEffects) is { } summaryLine)
            report.Add(new Diagnostic(ReviewVerdictDecisions.Code, Severity.Info, summaryLine, Subject));

        if (settings.Candidates)
        {
            // A partial read lists candidates only: a verdict, findings.json or a recorded cycle now would let a later
            // reader take it for the cycle's verdict.
            CommandHelpers.Finish(report, settings, "review verdict", "Review");
            if (ReportRenderer.IsHuman(settings.ParsedFormat))
                Console.WriteLine($"next: refute each candidate listed above, then {ReviewSeatsCommand.VerdictCommand(settings.Task)}");
            return 0;
        }

        report.Add(new Diagnostic(
            Verdict,
            outcome.Verdict == ReviewVerdict.Approve ? Severity.Info : Severity.Error,
            $"{DescribeForSummary(outcome.Verdict)} — risk {outcome.Risk.ToString().ToUpperInvariant()}, " +
            $"{outcome.Accepted.Count} finding(s) accepted, {outcome.Dropped.Count} dropped.",
            Subject));

        ReportFixRound(outcome, gateResults, config.Review.Policy, PriorFixCounts(root, settings, config), report);

        report.AddMetric("verdict", outcome.Verdict.ToString());
        report.AddMetric("risk", outcome.Risk.ToString());
        report.AddMetric("accepted", outcome.Accepted.Count);
        report.AddMetric("dropped", outcome.Dropped.Count);

        CommandHelpers.TryWriteReport(
            settings.Out,
            ReviewJson.Write(input with { Findings = findings }),
            ReportWriteFailed,
            report);
        RecordCycleIdentity(root, settings);

        CommandHelpers.Finish(report, settings, "review verdict", "Review");
        return report.HasErrors && outcome.Verdict == ReviewVerdict.Approve ? 1 : outcome.ExitCode;
    }

    /// <summary>The drop reason that marks a finding about pre-existing code the change did not touch.</summary>
    internal const string PreExistingDebt = "pre-existing debt";

    /// <summary>
    /// Says what the fix round takes and what becomes follow-up work, one line each, so the conductor dispatches from
    /// the lines and never decides the scope itself. <c>fix:</c> lines are every failed blocking gate and every
    /// finding at or above <c>review.policy.fix-level</c>. Any <c>fix:</c> line gets a fix round and then a
    /// verifier cycle, an APPROVE included, until a cycle prints none or a cap in <see cref="ReviewLoop"/> stops the
    /// loop; the findings still to fix are then <c>open:</c> lines for human review. <c>follow-up:</c> lines are the
    /// findings below the level and each finding dropped as pre-existing debt.
    /// </summary>
    private static void ReportFixRound(
        ReviewOutcome outcome, IReadOnlyList<GateResult> gates, ReviewPolicy policy, List<int> priorCounts, DiagnosticReport report)
    {
        List<string> fix = [.. FailedGates(gates).Select(gate => $"gate {gate.Id}")];
        fix.AddRange(outcome.Accepted.Where(policy.FixRoundTakes).Select(Line));
        List<string> followUp = [.. outcome.Accepted.Where(finding => !policy.FixRoundTakes(finding)).Select(Line)];
        followUp.AddRange(outcome.Dropped
            .Where(drop => drop.Reason.StartsWith(PreExistingDebt, StringComparison.OrdinalIgnoreCase))
            .Select(drop => $"{Line(drop.Finding)} ({PreExistingDebt})"));

        string level = policy.FixLevelName;
        ReviewLoopState state = ReviewLoop.Decide([.. priorCounts, fix.Count]);
        bool stopped = state is ReviewLoopState.CycleCap or ReviewLoopState.NoProgress;
        foreach (string line in fix)
            report.Add(new Diagnostic(FixRoundCode, Severity.Info, $"{(stopped ? "open" : "fix")}: {line}", Subject));
        foreach (string line in followUp)
            report.Add(new Diagnostic(FixRoundCode, Severity.Info, $"follow-up: {line}", Subject));
        report.Add(new Diagnostic(
            FixRoundCode,
            Severity.Info,
            state switch
            {
                ReviewLoopState.Clean when outcome.Verdict == ReviewVerdict.Approve => $"fix round: none (fix level {level}); review clean: proceed to archive.",
                ReviewLoopState.Clean => $"fix round: none (fix level {level}); nothing is at or above that level, so each follow-up: line goes to human review.",
                ReviewLoopState.CycleCap =>
                    $"fix round: none (fix level {level}); the loop stopped at its {ReviewLoop.MaxCycles.ToString(System.Globalization.CultureInfo.InvariantCulture)}-cycle cap, so each open: line goes to human review.",
                ReviewLoopState.NoProgress =>
                    $"fix round: none (fix level {level}); the loop stopped because {ReviewLoop.NoProgressCycles.ToString(System.Globalization.CultureInfo.InvariantCulture)} cycles in a row did not lower the findings to fix, so each open: line goes to human review.",
                _ => $"fix round: {fix.Count} item(s) at fix level {level}; fix these, then run a verifier cycle."
            },
            Subject));

        static string SeverityWord(ReviewSeverity severity) => severity switch
        {
            ReviewSeverity.Critical => "critical",
            ReviewSeverity.Major => "major",
            ReviewSeverity.Nitpick => "nitpick",
            _ => "minor"
        };

        static string Line(ReviewFinding finding) =>
            $"{finding.Id} ({SeverityWord(finding.Severity)}) {finding.File}:{finding.Line.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>The blocking gates that failed and are not just stopped for time or not applicable.</summary>
    private static IEnumerable<GateResult> FailedGates(IReadOnlyList<GateResult> gates) => gates
        .Where(gate => gate.Blocking && !gate.Passed && gate.NotApplicableReason is null && !VerdictEngine.IsStoppedForTime(gate));

    /// <summary>
    /// The count of findings to fix after each earlier cycle of this change, oldest first, each recomputed from the
    /// cycle's own findings and gates; empty without <c>--prior</c>. A cycle that cannot be read is left out.
    /// </summary>
    private static List<int> PriorFixCounts(string root, ReviewVerdictSettings settings, KyberWeaveConfig config)
    {
        List<int> counts = [];
        if (settings.Prior is null)
            return counts;

        IReadOnlyList<ReviewCycleRecord> history;
        try
        {
            history = ReviewWorkTree.Prior(
                ReviewWorkTree.Of(root), settings.BaseRef, ReviewCycles.ReadThrough(settings.Prior), CurrentCycleFolder(settings)).Valid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return counts;
        }

        foreach (ReviewCycleRecord cycle in history)
        {
            try
            {
                FindingsReport prior = ReviewJson.ReadFindings(File.ReadAllText(cycle.FindingsPath));
                IReadOnlyList<GateResult> gates = cycle.Gates?.Gates ?? [];
                ReviewOutcome outcome = VerdictEngine.Evaluate(prior.Findings, gates, config.Review, DateOnly.FromDateTime(DateTime.UtcNow));
                counts.Add(FailedGates(gates).Count() + outcome.Accepted.Count(config.Review.Policy.FixRoundTakes));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException or InvalidOperationException)
            {
                // A cycle that cannot be read adds no count: the loop then counts fewer cycles, never more.
            }
        }

        return counts;
    }

    /// <summary>
    /// The gate results the verdict is computed from. With <c>--prior</c>, a declared blocking gate this cycle did not
    /// run keeps the result of the cycle that last ran it, unless a file changed since that cycle matches the gate's
    /// inputs or the gate had not passed: then the cycle is refused, because dropping the gate would read as a pass.
    /// </summary>
    private static IReadOnlyList<GateResult> WithCarriedGates(
        string root,
        KyberWeaveConfig config,
        ReviewVerdictSettings settings,
        GateReport? gates,
        DiagnosticReport report)
    {
        IReadOnlyList<GateResult> given = gates?.Gates ?? [];
        if (settings.Prior is null || gates is null)
            return given;

        ReviewGate[] absent =
        [
            .. config.Review.Gates.Where(gate => gate.Blocking
                                                 && !given.Any(result => string.Equals(result.Id, gate.Id, StringComparison.Ordinal)))
        ];
        IReadOnlyList<ReviewCycleRecord> history =
            ReviewWorkTree.Prior(ReviewWorkTree.Of(root), settings.BaseRef, ReviewCycles.ReadThrough(settings.Prior), CurrentCycleFolder(settings)).Valid;
        if (absent.Length == 0 || history.Count == 0)
            return given;

        List<GateResult> effective = [.. given];
        List<GateCycleDecision> decisions = [.. ReviewCycles.DecideGates(
            history,
            absent,
            OperatingSystem.IsWindows() ? "windows" : "unix",
            commit => TryChangedSince(ReviewWorkTree.Of(root), commit))];
        string[] outstanding =
            [.. decisions.Where(decision => decision is { Action: GateCycleAction.Rerun, FromCycle: not null }).Select(decision => decision.GateId)];
        string command = ReviewCycleView.GatesCommand(root, Path.GetFullPath(settings.Gates!), settings.BaseRef);
        foreach (GateCycleDecision decision in decisions)
        {
            if (decision is { Action: GateCycleAction.Carry, Carried: { } carried })
            {
                effective.Add(carried with { Summary = $"carried from cycle {decision.FromCycle}: {carried.Summary}" });
                report.Add(new Diagnostic(
                    GateCarried,
                    Severity.Info,
                    $"{decision.GateId}: carried from cycle {decision.FromCycle} ({decision.Reason}).",
                    decision.GateId));
            }
            else if (decision is { Action: GateCycleAction.Rerun, FromCycle: not null })
            {
                report.Add(new Diagnostic(
                    GateNotRerun,
                    Severity.Error,
                    $"{decision.GateId}: this cycle's gate report has no result for it, and cycle {decision.FromCycle}'s " +
                    $"result cannot be carried ({decision.Reason}). Gates still outstanding: {string.Join(", ", outstanding)}. " +
                    $"One command runs them all: {command}",
                    decision.GateId,
                    Hint: $"Run this once, then compute the verdict again: {command}"));
            }
        }

        return effective;
    }

    /// <summary>
    /// Refuses a <c>--prior</c> folder that belongs to another change: carrying its gate results and lens coverage
    /// would let a stale folder from an earlier run stand in for this change's review.
    /// </summary>
    private static bool PriorBelongsToThisChange(string root, ReviewVerdictSettings settings, DiagnosticReport report)
    {
        IReadOnlyList<ReviewCycleRecord> history = ReviewCycles.ReadThrough(settings.Prior!);
        if (history.Count == 0)
            return true;

        IgnoredCycle? named = ReviewWorkTree.Prior(ReviewWorkTree.Of(root), settings.BaseRef, history, CurrentCycleFolder(settings)).Ignored
            .FirstOrDefault(cycle => cycle.Record.Number == history[^1].Number && cycle.Record.Folder == history[^1].Folder);
        if (named is null)
            return true;

        report.Add(new Diagnostic(
            PriorNotThisChange,
            Severity.Error,
            $"--prior {named.Record.Folder} does not belong to this change: {named.Reason}.",
            Subject,
            Hint: "Pass the folder of the cycle this review's own gate run wrote, or leave --prior out for a first cycle."));
        return false;
    }

    /// <summary>The folder of the cycle this verdict belongs to: the one <c>--out</c> or <c>--gates</c> sits in, when it records its change.</summary>
    private static string? CurrentCycleFolder(ReviewVerdictSettings settings)
    {
        foreach (string? path in new[] { settings.Out, settings.Gates })
        {
            if (path is not null && Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder && ReviewCycles.HasIdentity(folder))
                return folder;
        }

        return null;
    }

    /// <summary>
    /// When the findings document is written into a cycle folder that records no change yet, records the branch, base
    /// and head the verdict was computed on, so the next cycle can tell the folder belongs to this change.
    /// </summary>
    private static void RecordCycleIdentity(string root, ReviewVerdictSettings settings)
    {
        if (settings.Out is null || !ReviewCycleView.TryFromOutPath(settings.Out, out _, out _))
            return;

        string folder = Path.GetDirectoryName(Path.GetFullPath(settings.Out))!;
        if (ReviewCycles.HasIdentity(folder) || ReviewWorkTree.IdentityOf(ReviewWorkTree.Of(root), settings.BaseRef) is not { } identity)
            return;

        try
        {
            ReviewCycles.WriteIdentity(folder, identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The findings were written; without the record the next cycle runs in full, which is the safe answer.
        }
    }

    private static IReadOnlyList<string>? TryChangedSince(string root, string commit)
    {
        try
        {
            return ChangedFiles.ResolveSince(root, commit);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The verdict as printed in the console summary.</summary>
    internal static string DescribeForSummary(ReviewVerdict verdict)
    {
        try
        {
            return Describe(verdict);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The exit code already carries the invalid-verdict signal (2). Returning a
            // readable summary keeps Finish and the exit code reachable instead of crashing.
            return $"INVALID VERDICT ({verdict})";
        }
    }

    private static string Describe(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Approve => "APPROVE",
        ReviewVerdict.RequestChanges => "REQUEST CHANGES",
        _ => throw new ArgumentOutOfRangeException(
            nameof(verdict),
            verdict,
            "The review verdict is not a defined value.")
    };

    /// <summary>Reports every path-taking argument given an empty or blank value; true when there was one.</summary>
    private static bool RefuseEmptyValues(ReviewVerdictSettings settings, DiagnosticReport report)
    {
        (string Option, string? Value)[] values =
        [
            ("[path]", settings.Path), ("--config", settings.Config), ("--seats", settings.Seats),
            ("--findings", settings.Findings), ("--gates", settings.Gates), ("--prior", settings.Prior),
            ("--out", settings.Out)
        ];

        bool refused = false;
        foreach ((string option, string? value) in values.Where(entry => entry.Value is { } given && string.IsNullOrWhiteSpace(given)))
        {
            refused = true;
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                $"{option} was given an empty value. Name a path, or leave the option out.",
                Subject));
        }

        return refused;
    }

    private static int Fail(DiagnosticReport report, ReviewVerdictSettings settings)
    {
        CommandHelpers.Finish(report, settings, "review verdict", "Review");
        return 1;
    }

    /// <summary>
    /// The findings the verdict is computed from: the seat folder, or the deprecated findings document. A seat
    /// folder's report has no changed-path facts, because the seats do not measure the change.
    /// </summary>
    private static bool TryReadInput(
        ReviewVerdictSettings settings, ReviewPolicy policy, DiagnosticReport report, out FindingsReport? input)
    {
        input = null;
        if (settings.Seats is not null && settings.Findings is not null)
        {
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                "--seats and --findings name two sources for the same findings. Pass one of them.",
                Subject,
                Hint: "Pass --seats <DIR>, the folder of seat output files."));
            return false;
        }

        if (settings.Seats is null)
        {
            if (settings.Findings is not null)
            {
                // Hand-built findings files were the main source of refused verdicts, and a hand-built file also
                // skips the lens accounting the seat folder enforces, so its use is visible in every run.
                report.Add(new Diagnostic(
                    HandBuiltFindings,
                    Severity.Warning,
                    "--findings reads a hand-built findings file, which skips the lens coverage check.",
                    Subject,
                    Hint: "Pass --seats <DIR> with one <lens>.md per seat instead."));
            }

            return TryRead(settings.Findings, "--findings", ReviewJson.ReadFindings, report, out input);
        }

        FindingsReport? prior = null;
        if (settings.Prior is not null &&
            !TryRead(
                Path.Combine(settings.Prior, PriorFindingsFile), "--prior", ReviewJson.ReadFindings, report, out prior))
        {
            return false;
        }

        if (!TryReadSeats(settings.Seats, report, out List<SeatOutput> seats))
            return false;

        // The cycle's recorded decisions first; a reason typed on the command line wins over the recorded one.
        Dictionary<string, string> notSeated = new(
            NotSeatedRecord.Read(Path.GetDirectoryName(Path.GetFullPath(settings.Seats).TrimEnd(Path.DirectorySeparatorChar))!),
            StringComparer.Ordinal);
        foreach ((string lens, string reason) in ParseNotSeated(settings.NotSeated, report))
            notSeated[lens] = reason;
        List<LensCoverage> coverage = AccountForLenses(seats, notSeated, prior, report, settings.Candidates);
        if (report.HasErrors)
            return false;

        // A finding the fix round did not take (a minor one, by default) is carried, so the last cycle still lists it.
        List<ReviewFinding> seated = [.. seats.SelectMany(seat => seat.Findings)];
        IEnumerable<ReviewFinding> carried = (prior?.Findings ?? [])
            .Where(finding => !policy.FixRoundTakes(finding)
                              && !seated.Any(other => string.Equals(other.Id, finding.Id, StringComparison.Ordinal)));
        input = new FindingsReport(
            FindingsReport.CurrentSchema,
            [.. seated, .. carried],
            [],
            0,
            Coverage: coverage);
        return true;
    }

    /// <summary>
    /// Parses every <c>&lt;lens&gt;.md</c> in the seat folder. Every unreadable file is reported, not only the
    /// first, so one run tells the reviewer every seat to fix.
    /// </summary>
    private static bool TryReadSeats(string folder, DiagnosticReport report, out List<SeatOutput> seats)
    {
        seats = [];
        string full = Path.GetFullPath(folder);
        if (!Directory.Exists(full))
        {
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                $"Seat folder not found: {full}",
                Subject,
                full,
                Hint: "Pass --seats <DIR>, the folder the council seats wrote their <lens>.md files to."));
            return false;
        }

        // IgnoreInaccessible is off: a folder the process cannot list must be refused, not read as an empty folder
        // whose every lens then looks unaccounted for, or whose seats silently vanish.
        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(full).GetFileSystemInfos(
                "*.md",
                new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            report.Add(new Diagnostic(
                SeatUnreadable,
                Severity.Error,
                $"The seat folder {full} could not be listed ({ex.GetType().Name}).",
                Subject,
                full,
                Hint: "Give the process read and list access to the seat folder, then compute the verdict again."));
            return false;
        }

        foreach (FileSystemInfo entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            // An entry that cannot be a seat (a directory, an empty or dotted stem, a name that is not a lens id)
            // is named and left out, never parsed: its name is not a lens to attribute findings to.
            string lens = Path.GetFileNameWithoutExtension(entry.Name);
            string? notASeat = entry is not FileInfo ? "is a directory, not a seat file"
                : !LensId().IsMatch(lens) ? "is not named <lens>.md with a lowercase lens id"
                : null;
            if (notASeat is not null)
            {
                report.Add(new Diagnostic(
                    LensCoverageCode,
                    Severity.Warning,
                    $"{entry.Name} {notASeat}; it was ignored.",
                    Subject,
                    entry.FullName,
                    Hint: "Name each seat file after its lens, as listed by the code-review skill."));
                continue;
            }

            FileInfo file = (FileInfo)entry;
            if (!ReviewLensCatalogue.Names.Contains(lens, StringComparer.Ordinal)
                && !string.Equals(lens, ReviewerSeat, StringComparison.Ordinal))
            {
                report.Add(new Diagnostic(
                    LensCoverageCode,
                    Severity.Warning,
                    $"{file.Name} names no catalogue lens; its findings are kept but it covers no lens.",
                    lens,
                    file.FullName,
                    Hint: "Name each seat file after its lens, as listed by the code-review skill."));
            }

            try
            {
                seats.Add(SeatOutputParser.Parse(lens, File.ReadAllText(file.FullName)));
            }
            catch (Exception ex) when (ex is SeatOutputException or IOException or UnauthorizedAccessException)
            {
                report.Add(new Diagnostic(
                    SeatUnreadable,
                    Severity.Error,
                    $"{file.Name}: the seat output could not be read. {ex.Message}",
                    lens,
                    file.FullName,
                    Hint: "Fix the seat file so it is a YAML list of findings, or a 'NO FINDINGS:' or " +
                          "'SKIPPED:' line, then compute the verdict again."));
            }
        }

        foreach (SeatOutput seat in seats.Where(seat => seat.Kind is not SeatOutputKind.Skipped))
        {
            ReportDiscoveryGap(seat, report);
            ReportInputSitesGap(seat, report);
            if (seat.BudgetNote is { } note)
            {
                report.Add(new Diagnostic(
                    SeatBudgetReached,
                    Severity.Warning,
                    $"{seat.Lens} stopped at its budget; not examined: {note}",
                    seat.Lens,
                    Hint: "A coverage gap for the report's Council coverage, never a verdict input."));
            }
        }

        return !report.HasErrors;
    }

    /// <summary>
    /// Warns when a seat gives no evidence of how it found its code. Never a verdict input: a seat that read
    /// files directly may still be right, but the reader should know the index was not what it relied on.
    /// </summary>
    private static void ReportDiscoveryGap(SeatOutput seat, DiagnosticReport report)
    {
        // Two different reasons are a contradiction the reader should see; the verdict keeps the first.
        string[] fallbacks = [.. (seat.IndexFallbacks ?? []).Distinct(StringComparer.Ordinal)];
        if (fallbacks.Length > 1)
        {
            report.Add(new Diagnostic(
                DiscoveryGap,
                Severity.Warning,
                $"{seat.Lens}.md wrote {fallbacks.Length} different 'Index fallback:' lines " +
                $"({string.Join("; ", fallbacks.Select(value => $"'{value}'"))}); the first is kept. " +
                "The verdict does not depend on it.",
                seat.Lens,
                Hint: "Write one 'Index fallback:' line, naming the mode that applied."));
        }

        if (seat.IndexFallback is { } mode && !KnownIndexFallbackModes.Any(known =>
                mode.StartsWith(known, StringComparison.Ordinal)))
        {
            report.Add(new Diagnostic(
                DiscoveryGap,
                Severity.Warning,
                $"{seat.Lens}.md names index fallback mode '{mode}', which the seat contract does not define. " +
                "The verdict does not depend on it.",
                seat.Lens,
                Hint: $"Name one of: {string.Join(", ", KnownIndexFallbackModes)}, or leave the line out when the " +
                      "index answered."));
        }

        string? gap = seat.Discovery switch
        {
            null => "wrote no 'Discovery: codegraph N, docs N, direct reads N' line",
            { DirectReads: > 0, CodeGraph: 0 } discovery when seat.IndexFallback is null =>
                $"read {discovery.DirectReads} file(s) directly with no code index lookup and no " +
                "'Index fallback:' line saying why",
            _ => null
        };

        if (gap is null)
            return;

        report.Add(new Diagnostic(
            DiscoveryGap,
            Severity.Warning,
            $"{seat.Lens}.md {gap}. The verdict does not depend on it.",
            seat.Lens,
            Hint: "A seat looks code up through the index first, and records its counts on a Discovery line."));
    }

    /// <summary>
    /// Warns when the correctness seat reported findings or <c>NO FINDINGS</c> and wrote no readable <c>Input sites:</c>
    /// line, so a reader can see that no one recorded tracing the hostile inputs of each parse site. Never a verdict
    /// input; a <c>SKIPPED</c> seat is exempt.
    /// </summary>
    /// <remarks>
    /// Raised whenever the correctness seat reported findings or <c>NO FINDINGS</c>, not only when a code file changed: the seat folder carries no
    /// changed paths, and a seat with no input site in any file still says so as <c>0 traced, N files with none</c>.
    /// </remarks>
    private static void ReportInputSitesGap(SeatOutput seat, DiagnosticReport report)
    {
        if (!string.Equals(seat.Lens, CorrectnessLens, StringComparison.Ordinal) || seat.InputSites is not null)
            return;

        report.Add(new Diagnostic(
            InputSitesMissing,
            Severity.Warning,
            $"{seat.Lens}.md wrote no readable 'Input sites: N traced, M files with none' line, so its input-site " +
            "tracing is unrecorded. The verdict does not depend on it.",
            seat.Lens,
            Hint: "The correctness seat writes its input-site table and closes with 'Input sites:' above 'Discovery:'."));
    }

    private static Dictionary<string, string> ParseNotSeated(IReadOnlyList<string> values, DiagnosticReport report)
    {
        Dictionary<string, string> notSeated = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (!TrySplitReason(value, out string lens, out string reason)
                || !ReviewLensCatalogue.Names.Contains(lens, StringComparer.Ordinal))
            {
                report.Add(new Diagnostic(
                    LensCoverageCode,
                    Severity.Error,
                    $"--not-seated '{value}' does not name a catalogue lens and a reason.",
                    Subject,
                    Hint: $"Pass --not-seated <lens>=<reason>. The lenses are: {string.Join(", ", ReviewLensCatalogue.Names)}."));
                continue;
            }

            notSeated[lens] = reason;
        }

        return notSeated;
    }

    /// <summary>
    /// Accounts for every catalogue lens: a seat file, a stated reason it was not seated, or the previous cycle's
    /// coverage. A lens with none of these is refused, because an approval would otherwise read as covering a
    /// lens nobody ran.
    /// </summary>
    private static List<LensCoverage> AccountForLenses(
        List<SeatOutput> seats,
        Dictionary<string, string> notSeated,
        FindingsReport? prior,
        DiagnosticReport report,
        bool partial = false)
    {
        List<LensCoverage> coverage = [];
        List<string> unaccounted = [];

        foreach (string lens in ReviewLensCatalogue.Names)
        {
            SeatOutput? seat = seats.Find(candidate => string.Equals(candidate.Lens, lens, StringComparison.Ordinal));
            if (seat is not null)
            {
                coverage.Add(new LensCoverage(lens, CoverageOutcome(seat.Kind), null));
                continue;
            }

            if (notSeated.TryGetValue(lens, out string? reason))
            {
                coverage.Add(new LensCoverage(lens, "notSeated", reason));
                report.Add(new Diagnostic(
                    LensCoverageCode,
                    Severity.Info,
                    $"{lens}: not seated — {reason}",
                    lens));
                continue;
            }

            LensCoverage? carried = prior?.Coverage?
                .FirstOrDefault(entry => string.Equals(entry.Lens, lens, StringComparison.Ordinal));
            if (carried is not null)
            {
                coverage.Add(new LensCoverage(lens, carried.Outcome, "carried from the prior cycle"));
                report.Add(new Diagnostic(
                    LensCoverageCode,
                    Severity.Info,
                    $"{lens}: carried from the prior cycle ({carried.Outcome}).",
                    lens));
                continue;
            }

            unaccounted.Add(lens);
        }

        if (unaccounted.Count > 0 && partial)
        {
            report.Add(new Diagnostic(
                LensCoverageCode,
                Severity.Info,
                $"No seat file yet for: {string.Join(", ", unaccounted)}. Candidates are listed from the seats saved so far.",
                Subject));
        }
        else if (unaccounted.Count > 0)
        {
            report.Add(new Diagnostic(
                LensCoverageCode,
                Severity.Error,
                $"Lens coverage is incomplete. No seat file, --not-seated reason or prior coverage for: " +
                $"{string.Join(", ", unaccounted)}.",
                Subject,
                Hint: "Add a <lens>.md seat file for each, or pass --not-seated <lens>=<reason>, or " +
                      "--prior <DIR> when an earlier cycle covered it."));
        }

        return coverage;
    }

    private static string CoverageOutcome(SeatOutputKind kind) => kind switch
    {
        SeatOutputKind.Findings => "findings",
        SeatOutputKind.NoFindings => "noFindings",
        SeatOutputKind.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The seat outcome is not a defined value.")
    };

    /// <summary>
    /// Removes the findings the reviewer dropped, each with its reason. An id that names no finding is refused,
    /// because a drop that silently matched nothing would leave the finding it was meant for in the verdict.
    /// </summary>
    private static List<ReviewFinding> ApplyDrops(
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<string> drops,
        DiagnosticReport report,
        List<DroppedFinding> dropped)
    {
        List<ReviewFinding> kept = [.. findings];
        foreach (string drop in drops)
        {
            if (!TrySplitReason(drop, out string id, out string reason))
            {
                report.Add(new Diagnostic(
                    FindingDropped,
                    Severity.Error,
                    $"--drop '{drop}' does not name a finding id and a reason.",
                    Subject,
                    Hint: "Pass --drop <id>=<reason>."));
                continue;
            }

            List<ReviewFinding> matched = kept.FindAll(finding => string.Equals(finding.Id, id, StringComparison.Ordinal));
            if (matched.Count == 0)
            {
                report.Add(new Diagnostic(
                    FindingDropped,
                    Severity.Error,
                    $"--drop names no finding: '{id}'.",
                    Subject,
                    Hint: $"The finding ids are: {string.Join(", ", kept.Select(finding => finding.Id).Distinct(StringComparer.Ordinal))}."));
                continue;
            }

            foreach (ReviewFinding finding in matched)
            {
                kept.Remove(finding);
                dropped.Add(new DroppedFinding(finding, FindingDropped, reason));
                report.Add(new Diagnostic(
                    FindingDropped,
                    Severity.Info,
                    $"'{finding.Id}' was dropped by the reviewer: {reason}",
                    finding.Lens,
                    finding.File,
                    StartLine: finding.Line));
            }
        }

        return kept;
    }

    private static List<ReviewFinding> CheckCitedLines(
        string root,
        List<ReviewFinding> findings,
        DiagnosticReport report,
        List<DroppedFinding> dropped)
    {
        List<ReviewFinding> kept = new(findings.Count);
        IReadOnlyList<CitedLineResult> results = CitedLineCheck.Check(root, findings);
        foreach (CitedLineResult result in results)
        {
            ReviewFinding finding = result.Finding;
            switch (result.Kind)
            {
                case CitedLineKind.Relocated:
                    kept.Add(result.Finding);
                    report.Add(new Diagnostic(
                        CitedLine,
                        Severity.Info,
                        $"'{finding.Id}' cited line {result.OriginalLine}, which is not a line of the file; it was moved " +
                        $"to line {result.Finding.Line}, the one line holding its excerpt.",
                        finding.Lens,
                        finding.File,
                        StartLine: result.Finding.Line));
                    break;
                case CitedLineKind.Unresolved:
                    dropped.Add(new DroppedFinding(finding, CitedLine, result.Reason ?? "the cited line does not exist"));
                    report.Add(new Diagnostic(
                        CitedLine,
                        Severity.Warning,
                        $"'{finding.Id}' was dropped: {result.Reason}.",
                        finding.Lens,
                        finding.File,
                        "Re-run the seat, or cite the line the excerpt is on."));
                    break;
                default:
                    kept.Add(result.Finding);
                    break;
            }
        }

        // One line per file, not per finding: a seat that cites one missing file many times has one problem.
        foreach (IGrouping<string, CitedLineResult> file in results
                     .Where(result => result.Kind == CitedLineKind.Unverified)
                     .GroupBy(result => result.Finding.File, StringComparer.Ordinal))
        {
            CitedLineResult first = file.First();
            report.Add(new Diagnostic(
                CitedLine,
                Severity.Info,
                $"The cited lines of {string.Join(", ", file.Select(result => $"'{result.Finding.Id}'"))} were not " +
                $"checked: {first.Reason}.",
                first.Finding.Lens,
                Hint: "Check the cited file by hand, or run the verdict from the repository the seats reviewed."));
        }

        return kept;
    }

    /// <summary>
    /// Points the verdict at the review's newest cycle folder, so the reviewer types no path: its seats, its gate report
    /// when the gate run wrote one, its <c>findings.json</c> as <c>--out</c> for a verdict run, and the cycle before it
    /// as <c>--prior</c> when that one wrote its findings. An option given explicitly is kept.
    /// </summary>
    private static bool UseCurrentCycle(string root, ReviewVerdictSettings settings, DiagnosticReport report)
    {
        string tree = ReviewWorkTree.Of(root);
        string scratchpad = ReviewWorkTree.Scratchpad(tree);
        string? cycle = null;
        string? problem = null;
        if (ReviewCycleView.TryChooseTask(tree, scratchpad, settings.Task, out string task, out _, out problem))
        {
            cycle = NotSeatedRecord.LatestCycle(Path.Combine(scratchpad, task, "review"));
            problem = cycle is null ? $"no cycle folder with seats under {Path.Combine(scratchpad, task, "review")}" : null;
        }

        if (cycle is null)
        {
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                $"No findings were given and no review cycle was found: {problem}.",
                Subject,
                Hint: "Run kyber-weave review seats first, or pass --seats <DIR>."));
            return false;
        }

        settings.Seats = Path.Combine(cycle, "seats");
        string gates = Path.Combine(cycle, "gates.json");
        if (settings.Gates is null && File.Exists(gates))
            settings.Gates = gates;
        if (!settings.Candidates)
            settings.Out ??= Path.Combine(cycle, PriorFindingsFile);
        // The prior is the newest earlier cycle that belongs to this change, by the same test review seats applies, so a
        // folder left by another change is never taken.
        if (settings.Prior is null
            && ReviewCycleView.TryFromOutPath(Path.Combine(cycle, PriorFindingsFile), out string reviewFolder, out int number)
            && ReviewWorkTree.Prior(tree, settings.BaseRef, ReviewCycles.ReadFinished(reviewFolder)).Valid
                .Where(record => record.Number < number && File.Exists(record.FindingsPath))
                .MaxBy(record => record.Number) is { } prior)
        {
            settings.Prior = prior.Folder;
        }

        return true;
    }

    /// <summary>The cycle's decision-model answer file, beside the seat folder; none for a hand-built findings file.</summary>
    private static string? DecisionCachePath(ReviewVerdictSettings settings) =>
        settings.Seats is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.Seats).TrimEnd(Path.DirectorySeparatorChar))!, "decision-model.json");

    /// <summary>
    /// Drops each finding a lens raised as a nitpick when the fix level is below <c>nitpick</c>, with a reported
    /// reason: a nitpick is never fixed unless the level asks for it, and the report is the only place it is listed.
    /// </summary>
    private static List<ReviewFinding> DropNitpicksBelowTheFixLevel(
        List<ReviewFinding> findings, ReviewPolicy policy, DiagnosticReport report, List<DroppedFinding> dropped)
    {
        if (policy.FixLevel == ReviewFixLevel.Nitpick)
            return findings;

        List<ReviewFinding> kept = [];
        foreach (ReviewFinding finding in findings)
        {
            if (finding.Severity != ReviewSeverity.Nitpick)
            {
                kept.Add(finding);
                continue;
            }

            dropped.Add(new DroppedFinding(
                finding, ReviewVerdictDecisions.Code, $"nitpick: below the fix level ({policy.FixLevelName})"));
            report.Add(new Diagnostic(
                ReviewVerdictDecisions.Code,
                Severity.Info,
                $"Dropped as nitpick (fix level {policy.FixLevelName}): '{finding.Id}'",
                finding.Lens,
                finding.File,
                StartLine: finding.Line));
        }

        return kept;
    }

    /// <summary>
    /// Merges the findings the decision model confidently calls one defect, then, for a verdict run, drops the minor
    /// findings it confidently calls nitpicks. Each change is reported and kept in the dropped list with its reason, so
    /// nothing leaves the verdict silently. With no model configured nothing is asked and nothing changes.
    /// </summary>
    private static List<ReviewFinding> ApplyDecisionModel(
        ReviewDecisionModel model,
        List<ReviewFinding> findings,
        bool candidatesOnly,
        bool keepNitpicks,
        DiagnosticReport report,
        List<DroppedFinding> dropped,
        List<string> effects,
        CancellationToken cancellationToken)
    {
        if (!model.Configured)
            return findings;

        // The reviewer's drops take part in the merge, so a copy of a finding the reviewer dropped (refuted, say) goes
        // with it instead of coming back on its own.
        ReviewFinding[] reviewerDropped = [.. dropped.Where(entry => entry.Code == FindingDropped).Select(entry => entry.Finding)];
        (List<ReviewFinding> kept, List<MergedFindings> merged) = ReviewVerdictDecisions
            .MergeDuplicatesAsync(model, [.. findings, .. reviewerDropped], cancellationToken).GetAwaiter().GetResult();
        kept.RemoveAll(finding => reviewerDropped.Contains(finding));
        foreach (MergedFindings group in merged)
        {
            ReviewFinding? droppedMember = reviewerDropped.Contains(group.Kept)
                ? group.Kept
                : group.Merged.Select(item => item.Finding).FirstOrDefault(reviewerDropped.Contains);
            foreach ((ReviewFinding finding, double? confidence) in group.Merged.Prepend((group.Kept, null)))
            {
                if (reviewerDropped.Contains(finding))
                    continue;
                if (droppedMember is not null)
                {
                    kept.Remove(finding);
                    dropped.Add(new DroppedFinding(
                        finding, ReviewVerdictDecisions.Code, $"decision model: duplicate of '{droppedMember.Id}', which was dropped"));
                }
                else if (!ReferenceEquals(finding, group.Kept))
                {
                    dropped.Add(new DroppedFinding(
                        finding, ReviewVerdictDecisions.Code, $"decision model: duplicate of {group.Kept.Id} (confidence {ReviewDecisionModel.Format(confidence)})"));
                }
            }

            report.Add(new Diagnostic(
                ReviewVerdictDecisions.Code,
                Severity.Info,
                droppedMember is not null
                    ? $"Merged as duplicates (decision model): {string.Join(", ", group.Merged.Select(item => item.Finding).Prepend(group.Kept).Where(finding => !ReferenceEquals(finding, droppedMember)).Select(finding => $"'{finding.Id}'"))} " +
                      $"dropped with '{droppedMember.Id}', which the reviewer dropped"
                    : $"Merged as duplicates (decision model): kept '{group.Kept.Id}'; merged " +
                string.Join(", ", group.Merged.Select(item => $"'{item.Finding.Id}' (confidence {ReviewDecisionModel.Format(item.Confidence)})")),
                group.Kept.Lens,
                group.Kept.File,
                StartLine: group.Kept.Line));
        }

        int mergedCount = merged.Sum(group => group.Merged.Count);
        if (mergedCount > 0)
            effects.Add($"{mergedCount} duplicate(s) merged");
        if (candidatesOnly || keepNitpicks)
            return kept;

        (List<ReviewFinding> substantive, List<(ReviewFinding Finding, double? Confidence)> nitpicks) =
            ReviewVerdictDecisions.DropNitpicksAsync(model, kept, cancellationToken).GetAwaiter().GetResult();
        foreach ((ReviewFinding finding, double? confidence) in nitpicks)
        {
            string reason = $"decision model: nitpick (confidence {ReviewDecisionModel.Format(confidence)})";
            dropped.Add(new DroppedFinding(finding, ReviewVerdictDecisions.Code, reason));
            report.Add(new Diagnostic(
                ReviewVerdictDecisions.Code,
                Severity.Info,
                $"Dropped as nitpick (decision model): '{finding.Id}' (confidence {ReviewDecisionModel.Format(confidence)})",
                finding.Lens,
                finding.File,
                StartLine: finding.Line));
        }

        if (nitpicks.Count > 0)
            effects.Add($"{nitpicks.Count} nitpick(s) dropped");
        return substantive;
    }

    /// <summary>
    /// Notes each accepted finding whose stated severity the decision model confidently finds out of line with its
    /// stated impact. Advisory: the verdict was already computed on the seat's severity and does not change.
    /// </summary>
    private static void ReportSeverityNotes(
        ReviewDecisionModel model,
        IReadOnlyList<ReviewFinding> accepted,
        DiagnosticReport report,
        List<string> effects,
        CancellationToken cancellationToken)
    {
        if (!model.Configured)
            return;

        IReadOnlyList<(ReviewFinding Finding, string Direction, double? Confidence)> notes =
            ReviewVerdictDecisions.CheckSeveritiesAsync(model, accepted, cancellationToken).GetAwaiter().GetResult();
        foreach ((ReviewFinding finding, string direction, double? confidence) in notes)
        {
            report.Add(new Diagnostic(
                ReviewVerdictDecisions.Code,
                Severity.Info,
                $"'{finding.Id}': decision model: severity looks {direction} for the stated impact (confidence " +
                $"{ReviewDecisionModel.Format(confidence)}). Advisory; the verdict keeps '{ReviewVerdictDecisions.SeverityName(finding.Severity)}'.",
                finding.Lens,
                finding.File,
                StartLine: finding.Line,
                Hint: "Re-grade with a one-line reason in the report if you agree; otherwise ignore."));
        }

        if (notes.Count > 0)
            effects.Add($"{notes.Count} severity note(s)");
    }

    /// <summary>
    /// Lists the accepted critical and major findings, the ones worth a refutation run before the verdict is
    /// acted on. Omitted when a blocking gate that ran already failed, because the change goes back either way.
    /// With a decision model configured, a finding whose cited code it confidently found to support the claim is
    /// reported as confirmed and left out of the list, so no refuter is sent for it.
    /// </summary>
    private static void ReportRefutationCandidates(
        string root,
        ReviewDecisionModel model,
        ReviewOutcome outcome,
        IReadOnlyList<GateResult> gates,
        DiagnosticReport report,
        List<string> effects,
        CancellationToken cancellationToken)
    {
        bool blockingGateFailed = gates.Any(gate =>
            gate is { Blocking: true, Passed: false, NotApplicableReason: null, Outcome: GateOutcome.Completed });
        if (blockingGateFailed)
            return;

        ReviewFinding[] candidates = [.. outcome.Accepted
            .Where(finding => finding.Severity is ReviewSeverity.Critical or ReviewSeverity.Major)];
        string[] ids = [.. candidates.Select(finding => finding.Id)];
        if (model.Configured && candidates.Length > 0)
        {
            IReadOnlyList<ClaimCheck> checks =
                ReviewVerdictDecisions.CheckClaimsAsync(model, root, candidates, cancellationToken).GetAwaiter().GetResult();
            foreach (ClaimCheck check in checks.Where(check => check.Note is not null))
            {
                report.Add(new Diagnostic(
                    ReviewVerdictDecisions.Code,
                    Severity.Info,
                    check.NeedsRefuter
                        ? $"'{check.Finding.Id}': claim check (decision model): {check.Note}."
                        : $"Claim confirmed without refutation (decision model): '{check.Finding.Id}': {check.Note}.",
                    check.Finding.Lens,
                    check.Finding.File,
                    StartLine: check.Finding.Line));
            }

            int confirmed = checks.Count(check => !check.NeedsRefuter);
            if (confirmed > 0)
                effects.Add($"{confirmed} claim(s) confirmed without refutation");
            ids = [.. checks.Where(check => check.NeedsRefuter).Select(check => check.Finding.Id)];
        }

        if (ids.Length == 0)
            return;

        report.Add(new Diagnostic(
            RefutationCandidates,
            Severity.Info,
            $"Accepted critical and major findings: {string.Join(", ", ids)}.",
            Subject,
            Hint: "Put each to a refutation run before acting on the verdict."));
    }

    /// <summary>Splits <c>key=reason</c> at the first equals sign; both parts must be non-blank.</summary>
    private static bool TrySplitReason(string value, out string key, out string reason)
    {
        int equals = value.IndexOf('=', StringComparison.Ordinal);
        key = equals > 0 ? value[..equals].Trim() : string.Empty;
        reason = equals > 0 ? value[(equals + 1)..].Trim() : string.Empty;
        return key.Length > 0 && reason.Length > 0;
    }

    private static bool TryRead<T>(
        string? path,
        string option,
        Func<string, T> read,
        DiagnosticReport report,
        out T? value)
        where T : class
    {
        value = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                option == "--findings"
                    ? "No findings were given. Pass --seats <DIR>, the folder of seat output files."
                    : $"No document was given for {option}. Pass {option} <path>.",
                Subject));
            return false;
        }

        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                $"Input document not found: {full}",
                Subject,
                full));
            return false;
        }

        try
        {
            value = read(File.ReadAllText(full));
            return true;
        }
        // An empty or malformed document can surface as a JSON error, an argument error (a blank document), or an
        // unsupported-shape error from the serializer; each is the document being unreadable, never a crash.
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                $"Could not read '{full}': {ex.Message}",
                Subject,
                full));
            return false;
        }
    }

    /// <summary>A lens id as the catalogue writes them: lowercase words joined by single hyphens.</summary>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LensId();
}
