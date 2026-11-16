using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Skills.Authoring;

/// <summary>Turns a scan's <c>--fail-on</c> severity into the exit code of the run.</summary>
public static class FailOnPolicy
{
    /// <summary>The exit code for <paramref name="report"/> under the named threshold: 1 when it trips, 0 otherwise.</summary>
    /// <param name="report">The findings of the scan.</param>
    /// <param name="failOn">The <c>--fail-on</c> value.</param>
    /// <remarks>
    ///     The value is upper-cased before it is compared with the lower-case names, so only the default
    ///     (critical) threshold is ever reached. The behaviour is pinned as it shipped; changing it changes
    ///     exit codes CI already depends on.
    /// </remarks>
    public static int ExitCode(DiagnosticReport report, string failOn)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(failOn);

        return failOn.ToUpperInvariant() switch
        {
            "warning" => report.Warnings > 0 || report.HasErrors ? 1 : 0,
            "error" => report.HasErrors ? 1 : 0,
            _ => report.HasCritical ? 1 : 0
        };
    }
}
