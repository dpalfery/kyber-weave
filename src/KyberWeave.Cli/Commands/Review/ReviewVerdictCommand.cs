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
public sealed class ReviewVerdictCommand : Command<ReviewVerdictSettings>
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
        string root = Path.GetFullPath(settings.Path);

        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
            return Fail(report, settings);

        if (!TryReadInput(settings, report, out FindingsReport? input) || input is null)
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

        List<DroppedFinding> dropped = [];
        List<ReviewFinding> findings = ApplyDrops(input.Findings, settings.Drop, report, dropped);
        if (report.HasErrors)
            return Fail(report, settings);

        findings = CheckCitedLines(root, findings, report, dropped);

        IReadOnlyList<GateResult> gateResults = gates?.Gates ?? [];
        ReviewOutcome outcome = VerdictEngine.Evaluate(
            findings,
            gateResults,
            config.Review,
            DateOnly.FromDateTime(DateTime.UtcNow),
            gates?.Coverage);
        outcome = outcome with { Dropped = [.. dropped, .. outcome.Dropped] };

        report.AddRange(outcome.Diagnostics);
        ReportRefutationCandidates(outcome, gateResults, report);
        report.Add(new Diagnostic(
            Verdict,
            outcome.Verdict == ReviewVerdict.Approve ? Severity.Info : Severity.Error,
            $"{DescribeForSummary(outcome.Verdict)} — risk {outcome.Risk.ToString().ToUpperInvariant()}, " +
            $"{outcome.Accepted.Count} finding(s) accepted, {outcome.Dropped.Count} dropped.",
            Subject));

        report.AddMetric("verdict", outcome.Verdict.ToString());
        report.AddMetric("risk", outcome.Risk.ToString());
        report.AddMetric("accepted", outcome.Accepted.Count);
        report.AddMetric("dropped", outcome.Dropped.Count);

        CommandHelpers.TryWriteReport(
            settings.Out,
            ReviewJson.Write(input with { Findings = findings }),
            ReportWriteFailed,
            report);

        CommandHelpers.Finish(report, settings, "review verdict", "Review");
        return report.HasErrors && outcome.Verdict == ReviewVerdict.Approve ? 1 : outcome.ExitCode;
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

    private static int Fail(DiagnosticReport report, ReviewVerdictSettings settings)
    {
        CommandHelpers.Finish(report, settings, "review verdict", "Review");
        return 1;
    }

    /// <summary>
    /// The findings the verdict is computed from: the seat folder, or the deprecated findings document. A seat
    /// folder's report has no changed-path facts, because the seats do not measure the change.
    /// </summary>
    private static bool TryReadInput(ReviewVerdictSettings settings, DiagnosticReport report, out FindingsReport? input)
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

        Dictionary<string, string> notSeated = ParseNotSeated(settings.NotSeated, report);
        List<LensCoverage> coverage = AccountForLenses(seats, notSeated, prior, report);
        if (report.HasErrors)
            return false;

        input = new FindingsReport(
            FindingsReport.CurrentSchema,
            [.. seats.SelectMany(seat => seat.Findings)],
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

        FileInfo[] files = new DirectoryInfo(full)
            .GetFiles("*.md", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 });
        foreach (FileInfo file in files.OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            string lens = Path.GetFileNameWithoutExtension(file.Name);
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
            ReportDiscoveryGap(seat, report);

        return !report.HasErrors;
    }

    /// <summary>
    /// Warns when a seat gives no evidence of how it found its code. Never a verdict input: a seat that read
    /// files directly may still be right, but the reader should know the index was not what it relied on.
    /// </summary>
    private static void ReportDiscoveryGap(SeatOutput seat, DiagnosticReport report)
    {
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
        DiagnosticReport report)
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

        if (unaccounted.Count > 0)
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
    /// Lists the accepted critical and major findings, the ones worth a refutation run before the verdict is
    /// acted on. Omitted when a blocking gate that ran already failed, because the change goes back either way.
    /// </summary>
    private static void ReportRefutationCandidates(
        ReviewOutcome outcome,
        IReadOnlyList<GateResult> gates,
        DiagnosticReport report)
    {
        bool blockingGateFailed = gates.Any(gate =>
            gate is { Blocking: true, Passed: false, NotApplicableReason: null, Outcome: GateOutcome.Completed });
        if (blockingGateFailed)
            return;

        string[] ids = [.. outcome.Accepted
            .Where(finding => finding.Severity is ReviewSeverity.Critical or ReviewSeverity.Major)
            .Select(finding => finding.Id)];
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
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
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
}
