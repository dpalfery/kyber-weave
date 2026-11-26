using System.Reflection;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Application.Update;
using KyberWeave.Domain.Update;

namespace KyberWeave.Cli.Composition;

/// <summary>Reads the running binary's identity. It lives in the CLI so the version is the CLI assembly's own.</summary>
internal static class SelfUpdateHostFactory
{
    /// <summary>Snapshots where the running binary lives, what it reports and which RID to fetch.</summary>
    /// <param name="environment">Reports the operating system the binary runs on.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="SelfUpdateException">The operating system has no published RID.</exception>
    internal static SelfUpdateHost FromCurrentProcess(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new SelfUpdateHost(
            Environment.ProcessPath ?? string.Empty,
            ReadCurrentVersion(),
            PlatformRid.Detect(environment),
            environment.OperatingSystem == OperatingSystemKind.Windows,
            environment.OperatingSystem == OperatingSystemKind.MacOS);
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
