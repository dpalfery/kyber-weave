namespace KyberWeave.Core.Squad.Release;

/// <summary>The sanitized result of probing an external Squad prerequisite.</summary>
/// <param name="IsAvailable">Whether the named executable could be started.</param>
/// <param name="Version">The exact semantic version, when the output was valid.</param>
/// <param name="FailureReason">A secret-free actionable failure category.</param>
public sealed record ToolProbeResult(
    bool IsAvailable,
    string? Version,
    string? FailureReason);
