using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Data.Squad.Release;

namespace KyberWeave.Application.Squad;

/// <summary>Builds the local-checkout Squad release source for the callers that run outside the container.</summary>
public static class SquadReleaseSources
{
    /// <summary>Creates the source that stages a local kyber-squad checkout in place of a download.</summary>
    /// <param name="sourceRoot">The checkout's kyber-squad source root.</param>
    /// <param name="companions">Finds the companion skills beside the checkout.</param>
    /// <returns>A source that copies the checkout.</returns>
    public static ISquadReleaseSource LocalCheckout(string sourceRoot, ISquadCompanionSkills companions) =>
        new LocalCheckoutSquadReleaseSource(sourceRoot, companions);
}
