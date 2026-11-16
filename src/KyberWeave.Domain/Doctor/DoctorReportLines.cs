namespace KyberWeave.Domain.Doctor;

/// <summary>
///     The doctor's line contract (D9): one <c>PASS</c> or <c>FAIL &lt;check-id&gt; &lt;reason&gt;</c> line per check, each
///     failure followed by one indented <c>fix:</c> line with the exact command.
/// </summary>
/// <remarks>
///     The <c>doctor</c> CLI verb and the docs server's tooling-check tool both print these lines, so a script or an
///     agent parses one form whichever route ran the check.
/// </remarks>
public static class DoctorReportLines
{
    /// <summary>The report's check lines, in check order.</summary>
    /// <param name="report">The report to render.</param>
    /// <returns>One line per passing check, and two per failing check.</returns>
    public static IReadOnlyList<string> For(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<string> lines = [];
        foreach (DoctorCheck check in report.Checks)
        {
            if (check.Passed)
            {
                lines.Add($"PASS {check.Id} {SingleLine(check.Reason)}".TrimEnd());
                continue;
            }

            // One line each, always. A reason can carry a child's first output line and a fix is a command; a line
            // break inside either would split one failing check into what reads as two, and the line contract is
            // what a script parses.
            lines.Add($"FAIL {check.Id} {SingleLine(check.Reason)}".TrimEnd());
            lines.Add($"  fix: {SingleLine(check.Fix)}".TrimEnd());
        }

        return lines;
    }

    /// <summary>Collapses line breaks so one check stays on one line, with no empty line left between them.</summary>
    private static string SingleLine(string? text) =>
        text is null
            ? string.Empty
            : string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
