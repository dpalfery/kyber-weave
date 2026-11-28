using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reports the installed <c>kyber-weave-arbiter</c> binary and the CLI version it must match.</summary>
public interface IArbiterBinaryProbe
{
    /// <summary>The version of this CLI, normalized the way the probe's own output is normalized.</summary>
    string CliVersion { get; }

    /// <summary>Runs the binary's <c>--version</c> probe without blocking a thread.</summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>Whether the binary is available, and the version its output names.</returns>
    Task<ToolProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}
