using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The one fake for <see cref="IArbiterBinaryProbe"/>: it reports the binary as present with a version a test names,
/// or as absent, without starting a process.
/// </summary>
public sealed class FakeArbiterBinaryProbe : IArbiterBinaryProbe
{
    /// <summary>Creates a probe that reports the binary present.</summary>
    public FakeArbiterBinaryProbe()
    {
    }

    /// <summary>Creates a probe that reports the binary absent for <paramref name="failureReason"/>.</summary>
    /// <param name="failureReason">Why the binary is unavailable.</param>
    public FakeArbiterBinaryProbe(string failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        FailureReason = failureReason;
    }

    /// <summary>Gets the reason the binary is unavailable, or null when it is present.</summary>
    public string? FailureReason { get; }

    /// <inheritdoc />
    public string CliVersion => "1.0.0";

    /// <inheritdoc />
    public ToolProbeResult Probe() =>
        FailureReason is null
            ? new ToolProbeResult(IsAvailable: true, Version: CliVersion, FailureReason: null)
            : new ToolProbeResult(IsAvailable: false, Version: null, FailureReason: FailureReason);
}
