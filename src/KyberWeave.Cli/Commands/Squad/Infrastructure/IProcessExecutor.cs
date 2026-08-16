using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>Injectable boundary over the repository's deadlock-safe process runner.</summary>
public interface IProcessExecutor
{
    /// <summary>Runs the configured process with the complete standard input.</summary>
    ProcessResult Run(ProcessStartInfo startInfo, string standardInput);
}

/// <summary>Production process executor backed by <see cref="ProcessRunner"/>.</summary>
public sealed class ProcessExecutor : IProcessExecutor
{
    /// <summary>The stateless shared executor.</summary>
    public static ProcessExecutor Instance { get; } = new();

    /// <summary>Initializes a new instance of the <see cref="ProcessExecutor"/> class.</summary>
    public ProcessExecutor()
    {
    }

    /// <inheritdoc />
    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput) =>
        ProcessRunner.Run(startInfo, standardInput);
}
