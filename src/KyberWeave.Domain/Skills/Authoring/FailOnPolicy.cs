using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Skills.Authoring;

/// <summary>Turns a scan's <c>--fail-on</c> severity into the exit code of the run.</summary>
public static class FailOnPolicy
{
    /// <summary>The exit code for <paramref name="report"/> under the named threshold: 1 when it trips, 0 otherwise.</summary>
    /// <param name="report">The findings of the scan.</param>
    /// <param name="failOn">The <c>--fail-on</c> value.</param>
    /// <remarks>
    ///     The value is compared case-insensitively with the lower-case names, so
    ///     <c>warning</c>, <c>error</c> and <c>critical</c> match in any letter case. An unknown value
    ///     falls back to the default (critical) threshold.
    /// </remarks>
    public static int ExitCode(DiagnosticReport report, string failOn)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(failOn);

        if (string.Equals(failOn, "warning", StringComparison.OrdinalIgnoreCase))
        {
            return report.Warnings > 0 || report.HasErrors ? 1 : 0;
        }

        if (string.Equals(failOn, "error", StringComparison.OrdinalIgnoreCase))
        {
            return report.HasErrors ? 1 : 0;
        }

        return report.HasCritical ? 1 : 0;
    }
}
