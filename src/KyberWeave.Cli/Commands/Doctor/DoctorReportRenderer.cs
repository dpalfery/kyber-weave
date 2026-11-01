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
                    mode = report.Mode == DoctorMode.Provision ? "provision" : "check",
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
                Console.Out.WriteLine($"PASS {check.Id} {check.Reason}".TrimEnd());
                continue;
            }

            Console.Out.WriteLine($"FAIL {check.Id} {check.Reason}");
            Console.Out.WriteLine($"  fix: {check.Fix}");
        }
    }
}
