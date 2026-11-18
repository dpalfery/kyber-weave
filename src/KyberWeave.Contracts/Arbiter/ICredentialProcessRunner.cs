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
}
