using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Review;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>Reads the gate report the Arbiter derives its <c>gates.report</c> fact from.</summary>
/// <remarks>
/// <para>
/// A missing or unreadable <c>artifacts/gates.json</c> is an absent fact, not a
/// failure: the change may simply predate any gate run.
/// </para>
/// <para>
/// The same reader answers <c>gate.report.agrees</c> for a returned digest: a worker's
/// <c>TASK_GATE:</c> line names the report its gate run wrote, and the report either
/// corroborates the claimed status or it does not. The comparison spans two facts — the
/// claim and the report — which the closed step-0 predicate set cannot express, so it is
/// made here and the rule reads a single boolean. Every unreadable case answers absent:
/// the rule cannot decide from the line, so it asks the model whether the return reports a
/// gate run at all, and reads the raw claim line and the report tail this class also supplies.
/// </para>
/// <para>Every file read goes through the repository file-store seam.</para>
/// </remarks>
/// <param name="store">The repository checkout the reports are read from.</param>
/// <param name="paths">Turns a report, a root or a link target into a full path.</param>
/// <param name="environment">Names the directory a relative path is resolved against, and the file system's case rule.</param>
public sealed class GateReportFacts(
    IRepositoryFileStore store,
    IPathResolver paths,
    IProcessEnvironment environment) : IGateReportFacts
{
    /// <summary>The fact a returned digest's <c>TASK_GATE:</c> claim is verified into.</summary>
    public const string AgreesFact = "gate.report.agrees";

    /// <summary>The raw <c>TASK_GATE:</c> line a return carries, kept whole for the model question.</summary>
    public const string ClaimFact = "gate.report.claim";

    /// <summary>The tail of the worker's return, where its digest closes it.</summary>
    public const string ReturnedTailFact = "delegation.returned-tail";

    /// <summary>
    /// Whether the folder of the report a returned digest claims as a pass also holds a failing-run report
    /// written before it: true when it does, false when the claimed pass has none older, absent when the
    /// claim or its report cannot be read. <c>KW-ARB-MODE-002</c> reads it as the proof of test-first.
    /// </summary>
    public const string FailingRunFirstFact = "gate.report.failing-run-first";

    /// <summary>
    /// How <c>review gates --red</c> marks a gate it accepted as a failing run: the gate is recorded as passed
    /// with this summary prefix. A report holding such a gate is a failing-run report, whatever its file name.
    /// </summary>
    public const string RedAcceptedSummaryPrefix = "RED accepted";

    /// <summary>The digest line whose claim the rule checks.</summary>
    private const string DigestMarker = "TASK_GATE:";

    /// <summary>The digest field naming the report the claimed gate run wrote.</summary>
    private const string ReportField = "report=";

    /// <summary>
    /// The one claimed status a report can corroborate. Every other status word reports
    /// that something went wrong or that nothing ran, so it is not a claim of a clean run
    /// and the report cannot back it.
    /// </summary>
    private const string PassedStatus = "PASS";

    /// <summary>Reads the gate report for a repository root.</summary>
    /// <returns>The report, or <c>null</c> when it is missing or unreadable.</returns>
    public GateReport? TryRead(string repositoryRoot)
    {
        string? fullRoot = FullRoot(repositoryRoot);
        return fullRoot is null ? null : TryReadFile(Path.Combine(fullRoot, "artifacts", "gates.json"));
    }

    /// <summary>Reads a gate report from its file path.</summary>
    /// <returns>The report, or <c>null</c> when it is missing or unreadable.</returns>
    public GateReport? TryReadFile(string gatesJsonPath)
    {
        try
        {
            if (!store.FileExists(gatesJsonPath))
                return null;

            const int maxBytes = 1024 * 1024;
            using Stream stream = store.OpenRead(gatesJsonPath);
            if (stream.Length > maxBytes || stream.Length == 0)
                return null;

            int length = (int)stream.Length;
            byte[] buffer = new byte[length];
            stream.ReadExactly(buffer);

            string json = Encoding.UTF8.GetString(buffer);
            return ReviewJson.ReadGates(json);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Merges <see cref="AgreesFact"/> into <paramref name="facts"/> when the trigger
    /// declares it, leaving every other trigger's bag untouched.
    /// </summary>
    /// <param name="facts">The fact bag built for the event's trigger.</param>
    /// <param name="ev">The event; only a return carries the text to verify.</param>
    public ArbiterFactSet Enrich(ArbiterFactSet facts, ArbiterEvent ev)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(ev);

        if (!facts.Contains(AgreesFact))
            return facts;

        facts = facts.With(AgreesFact, Agrees(ev.ToolOutput, ev.Cwd), ArbiterFactLabel.Derived);
        if (facts.Contains(FailingRunFirstFact))
            facts = facts.With(FailingRunFirstFact, FailingRunFirst(ev.ToolOutput, ev.Cwd), ArbiterFactLabel.Derived);
        if (facts.Contains(ClaimFact))
            facts = facts.With(ClaimFact, RawClaimLine(ev.ToolOutput), ArbiterFactLabel.Asserted);
        if (facts.Contains(ReturnedTailFact))
            facts = facts.With(ReturnedTailFact, TextExcerpts.ReturnTail(ev.ToolOutput), ArbiterFactLabel.Asserted);
        return facts;
    }

    /// <summary>The last line that carries <c>TASK_GATE:</c>, as written, or null when none does.</summary>
    private static string? RawClaimLine(string? returnedText)
    {
        if (string.IsNullOrWhiteSpace(returnedText))
            return null;

        return ReturnMarkerParser.Flatten(returnedText)
            .Split('\n')
            .LastOrDefault(line => line.Contains(DigestMarker, StringComparison.Ordinal))
            ?.Trim();
    }

    /// <summary>
    /// Whether the repository's gate report shows a failed gate, which corroborates a lens finding. The same
    /// report reader <see cref="Agrees(string?, string?)"/> uses, so a finding and a digest are checked against one source.
    /// </summary>
    /// <returns><c>true</c> when a gate ran and failed; <c>false</c> when the report is missing or shows none failed.</returns>
    public bool FailedGateCorroborates(string? repositoryRoot)
    {
        GateReport? report = string.IsNullOrWhiteSpace(repositoryRoot) ? null : TryRead(repositoryRoot);
        return report?.Gates is not null
            && report.Gates.Any(gate => gate is not null && !gate.Passed && !gate.NotApplicable);
    }

    /// <summary>Whether a returned digest's gate report corroborates its claimed status.</summary>
    /// <param name="returnedText">The worker's return text, digest included.</param>
    /// <param name="repositoryRoot">The root a relative report path resolves against.</param>
    /// <returns>
    /// <c>true</c> when the named report corroborates a <c>PASS</c> claim, <c>false</c> when
    /// it contradicts the claim or the claim is one no report can corroborate, and
    /// <c>null</c> when there is no claim or no report to read. False is a machine finding and
    /// escalates; null is undecidable and goes to the model question.
    /// </returns>
    public bool? Agrees(string? returnedText, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(returnedText))
            return null;

        DigestClaim? claim = ReadClaim(returnedText, repositoryRoot);
        if (claim is null)
            return null;

        string? path = Resolve(claim.ReportPath, repositoryRoot);
        if ((path is null || !store.FileExists(path)) && claim.WholeLinePath is not null)
            path = Resolve(claim.WholeLinePath, repositoryRoot) ?? path;
        if (path is null)
            return null;

        GateReport? report = TryReadFile(path);
        return report is null ? null : Corroborates(claim.Claimed, report);
    }

    /// <summary>
    /// Whether a returned digest's passing report was preceded by a failing run of the same task gate: the
    /// worker wrote its tests, ran the gate with <c>--red</c>, then implemented and ran it again. Both runs write
    /// reports, so the proof is two files in one folder, not a line the worker composes.
    /// </summary>
    /// <param name="returnedText">The worker's return text, digest included.</param>
    /// <param name="repositoryRoot">The root a relative report path resolves against.</param>
    /// <returns>
    /// <c>true</c> when the claimed report is a clean pass and a failing-run report in its folder was written
    /// before it; <c>false</c> when the claimed report is a clean pass with no older failing-run report beside it,
    /// or is itself the failing-run report; <c>null</c> when there is no claim, no report, or the claimed report
    /// is not a clean pass (<c>KW-ARB-GATE-REPORT-001</c> judges that).
    /// </returns>
    public bool? FailingRunFirst(string? returnedText, string? repositoryRoot)
    {
        if (Agrees(returnedText, repositoryRoot) is not true)
            return null;

        DigestClaim claim = ReadClaim(returnedText!, repositoryRoot)!;
        string? path = Resolve(claim.ReportPath, repositoryRoot);
        if ((path is null || !store.FileExists(path)) && claim.WholeLinePath is not null)
            path = Resolve(claim.WholeLinePath, repositoryRoot) ?? path;
        if (path is null || Path.GetDirectoryName(path) is not { } folder)
            return null;

        try
        {
            if (IsFailingRun(TryReadFile(path)))
                return false;

            DateTime passedAt = store.GetLastWriteTimeUtc(path);
            return store.EnumerateFiles(folder, "*.json", recursive: false)
                .Where(candidate => !paths.AreSame(candidate, path))
                .Any(candidate => store.GetLastWriteTimeUtc(candidate) < passedAt && IsFailingRun(TryReadFile(candidate)));
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="report"/> was written by an accepted <c>review gates --red</c> run.</summary>
    private static bool IsFailingRun(GateReport? report) =>
        report?.Gates is not null
        && report.Gates.Any(gate => gate is not null
            && gate.Passed
            && gate.Summary is not null
            && gate.Summary.StartsWith(RedAcceptedSummaryPrefix, StringComparison.Ordinal));

    /// <summary>
    /// The claim a returned digest makes. Report paths are compared after resolving them against
    /// <paramref name="repositoryRoot"/>, the base the verdict reads them against, so one report cited in a relative
    /// and in an absolute spelling is recognised as the same file.
    /// </summary>
    private DigestClaim? ReadClaim(string returnedText, string? repositoryRoot)
    {
        List<DigestClaim> claims = [];
        foreach (string line in returnedText.Split('\n'))
        {
            int marker = line.IndexOf(DigestMarker, StringComparison.Ordinal);
            if (marker < 0)
                continue;

            string[] words = line[(marker + DigestMarker.Length)..]
                .Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                continue;

            string? reportPath = null;
            string? status = null;
            foreach (string word in words)
            {
                if (word.Length > ReportField.Length && word.StartsWith(ReportField, StringComparison.Ordinal))
                    reportPath = word[ReportField.Length..];
                else if (status is null && !word.Contains('=', StringComparison.Ordinal))
                    status = ReturnMarkerParser.MarkerWord(word);
            }

            // A path with a space splits into words. The rest of the line after report= is kept as a second
            // reading, tried when the first names no file, so the space does not lose the report.
            int field = line.IndexOf(ReportField, marker, StringComparison.Ordinal);
            string? whole = field < 0
                ? null
                : line[(field + ReportField.Length)..].Trim().Trim('`', '"', '\'');
            if (reportPath is not null && status is not null)
                claims.Add(new DigestClaim(status, reportPath, whole != reportPath ? whole : null));
        }

        if (claims.Count == 0)
            return null;

        DigestClaim finalClaim = claims[^1];

        StringComparison pathComparison = environment.IsFileSystemCaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string? readBase = FullRoot(repositoryRoot);
        string finalCanonical = CanonicalReportPath(finalClaim.ReportPath, readBase);

        // Any claim for the final report path that says non-PASS invalidates a PASS on the same report.
        if (string.Equals(finalClaim.Claimed, PassedStatus, StringComparison.Ordinal) &&
            claims.Any(c => string.Equals(CanonicalReportPath(c.ReportPath, readBase), finalCanonical, pathComparison) &&
                            !string.Equals(c.Claimed, PassedStatus, StringComparison.Ordinal)))
        {
            return finalClaim with { Claimed = "FAIL" };
        }

        return finalClaim;
    }

    private string CanonicalReportPath(string path, string? readBase)
    {
        string normalized = path.Replace('\\', '/');
        try
        {
            return paths.GetFullPath(normalized, readBase ?? environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return normalized;
        }
    }

    /// <summary>The repository root as a full path, or null when the caller gave none or it cannot be resolved.</summary>
    private string? FullRoot(string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            return null;

        try
        {
            return paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private string? Resolve(string reportPath, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(reportPath) || string.IsNullOrWhiteSpace(repositoryRoot))
            return null;

        try
        {
            string fullRepo = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
            string resolved = paths.GetFullPath(reportPath, fullRepo);

            string normalizedRepo = fullRepo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;

            StringComparison comparison = environment.IsFileSystemCaseInsensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!resolved.StartsWith(normalizedRepo, comparison) &&
                !string.Equals(resolved, fullRepo, comparison))
            {
                return null;
            }

            string checkPath = resolved;
            while (checkPath.Length >= fullRepo.Length)
            {
                if (store.GetLinkTarget(checkPath) is { } linkTarget)
                {
                    string target = paths.GetFullPath(
                        linkTarget,
                        Path.GetDirectoryName(checkPath) ?? fullRepo);

                    if (!target.StartsWith(normalizedRepo, comparison) && !string.Equals(target, fullRepo, comparison))
                        return null;
                }

                string? parent = Path.GetDirectoryName(checkPath);
                if (parent is null || parent.Length == checkPath.Length)
                    break;
                checkPath = parent;
            }

            return resolved;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static bool Corroborates(string claimed, GateReport report)
    {
        // A PASS claim is the only one that asserts a clean run, so it is the only one a
        // report can back. "Clean" means at least one gate passed and none failed: a gate
        // recorded as not applicable is evidence neither way (KW-REVIEW-026), and a run
        // that skipped every gate is the NONE_APPLICABLE case, which is not a pass.
        if (!string.Equals(claimed, PassedStatus, StringComparison.Ordinal))
            return false;

        if (report.Gates is null || report.Gates.Count == 0)
            return false;

        return report.Gates.Any(gate => gate is not null && gate.Passed)
            && report.Gates.All(gate => gate is not null && (gate.Passed || gate.NotApplicable));
    }

    /// <summary>
    /// The claim a returned digest makes: its status word, its report path, and the rest of the line after
    /// <c>report=</c> when that reads differently (a path with a space).
    /// </summary>
    private sealed record DigestClaim(string Claimed, string ReportPath, string? WholeLinePath = null);
}
