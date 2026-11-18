namespace KyberWeave.Contracts.Repositories;

/// <summary>The operating-system family a process runs on, as the environment seam reports it.</summary>
/// <remarks>
/// Code asks <see cref="IProcessEnvironment.OperatingSystem" /> instead of the static
/// <c>OperatingSystem.IsWindows</c>, <c>IsMacOS</c> and <c>IsLinux</c>, so a test can state which family it exercises.
/// </remarks>
public enum OperatingSystemKind
{
    /// <summary>The family could not be identified.</summary>
    Unknown = 0,

    /// <summary>A Microsoft Windows host.</summary>
    Windows,

    /// <summary>An Apple macOS host.</summary>
    MacOS,

    /// <summary>A Linux host.</summary>
    Linux,

    /// <summary>A FreeBSD host.</summary>
    FreeBSD,
}
