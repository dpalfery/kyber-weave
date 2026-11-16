namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     The result of ensuring a harness instance is installed and synchronized.
/// </summary>
/// <param name="InstanceRoot">The absolute filesystem path of the instance root directory.</param>
/// <param name="Warnings">Any warnings generated during link synchronization or lifecycle operations.</param>
public sealed record SquadInstanceEnsureResult(
    string InstanceRoot,
    IReadOnlyList<string> Warnings);
