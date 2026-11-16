using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Data.Docs.FileSystem;

/// <summary>Runs child processes through the kernel's deadlock-safe runner, killing the process tree when the limit passes.</summary>
public sealed class KernelBoundedProcessExecutor : IBoundedProcessExecutor
{
    /// <inheritdoc />
    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput, TimeSpan timeout) =>
        ProcessRunner.Run(startInfo, standardInput, timeout);
}
