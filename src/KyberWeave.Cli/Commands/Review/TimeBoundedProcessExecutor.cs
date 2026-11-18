using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>Runs every child through a bounded executor with one fixed time limit, so a caller that takes only a plain executor still gets the limit.</summary>
/// <param name="processes">The bounded executor that starts the child and kills it when the limit passes.</param>
/// <param name="timeout">The limit applied to every run.</param>
internal sealed class TimeBoundedProcessExecutor(IBoundedProcessExecutor processes, TimeSpan timeout) : IProcessExecutor
{
    /// <inheritdoc />
    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput) =>
        processes.Run(startInfo, standardInput, timeout);
}
