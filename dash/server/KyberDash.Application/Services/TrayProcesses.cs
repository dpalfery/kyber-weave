using System.Globalization;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Pure decisions about the KyberDash tray processes: how many run, what <c>menubar --running</c>
/// reports, and whether a quit has finished before a force-kill is considered.
/// </summary>
/// <remarks>
/// The process listing itself (<c>pgrep</c>, <c>tasklist</c>) and the kill are side effects and stay
/// in <see cref="MenubarService"/>; this type only reads their output, so each rule is unit tested
/// without a real tray. Windows image names are matched case-insensitively because <c>tasklist</c>
/// prints the name as the executable was launched.
/// </remarks>
public static class TrayProcesses
{
    private const string WindowsTrayLinePrefix = "\"kyberdash-tray.exe\",";

    /// <summary>Counts the tray processes in a process listing.</summary>
    /// <param name="output">Standard output of <c>pgrep -f</c> on macOS, or of <c>tasklist /FO CSV /NH</c> on Windows.</param>
    /// <param name="windows">Whether <paramref name="output"/> is the Windows <c>tasklist</c> listing.</param>
    /// <returns>The number of tray processes listed.</returns>
    /// <remarks>
    /// On macOS every line that starts with a process id (digits, then the end of the line or whitespace) is one process;
    /// any other line is a tool warning, not a process. On Windows only lines naming <c>kyberdash-tray.exe</c>
    /// count, so the <c>INFO: No tasks are running</c> line that <c>tasklist</c> prints for no match is not
    /// mistaken for a process.
    /// </remarks>
    public static int CountTrayProcesses(string output, bool windows)
    {
        ArgumentNullException.ThrowIfNull(output);

        int count = 0;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (windows ? line.StartsWith(WindowsTrayLinePrefix, StringComparison.OrdinalIgnoreCase) : StartsWithPid(line))
            {
                count++;
            }
        }

        return count;
    }

    private static bool StartsWithPid(string line)
    {
        int digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        return digits > 0 && (digits == line.Length || char.IsWhiteSpace(line[digits]));
    }

    /// <summary>Turns a tray count into the exit code and one-line message <c>kyberdash menubar --running</c> reports.</summary>
    /// <param name="count">Number of tray processes running.</param>
    /// <returns>Exit 0 only when exactly one tray runs; otherwise exit 1 with the reason.</returns>
    public static MenubarInstallOutcome RunningOutcome(int count) => count switch
    {
        1 => new MenubarInstallOutcome(0, "kyberdash: the tray is running"),
        0 => new MenubarInstallOutcome(1, "kyberdash: no tray process running"),
        _ => new MenubarInstallOutcome(
            1,
            string.Create(CultureInfo.InvariantCulture, $"kyberdash: {count} tray processes running")),
    };

    /// <summary>Polls until no tray runs, or the timeout passes, before a caller considers a force-kill.</summary>
    /// <param name="isRunning">Checks whether a tray still runs; called once per poll.</param>
    /// <param name="delay">Waits between polls; called only between two polls, never before the first.</param>
    /// <param name="interval">Time between polls; must be positive.</param>
    /// <param name="timeout">How long the tray may take to exit after the first poll.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// <see langword="true"/> when a poll finds no tray before the timeout; <see langword="false"/> when every
    /// poll up to the timeout still finds one. The caller force-kills only on <see langword="false"/>.
    /// </returns>
    public static async Task<bool> WaitForTrayExitAsync(
        Func<CancellationToken, Task<bool>> isRunning,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan interval,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isRunning);
        ArgumentNullException.ThrowIfNull(delay);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The poll interval must be positive.");
        }

        TimeSpan elapsed = TimeSpan.Zero;
        while (true)
        {
            if (!await isRunning(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (elapsed >= timeout)
            {
                return false;
            }

            await delay(interval, cancellationToken).ConfigureAwait(false);
            elapsed += interval;
        }
    }
}
