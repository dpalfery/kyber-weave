using System.Text.Json;
using KyberWeave.Core.Doctor;

namespace KyberWeave.Cli.Commands.Doctor;

/// <summary>
///     Writes a doctor report in the D9 contract: one <c>FAIL &lt;check-id&gt; &lt;reason&gt;</c> line per failure, each
///     followed by an indented <c>fix:</c> line with the exact command.
/// </summary>
/// <remarks>
///     Written to the console without folding. A redirected console folds at 80 columns, which would split a
///     copy-pastable fix command across lines and break the one thing the line exists for.
/// </remarks>
internal static class DoctorReportRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Writes the report as check lines, or as JSON when <paramref name="asJson"/> is set.</summary>
    public static void Write(DoctorReport report, bool asJson)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new
                {
                    root = report.Root,
                    mode = ModeName(report.Mode),
                    exitCode = report.ExitCode,
                    checks = report.Checks.Select(check => new
                    {
                        id = check.Id,
                        passed = check.Passed,
                        reason = check.Reason,
                        fix = check.Fix
                    })
                },
                JsonOptions));
            return;
        }

        foreach (DoctorCheck check in report.Checks)
        {
            if (check.Passed)
            {
                Console.Out.WriteLine($"PASS {check.Id} {SingleLine(check.Reason)}".TrimEnd());
                continue;
            }

            // One line each, always. A reason can carry a child's first output line and a fix is a command; a line
            // break inside either would split one failing check into what reads as two, and the line contract is
            // what a script parses.
            Console.Out.WriteLine($"FAIL {check.Id} {SingleLine(check.Reason)}".TrimEnd());
            Console.Out.WriteLine($"  fix: {SingleLine(check.Fix)}".TrimEnd());
        }
    }

    /// <summary>Collapses line breaks so one check stays on one line, with no empty line left between them.</summary>
    private static string SingleLine(string? text) =>
        text is null
            ? string.Empty
            : string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The wire name of the mode the run actually used. It matches the option that selected it.</summary>
    /// <remarks>
    ///     A report that names a mode it did not run is a report that lies about what it did, which is the one thing a
    ///     doctor report exists to prevent: an ensure run used to render as <c>"check"</c>, so a JSON consumer could
    ///     not tell a run that wrote the index from one that promised not to. Every member is named explicitly rather
    ///     than derived from the enum, so the wire contract is visible here and a new mode cannot inherit a wrong
    ///     name; a value outside the enum reports itself rather than borrowing another mode's name.
    /// </remarks>
    private static string ModeName(DoctorMode mode) =>
        mode switch
        {
            DoctorMode.Check => "check",
            DoctorMode.Provision => "provision",
            DoctorMode.Ensure => "ensure",
            _ => mode.ToString()
        };
}
