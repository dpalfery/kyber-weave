using System.Text;
using System.Text.Json;
using KyberWeave.Core.Review;

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
/// made here and the rule reads a single boolean. Every unreadable case answers absent
/// rather than allow: a claim whose evidence cannot be found is the case the rule exists
/// for, and the evaluator turns an unexpected exception on this path into an error
/// result rather than a pass.
/// </para>
/// </remarks>
public static class GateReportFacts
{
    /// <summary>The fact a returned digest's <c>TASK_GATE:</c> claim is verified into.</summary>
    public const string AgreesFact = "gate.report.agrees";

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
    public static GateReport? TryRead(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            return null;
        return TryReadFile(Path.Combine(repositoryRoot, "artifacts", "gates.json"));
    }

    /// <summary>Reads a gate report from its file path.</summary>
    /// <returns>The report, or <c>null</c> when it is missing or unreadable.</returns>
    public static GateReport? TryReadFile(string gatesJsonPath)
    {
        try
        {
            if (!File.Exists(gatesJsonPath))
                return null;

            const int maxBytes = 1024 * 1024;
            using FileStream stream = new(gatesJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maxBytes || stream.Length == 0)
                return null;

            int length = (int)stream.Length;
            byte[] buffer = new byte[length];
            stream.ReadExactly(buffer);

            string json = Encoding.UTF8.GetString(buffer);
            return ReviewJson.ReadGates(json);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException)
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
    public static ArbiterFactSet Enrich(ArbiterFactSet facts, ArbiterEvent ev)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(ev);

        return facts.Contains(AgreesFact)
            ? facts.With(AgreesFact, Agrees(ev.ToolOutput, ev.Cwd), ArbiterFactLabel.Derived)
            : facts;
    }

    /// <summary>Whether a returned digest's gate report corroborates its claimed status.</summary>
    /// <param name="returnedText">The worker's return text, digest included.</param>
    /// <param name="repositoryRoot">The root a relative report path resolves against.</param>
    /// <returns>
    /// <c>true</c> when the named report corroborates a <c>PASS</c> claim, <c>false</c> when
    /// it contradicts the claim or the claim is one no report can corroborate, and
    /// <c>null</c> when there is no claim or no report to read. Every non-true answer
    /// escalates; none of them is a pass.
    /// </returns>
    public static bool? Agrees(string? returnedText, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(returnedText))
            return null;

        DigestClaim? claim = ReadClaim(returnedText);
        if (claim is null)
            return null;

        string? path = Resolve(claim.ReportPath, repositoryRoot);
        if (path is null)
            return null;

        GateReport? report = TryReadFile(path);
        return report is null ? null : Corroborates(claim.Claimed, report);
    }

    private static DigestClaim? ReadClaim(string returnedText)
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
                    status = word;
            }

            if (reportPath is not null && status is not null)
                claims.Add(new DigestClaim(status, reportPath));
        }

        if (claims.Count == 0)
            return null;

        DigestClaim finalClaim = claims[^1];

        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Any claim for the final report path that says non-PASS invalidates a PASS on the same report.
        if (string.Equals(finalClaim.Claimed, PassedStatus, StringComparison.Ordinal) &&
            claims.Any(c => string.Equals(c.ReportPath.Replace('\\', '/'), finalClaim.ReportPath.Replace('\\', '/'), pathComparison) &&
                            !string.Equals(c.Claimed, PassedStatus, StringComparison.Ordinal)))
        {
            return new DigestClaim("FAIL", finalClaim.ReportPath);
        }

        return finalClaim;
    }

    private static string? Resolve(string reportPath, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(reportPath) || string.IsNullOrWhiteSpace(repositoryRoot))
            return null;

        try
        {
            string fullRepo = Path.GetFullPath(repositoryRoot);
            string resolved = Path.IsPathRooted(reportPath)
                ? Path.GetFullPath(reportPath)
                : Path.GetFullPath(Path.Combine(fullRepo, reportPath));

            string normalizedRepo = fullRepo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;

            StringComparison comparison = OperatingSystem.IsWindows()
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
                FileSystemInfo fsi = Directory.Exists(checkPath) ? new DirectoryInfo(checkPath) : new FileInfo(checkPath);
                if (fsi.LinkTarget is { } linkTarget)
                {
                    string target = Path.IsPathRooted(linkTarget)
                        ? Path.GetFullPath(linkTarget)
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(checkPath) ?? fullRepo, linkTarget));

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
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
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

    /// <summary>The claim a returned digest makes: its status word and its report path.</summary>
    private sealed record DigestClaim(string Claimed, string ReportPath);
}
