namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Result of synchronizing harness instance links with the main harness home directory.
/// </summary>
/// <param name="Links">The active symbolic and junction links currently established for this instance.</param>
/// <param name="Warnings">
///     Warnings generated during link synchronization (e.g. preserved split files or kept unmanaged
///     files).
/// </param>
public sealed record SquadInstanceLinkSyncResult(
    IReadOnlyList<SquadLinkRecord> Links,
    IReadOnlyList<string> Warnings);
