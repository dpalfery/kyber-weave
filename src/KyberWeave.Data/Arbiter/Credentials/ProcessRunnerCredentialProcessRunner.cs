using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Arbiter.Credentials;

/// <summary>Production <see cref="ICredentialProcessRunner"/> over <see cref="ProcessRunner"/>.</summary>
public sealed class ProcessRunnerCredentialProcessRunner : ICredentialProcessRunner
{
    /// <inheritdoc />
    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardInput);
        return ProcessRunner.Run(startInfo, standardInput);
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string standardInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardInput);
        return await ProcessRunner.RunAsync(
            startInfo,
            standardInput,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
