using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Arbiter.Credentials;

/// <summary>Injected child-process seam for the macOS and Linux credential stores.</summary>
/// <remarks>
/// The stores shell out to <c>security</c> and <c>secret-tool</c> through this seam so
/// tests can assert the exact argv and stdin without those tools installed. Production
/// uses <c>ProcessRunnerCredentialProcessRunner</c>, which delegates to the kernel
/// process runner. The domain defines the port because <c>ArbiterKeyResolver</c>'s
/// resolution order reads it; the composition root picks the implementation.
/// </remarks>
public interface ICredentialProcessRunner
{
    /// <summary>Runs a child process to completion with the given stdin.</summary>
    ProcessResult Run(ProcessStartInfo startInfo, string standardInput);

    /// <summary>Runs a child process to completion with the given stdin, without blocking a thread.</summary>
    /// <param name="startInfo">The configured program; its argument list is used, never its argument string.</param>
    /// <param name="standardInput">The text written to standard input.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The exit code and both output streams.</returns>
    Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string standardInput,
        CancellationToken cancellationToken = default);
}
