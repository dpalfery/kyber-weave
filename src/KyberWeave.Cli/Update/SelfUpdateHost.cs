using System.Reflection;

namespace KyberWeave.Cli.Update;

/// <summary>Snapshot of the running binary — where it lives, what it reports, which RID to fetch.</summary>
internal sealed record SelfUpdateHost(
    string ProcessPath,
    string CurrentVersion,
    string Rid,
    bool IsWindows,
    bool IsMacOs)
{
    internal string InstallDirectory =>
        Path.GetDirectoryName(ProcessPath)
        ?? throw new SelfUpdateException("could not determine the install directory.");

    internal static SelfUpdateHost FromCurrentProcess()
    {
        return new SelfUpdateHost(
            Environment.ProcessPath ?? string.Empty,
            ReadCurrentVersion(),
            PlatformRid.Detect(),
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS());
    }

    private static string ReadCurrentVersion()
    {
        Assembly assembly = typeof(SelfUpdateHost).Assembly;
        string? infoVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
            return infoVersion;

        Version? nameVersion = assembly.GetName().Version;
        return nameVersion?.ToString() ?? "0.0.0";
    }
}

/// <param name="NoRestart">
/// Skips restarting the KyberDash engine and the tray after the binaries commit. The OpenCode capture plugin
/// step still runs, because it changes a file and does not restart anything.
/// </param>
internal readonly record struct SelfUpdateOptions(
    string? Version,
    bool ReleaseCandidate,
    bool NoMcp,
    bool NoKyberDash = false,
    bool NoMenubar = false,
    bool NoRestart = false);

internal sealed record SelfUpdateOutcome(int ExitCode, string Message);
