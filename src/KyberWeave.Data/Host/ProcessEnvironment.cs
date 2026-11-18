using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Host;

/// <summary>Reports the real process: its environment variables, its well-known directories and the platform it runs on.</summary>
public sealed class ProcessEnvironment : IProcessEnvironment
{
    /// <inheritdoc />
    public string CurrentDirectory => Environment.CurrentDirectory;

    /// <inheritdoc />
    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <inheritdoc />
    public string TempDirectory => Path.GetTempPath();

    /// <inheritdoc />
    public OperatingSystemKind OperatingSystem => CurrentOperatingSystem();

    /// <inheritdoc />
    public string UserName => Environment.UserName;

    /// <inheritdoc />
    public string MachineName => Environment.MachineName;

    /// <inheritdoc />
    /// <remarks>
    /// The default rule of this host's platform family. Windows and macOS volumes ignore case; every other family,
    /// FreeBSD and an unidentified one included, is treated as case-sensitive.
    /// </remarks>
    public bool IsFileSystemCaseInsensitive => IsCaseInsensitivePlatform(OperatingSystem);

    /// <summary>Whether a platform family's default volume compares names without regard to case.</summary>
    /// <param name="operatingSystem">The operating-system family.</param>
    /// <returns>True for Windows and macOS; false for every other family, including an unidentified one.</returns>
    /// <remarks>
    /// A fallback, not the answer: the volume's own rule is probed. On FreeBSD and other families whose disks vary,
    /// assuming insensitivity would merge names the file system keeps distinct.
    /// </remarks>
    internal static bool IsCaseInsensitivePlatform(OperatingSystemKind operatingSystem) =>
        operatingSystem is OperatingSystemKind.Windows or OperatingSystemKind.MacOS;

    /// <inheritdoc />
    public bool IsInputRedirected => Console.IsInputRedirected;

    /// <inheritdoc />
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    private static OperatingSystemKind CurrentOperatingSystem()
    {
        if (System.OperatingSystem.IsWindows())
        {
            return OperatingSystemKind.Windows;
        }

        if (System.OperatingSystem.IsMacOS())
        {
            return OperatingSystemKind.MacOS;
        }

        if (System.OperatingSystem.IsLinux())
        {
            return OperatingSystemKind.Linux;
        }

        return System.OperatingSystem.IsFreeBSD() ? OperatingSystemKind.FreeBSD : OperatingSystemKind.Unknown;
    }
}
