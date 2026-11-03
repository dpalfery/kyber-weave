using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Arbiter.Credentials;

/// <summary>Operating-system credential-store port for the Arbiter key.</summary>
/// <remarks>
/// One implementation per OS reads the entry for an endpoint origin. The key itself
/// never appears in configuration; it travels on stdin (macOS, Linux) or through the
/// native credential API (Windows), and never in argv.
/// </remarks>
public interface ICredentialStore
{
    /// <summary>Reads the stored key for <paramref name="origin"/>, or null when absent.</summary>
    string? Read(string origin);

    /// <summary>Stores <paramref name="key"/> for <paramref name="origin"/>.</summary>
    void Write(string origin, string key);
}

/// <summary>Injected child-process seam for the macOS and Linux credential stores.</summary>
/// <remarks>
/// The stores shell out to <c>security</c> and <c>secret-tool</c> through this seam so
/// tests can assert the exact argv and stdin without those tools installed. Production
/// uses <see cref="ProcessRunnerCredentialProcessRunner"/>, which delegates to
/// <see cref="ProcessRunner"/>. Core defines the port; the composition root picks the
/// implementation.
/// </remarks>
public interface ICredentialProcessRunner
{
    /// <summary>Runs a child process to completion with the given stdin.</summary>
    ProcessResult Run(ProcessStartInfo startInfo, string standardInput);
}

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
