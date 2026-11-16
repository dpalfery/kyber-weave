using System.Diagnostics;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Prepares and starts the harness process of a Squad instance.</summary>
public interface ISquadHarnessLauncher
{
    /// <summary>Prepares the start information for a harness instance, validating arguments and locating the executable.</summary>
    /// <param name="row">The harness instance catalog row.</param>
    /// <param name="instanceRoot">The directory holding the isolated instance configuration.</param>
    /// <param name="args">Arguments to forward to the harness.</param>
    /// <param name="getEnv">Reads an environment variable.</param>
    /// <param name="findOnPath">Resolves an executable name to its path.</param>
    /// <param name="isWindows">Whether the host operating system is Windows.</param>
    /// <returns>The configured start information.</returns>
    /// <exception cref="ArgumentException">An argument contains a dangerous character, or the harness is not ready.</exception>
    /// <exception cref="FileNotFoundException">The executable cannot be found.</exception>
    ProcessStartInfo PrepareLaunch(
        SquadInstanceRow row,
        string instanceRoot,
        IReadOnlyList<string> args,
        Func<string, string?> getEnv,
        Func<string, string?> findOnPath,
        bool isWindows);

    /// <summary>Finds an executable on the path.</summary>
    /// <param name="executableName">The executable's name, or a rooted path.</param>
    /// <param name="getEnv">Reads an environment variable; null reads the process environment.</param>
    /// <returns>The path, or null when it is not found.</returns>
    string? FindOnPath(string executableName, Func<string, string?>? getEnv = null);

    /// <summary>Starts the process attached to the console and waits for it without a bound: the session ends when the person ends it.</summary>
    /// <param name="startInfo">What to start.</param>
    /// <returns>The process's exit code.</returns>
    int RunInteractive(ProcessStartInfo startInfo);
}
