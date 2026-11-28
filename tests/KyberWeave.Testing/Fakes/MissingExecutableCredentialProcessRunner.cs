using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Reproduces what <c>ProcessRunner</c> raises when a credential program such as <c>secret-tool</c> is not installed:
/// <c>Process.Start</c> fails with ENOENT as a <see cref="Win32Exception"/>.
/// </summary>
internal sealed class MissingExecutableCredentialProcessRunner : ICredentialProcessRunner
{
    /// <inheritdoc/>
    public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardInput);
        throw Missing(startInfo);
    }

    /// <inheritdoc/>
    public Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string standardInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardInput);
        cancellationToken.ThrowIfCancellationRequested();
        throw Missing(startInfo);
    }

    private static Win32Exception Missing(ProcessStartInfo startInfo) =>
        new(
            2,
            $"An error occurred trying to start process '{startInfo.FileName}' with working directory " +
            "'/home/runner'. No such file or directory");
}
