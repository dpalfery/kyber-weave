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
            FileInfo fileInfo = new(gatesJsonPath);
            if (!fileInfo.Exists || fileInfo.Length > 1024 * 1024)
                return null;

            return ReviewJson.ReadGates(File.ReadAllText(gatesJsonPath));
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
        DigestClaim? lastClaim = null;
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
            foreach (string word in words)
            {
                if (word.Length > ReportField.Length && word.StartsWith(ReportField, StringComparison.Ordinal))
                    reportPath = word[ReportField.Length..];
            }

            if (reportPath is not null)
                lastClaim = new DigestClaim(words[0], reportPath);
        }

        return lastClaim;
    }

    private static string? Resolve(string reportPath, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
            return null;

        if (Path.IsPathRooted(reportPath))
            return reportPath;

        // A worktree worker reports an absolute path; a shared-checkout worker reports one
        // relative to the root it dispatched from, which is the event's working directory.
        return string.IsNullOrWhiteSpace(repositoryRoot) ? null : Path.Combine(repositoryRoot, reportPath);
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
