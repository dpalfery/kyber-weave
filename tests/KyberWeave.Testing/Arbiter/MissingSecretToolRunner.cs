using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests.Arbiter;

/// <summary>An <see cref="ICredentialProcessRunner"/> whose program is not installed: starting it fails with ENOENT, as the real runner reports it.</summary>
internal sealed class MissingSecretToolRunner : ICredentialProcessRunner
{
    /// <inheritdoc />
    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardInput);
        throw new Win32Exception(
            2,
            $"An error occurred trying to start process '{startInfo.FileName}' with working directory " +
            "'/home/runner'. No such file or directory");
    }
}
