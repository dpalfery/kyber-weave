using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Domain.Update;

namespace KyberWeave.Contracts.Services;

/// <summary>Packages the canonical Kyber-Squad source into its distribution archives. Maintainer-only: it runs from the repository root.</summary>
public interface ISquadPackService
{
    /// <summary>Validates the source and the composed tree, then writes the requested archives.</summary>
    /// <param name="request">What to pack.</param>
    /// <returns>How the run ended, with the archives it wrote.</returns>
    /// <exception cref="KyberWeave.Domain.Update.SelfUpdateException">The version is not a release tag.</exception>
    SquadPackOutcome Pack(SquadPackRequest request);
}
