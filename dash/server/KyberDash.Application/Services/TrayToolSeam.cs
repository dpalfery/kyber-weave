using KyberDash.Core.Processes;

namespace KyberDash.Application;

/// <summary>Runs one external tool for the tray service and returns how it ended.</summary>
/// <param name="fileName">The tool to run.</param>
/// <param name="arguments">Its arguments.</param>
/// <param name="timeout">How long it may take.</param>
/// <param name="cancellationToken">Cancels the run.</param>
/// <returns>The tool's exit code and output.</returns>
public delegate Task<ProcessResult> TrayToolRunner(
    string fileName,
    string[] arguments,
    TimeSpan timeout,
    CancellationToken cancellationToken);

/// <summary>The platform the tray service decides for.</summary>
public enum TrayPlatform
{
    /// <summary>A platform the tray does not ship for.</summary>
    Other,

    /// <summary>macOS, where the tray is an application bundle.</summary>
    MacOS,

    /// <summary>Windows, where the tray is one executable.</summary>
    Windows,
}
