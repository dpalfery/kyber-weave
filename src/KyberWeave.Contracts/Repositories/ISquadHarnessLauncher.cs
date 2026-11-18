using System.Diagnostics;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Prepares and starts the harness process of a Squad instance.</summary>
public interface ISquadHarnessLauncher : ISquadHarnessStartInfoBuilder
{
    /// <summary>Starts the process attached to the console and waits for it without a bound: the session ends when the person ends it.</summary>
    /// <param name="startInfo">What to start.</param>
    /// <returns>The process's exit code.</returns>
    int RunInteractive(ProcessStartInfo startInfo);
}
