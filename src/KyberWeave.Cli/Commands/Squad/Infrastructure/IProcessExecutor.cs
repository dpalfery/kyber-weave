using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>
///     Production process executor backed by <see cref="ProcessRunner"/>. Every run is bounded: a child that
///     does not finish in time is killed with its process tree and surfaces as a <see cref="TimeoutException"/>.
/// </summary>
public sealed class ProcessExecutor : IProcessExecutor
{
    /// <summary>
    ///     The bound for a run that does not name its own. Generous for a version probe or a short verb, and
    ///     short enough that a hung child cannot stall the command that started it.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The shared executor, bounded by <see cref="DefaultTimeout"/>.</summary>
    public static ProcessExecutor Instance { get; } = new(DefaultTimeout);

    private readonly TimeSpan _timeout;

    private ProcessExecutor(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    /// <summary>Creates an executor that bounds every run at <paramref name="timeout"/>.</summary>
    /// <param name="timeout">The longest a child may run before its process tree is killed. Must be positive.</param>
    public static ProcessExecutor WithTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return new ProcessExecutor(timeout);
    }

    /// <inheritdoc />
    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput) =>
        ProcessRunner.Run(startInfo, standardInput, _timeout);
}
