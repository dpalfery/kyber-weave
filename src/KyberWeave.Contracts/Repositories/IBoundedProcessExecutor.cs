using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Runs one child process to completion under a time limit, killing its process tree when the limit passes.</summary>
public interface IBoundedProcessExecutor
{
    /// <summary>Runs the configured process with the complete standard input.</summary>
    /// <param name="startInfo">The process to start; its arguments are never passed through a shell.</param>
    /// <param name="standardInput">The text written to standard input.</param>
    /// <param name="timeout">The longest the process may run.</param>
    /// <returns>The exit code and both output streams.</returns>
    ProcessResult Run(ProcessStartInfo startInfo, string standardInput, TimeSpan timeout);
}
