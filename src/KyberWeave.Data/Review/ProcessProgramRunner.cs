using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Data.Review;

/// <summary>Runs a review program through the kernel's deadlock-safe process runner.</summary>
public sealed class ProcessProgramRunner : IProgramRunner
{
    /// <inheritdoc />
    public ProcessResult Run(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan? timeout = null,
        long? maxOutputBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo startInfo = new(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return ProcessRunner.Run(startInfo, string.Empty, timeout, maxOutputBytes, cancellationToken);
    }
}
