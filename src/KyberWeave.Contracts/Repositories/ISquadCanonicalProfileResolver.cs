using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Finds the canonical model profiles the preset commands validate against.</summary>
public interface ISquadCanonicalProfileResolver
{
    /// <summary>Resolves the profiles from a checkout, then the snapshot of a version, then the newest snapshot.</summary>
    /// <param name="preferredVersion">The active lock's Squad version, or null when there is no deployment context.</param>
    /// <returns>The canonical profiles and agent roster, or null when none resolves.</returns>
    SquadCanonicalProfiles? Resolve(string? preferredVersion);
}
