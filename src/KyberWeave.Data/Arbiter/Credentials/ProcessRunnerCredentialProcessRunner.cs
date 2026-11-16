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
}
