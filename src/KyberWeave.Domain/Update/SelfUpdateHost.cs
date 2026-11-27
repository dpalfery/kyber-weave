namespace KyberWeave.Domain.Update;

/// <summary>Snapshot of the running binary — where it lives, what it reports, which RID to fetch.</summary>
public sealed record SelfUpdateHost(
    string ProcessPath,
    string CurrentVersion,
    string Rid,
    bool IsWindows,
    bool IsMacOs)
{
    internal string InstallDirectory =>
        Path.GetDirectoryName(ProcessPath)
        ?? throw new SelfUpdateException("could not determine the install directory.");
}

/// <summary>What the user asked an update to do.</summary>
/// <param name="Version">The version to pin, or null for the latest.</param>
/// <param name="ReleaseCandidate">Whether to take the newest release including candidates.</param>
/// <param name="NoMcp">Skips replacing the MCP binary.</param>
/// <param name="NoKyberDash">Skips the KyberDash steps.</param>
/// <param name="NoMenubar">Skips the KyberDash tray.</param>
/// <param name="NoArbiter">Skips the arbiter binary and its hook refresh.</param>
/// <param name="FinishUpdate">Runs the finishing half of an update that the old process started.</param>
/// <param name="NoRestart">
/// Skips restarting the KyberDash engine and the tray after the binaries commit. The OpenCode capture plugin
/// step still runs, because it changes a file and does not restart anything.
/// </param>
public readonly record struct SelfUpdateOptions(
    string? Version,
    bool ReleaseCandidate,
    bool NoMcp,
    bool NoKyberDash = false,
    bool NoMenubar = false,
    bool NoArbiter = false,
    bool NoRestart = false,
    bool FinishUpdate = false);

/// <summary>How an update ended.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Message">The line to print; empty for a finishing run.</param>
public sealed record SelfUpdateOutcome(int ExitCode, string Message);
