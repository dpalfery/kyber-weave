namespace KyberWeave.Core.Squad.Model;

/// <summary>A version-keyed snapshot of the canonical Squad model profiles and agent roster.</summary>
/// <param name="SquadVersion">The Squad release version the snapshot was rendered from, without a leading <c>v</c>.</param>
/// <param name="Profiles">The canonical model profiles, mirroring the <c>models.yml</c> shape.</param>
/// <param name="AgentProfiles">The canonical agent name to profile category map.</param>
/// <remarks>
/// The snapshot lets the preset verbs resolve canonical profiles on an installed CLI, where no
/// checkout exists to load <c>products/kyber-squad</c> from. Install and update persist it after a
/// committed transaction; the preset verbs read it back without ever downloading a release.
/// </remarks>
public sealed record SquadCanonicalProfiles(
    string SquadVersion,
    SquadModelProfiles Profiles,
    IReadOnlyDictionary<string, string> AgentProfiles);
