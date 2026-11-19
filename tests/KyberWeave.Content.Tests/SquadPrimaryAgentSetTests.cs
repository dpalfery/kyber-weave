using KyberWeave.Core.Squad.Model;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins the shipped roster's primary agents: the receipt assertions elsewhere select one primary by
///     name, which only says that primary exists, so this class says the whole primary set is the three
///     declared agents and that exactly one of them is not a shared identity.
/// </summary>
public sealed class SquadPrimaryAgentSetTests
{
    private static SquadSource Source => CanonicalSquadTree.SharedSource;

    [Fact]
    public void ThePrimarySetIsExactlyTheThreeDeclaredPrimaryAgents()
    {
        SquadSource source = Source;

        string[] primaries =
        [
            .. source.Agents
                .Where(agent => agent.Invocation == SquadInvocation.Primary)
                .Select(agent => agent.Name)
                .Order(StringComparer.Ordinal)
        ];

        Assert.Equal(["bug-crusher", "conductor", "product-owner"], primaries);

        // The two root wrappers are declared shared identities, so they reuse their canonical
        // skill and emit no agent principal of their own; the conductor is the only primary that
        // is not. The receipt assertions select on the conductor's name, so this is what keeps
        // that selection meaning "the only non-shared primary".
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);
        string[] nonSharedPrimaries =
        [
            .. source.Agents
                .Where(agent => agent.Invocation == SquadInvocation.Primary
                                && !sharedIdentities.Contains(agent.Name))
                .Select(agent => agent.Name)
                .Order(StringComparer.Ordinal)
        ];

        Assert.Equal(["conductor"], nonSharedPrimaries);
    }
}
