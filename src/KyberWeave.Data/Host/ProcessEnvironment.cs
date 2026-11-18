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
    /// The name rule of this host's default volume. It matches the rule <see cref="Paths.PhysicalPathResolver" />
    /// takes when the caller does not pass one: Linux volumes compare names case-sensitively, Windows and macOS
    /// volumes do not.
    /// </remarks>
    public bool IsFileSystemCaseInsensitive => OperatingSystem is not OperatingSystemKind.Linux;

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
