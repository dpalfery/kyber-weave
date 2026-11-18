using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Host;

/// <summary>Reports the real process: its environment variables, its well-known directories and the platform it runs on.</summary>
public sealed class ProcessEnvironment : IProcessEnvironment
{
    private readonly Lazy<bool> _isFileSystemCaseInsensitive;

    /// <summary>Creates the environment for the running process, probing its temporary volume's name rule once.</summary>
    public ProcessEnvironment()
        : this(ProbeCaseInsensitivity)
    {
    }

    /// <summary>Creates the environment with a chosen case probe, so a test can state the volume's rule.</summary>
    /// <param name="caseInsensitivityProbe">Answers whether the directory it is given ignores case in names.</param>
    internal ProcessEnvironment(Func<string, bool> caseInsensitivityProbe)
    {
        ArgumentNullException.ThrowIfNull(caseInsensitivityProbe);
        _isFileSystemCaseInsensitive = new Lazy<bool>(() => caseInsensitivityProbe(TempDirectory));
    }

    /// <inheritdoc />
    public string CurrentDirectory => Environment.CurrentDirectory;

    /// <inheritdoc />
    public string ApplicationBaseDirectory => AppContext.BaseDirectory;

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
    public bool IsFileSystemCaseInsensitive => _isFileSystemCaseInsensitive.Value;

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

    /// <summary>Writes a uniquely named probe file and asks the volume whether it exists under another case.</summary>
    /// <param name="directory">The directory whose volume is being asked.</param>
    /// <returns>True when the volume ignores case; the platform default when it cannot be asked.</returns>
    private static bool ProbeCaseInsensitivity(string directory)
    {
        string leaf = "kw-case-" + Path.GetRandomFileName() + "aA";
        string probe = Path.Combine(directory, leaf);
        try
        {
            using (File.Create(probe))
            {
            }

            return File.Exists(Path.Combine(directory, leaf.ToUpperInvariant()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return IsCaseInsensitivePlatform(CurrentOperatingSystem());
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The probe could not be cleaned up; a fresh name is used next time.
            }
        }
    }
}
