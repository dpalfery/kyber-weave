using System.Reflection;
using KyberWeave.Cli.Update;

namespace KyberWeave.Cli.Composition;

/// <summary>Reads the running binary's identity. It lives in the CLI so the version is the CLI assembly's own.</summary>
internal static class SelfUpdateHostFactory
{
    /// <summary>Snapshots where the running binary lives, what it reports and which RID to fetch.</summary>
    /// <returns>The snapshot.</returns>
    /// <exception cref="SelfUpdateException">The operating system has no published RID.</exception>
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
        Assembly assembly = typeof(SelfUpdateHostFactory).Assembly;
        string? infoVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
            return infoVersion;

        Version? nameVersion = assembly.GetName().Version;
        return nameVersion?.ToString() ?? "0.0.0";
    }
}
