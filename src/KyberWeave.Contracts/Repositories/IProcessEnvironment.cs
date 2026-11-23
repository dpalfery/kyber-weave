namespace KyberWeave.Contracts.Repositories;

/// <summary>The state of the running process that commands read: environment variables, the three well-known directories and the platform facts.</summary>
/// <remarks>
/// Code above the data layer asks this instead of calling <c>Environment</c>, <c>Directory.GetCurrentDirectory</c> or
/// the static <c>OperatingSystem.Is*</c> members, so a test supplies its own values without touching the real process.
/// </remarks>
public interface IProcessEnvironment
{
    /// <summary>Gets the directory the process was started in.</summary>
    string CurrentDirectory { get; }

    /// <summary>Gets the directory the application's own files are in, which is where the running program was loaded from.</summary>
    string ApplicationBaseDirectory { get; }

    /// <summary>Gets the operator's home directory, or an empty string when the platform reports none.</summary>
    string HomeDirectory { get; }

    /// <summary>Gets the directory for temporary files.</summary>
    string TempDirectory { get; }

    /// <summary>Gets the operating-system family the process runs on.</summary>
    OperatingSystemKind OperatingSystem { get; }

    /// <summary>Gets the name of the user running the process.</summary>
    string UserName { get; }

    /// <summary>Gets the name of the machine running the process.</summary>
    string MachineName { get; }

    /// <summary>Gets the identifier the operating system gave this process.</summary>
    int ProcessId { get; }

    /// <summary>Whether the file system compares names without regard to case.</summary>
    bool IsFileSystemCaseInsensitive { get; }

    /// <summary>Whether standard input is redirected, so no person is typing at the other end of it.</summary>
    bool IsInputRedirected { get; }

    /// <summary>Reads an environment variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The value, or null when the variable is not set.</returns>
    string? GetVariable(string name);
}
