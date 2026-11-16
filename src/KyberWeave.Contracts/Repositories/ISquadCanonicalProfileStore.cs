using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Keeps a snapshot of the canonical model profiles of each installed Squad version.</summary>
public interface ISquadCanonicalProfileStore
{
    /// <summary>Writes the snapshot of a version.</summary>
    void Write(SquadCanonicalProfiles snapshot);

    /// <summary>Reads the snapshot of a version, or null when none was kept.</summary>
    SquadCanonicalProfiles? TryRead(string version);

    /// <summary>Reads the newest snapshot, or null when none was kept.</summary>
    SquadCanonicalProfiles? TryReadLatest();
}
