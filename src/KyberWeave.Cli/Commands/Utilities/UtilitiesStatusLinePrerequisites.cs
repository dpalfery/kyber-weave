using KyberWeave.Core.Utilities.StatusLine;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>One runtime prerequisite a status-line variant declares, and how to remedy its absence.</summary>
/// <param name="Executable">The executable name doctor looks for on <c>PATH</c>.</param>
/// <param name="Hint">An actionable remediation, printed only when the executable is missing.</param>
internal sealed record UtilitiesStatusLinePrerequisite(string Executable, string Hint);

/// <summary>The prerequisites and harness binary each status-line variant declares (T0 inventory).</summary>
internal static class UtilitiesStatusLinePrerequisites
{
    /// <summary>The optional KyberDash recorder the <c>agy</c> variant hands its payload to (C8, D10).</summary>
    public const string KyberDashExecutable = "kyberdash";

    /// <summary>
    ///     The runtime prerequisites for <paramref name="target" />.
    /// </summary>
    /// <remarks>
    ///     The lists come from the plan's T0 inventory, not from guesswork: Claude's bash script needs
    ///     <c>bash</c>, <c>jq</c>, <c>git</c>, and <c>awk</c>; the <c>agy</c> Python script needs
    ///     <c>python3</c> and <c>git</c>; the Pi extension needs the Node runtime. A variant whose
    ///     runtime is missing degrades to a printed fallback rather than failing a status line, which is
    ///     why doctor reports a missing prerequisite with a remedy instead of aborting.
    /// </remarks>
    public static IReadOnlyList<UtilitiesStatusLinePrerequisite> For(StatusLineTarget target)
    {
        return target switch
        {
            StatusLineTarget.Claude =>
            [
                new UtilitiesStatusLinePrerequisite("bash",
                    "Install bash, for example 'brew install bash' on macOS or 'apt-get install bash' on Debian/Ubuntu."),
                new UtilitiesStatusLinePrerequisite("jq",
                    "Install jq, for example 'brew install jq' on macOS or 'apt-get install jq' on Debian/Ubuntu."),
                new UtilitiesStatusLinePrerequisite("git",
                    "Install git, for example 'brew install git' on macOS or 'apt-get install git' on Debian/Ubuntu."),
                new UtilitiesStatusLinePrerequisite("awk",
                    "Install awk; it ships with the base system on macOS and most Linux distributions.")
            ],
            StatusLineTarget.Agy =>
            [
                new UtilitiesStatusLinePrerequisite("python3",
                    "Install Python 3, for example 'brew install python' on macOS or 'apt-get install python3' on Debian/Ubuntu."),
                new UtilitiesStatusLinePrerequisite("git",
                    "Install git, for example 'brew install git' on macOS or 'apt-get install git' on Debian/Ubuntu.")
            ],
            StatusLineTarget.Pi =>
            [
                new UtilitiesStatusLinePrerequisite("node",
                    "Install Node.js, the Pi extension runtime, for example 'brew install node' on macOS or 'apt-get install nodejs' on Debian/Ubuntu.")
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "No prerequisite list exists for this target.")
        };
    }

    /// <summary>The harness binary doctor probes to decide whether a harness is installed.</summary>
    public static string HarnessExecutable(StatusLineTarget target)
    {
        return target switch
        {
            StatusLineTarget.Claude => "claude",
            StatusLineTarget.Agy => "agy",
            StatusLineTarget.Pi => "pi",
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "No harness executable exists for this target.")
        };
    }
}
