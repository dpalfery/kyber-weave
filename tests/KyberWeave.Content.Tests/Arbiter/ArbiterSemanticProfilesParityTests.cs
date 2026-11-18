using KyberWeave.Testing.Squad;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Squad.Parsing;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.3 (parity): <c>ArbiterSemanticProfiles.All</c> must name exactly the profile
/// keys Kyber-Squad ships in <c>products/kyber-squad/profiles/models.yml</c>. A profile added to one
/// side and not the other would let the Arbiter accept or reject a profile the squad cannot render.
/// RED: <c>ArbiterSemanticProfiles</c> does not exist yet.
/// </summary>
public sealed class ArbiterSemanticProfilesParityTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void SemanticProfiles_MatchTheSquadModelProfileKeysExactly()
    {
        string productRoot = Path.Combine(Corpus.Root, "products", "kyber-squad");
        IReadOnlyList<string> squadProfiles = [.. RealSquadServices.Load(productRoot)
            .ModelProfiles.Profiles.Keys
            .Order(StringComparer.Ordinal)];

        IReadOnlyList<string> arbiterProfiles = [.. ArbiterSemanticProfiles.All
            .Order(StringComparer.Ordinal)];

        Assert.Equal(squadProfiles, arbiterProfiles);
    }
}
