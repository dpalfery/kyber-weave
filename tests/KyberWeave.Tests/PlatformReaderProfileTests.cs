using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>platform-reader</c> profile lets a research agent read and run the platform's own
/// commands, and denies every way of changing what it finds. Every declared capability has an
/// explicit decision, so each harness's permission mapping can be built for it.
/// </summary>
public sealed class PlatformReaderProfileTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly Lazy<SquadSource> Source = new(() => SquadSourceLoader.Load(ProductRoot));

    [Fact]
    public void TheProfileAllowsReadingSearchNetworkingAndCommandExecution()
    {
        SquadCapabilityProfile profile = Source.Value.CapabilityProfiles.Profiles["platform-reader"];

        Assert.Equal(SquadPermissionDecision.Allow, profile.Permissions["filesystem.read"]);
        Assert.Equal(SquadPermissionDecision.Allow, profile.Permissions["filesystem.search"]);
        Assert.Equal(SquadPermissionDecision.Allow, profile.Permissions["network.read"]);
        Assert.Equal(SquadPermissionDecision.Allow, profile.Permissions["process.execute"]);
    }

    [Fact]
    public void TheProfileDeniesWritingPublishingAndDelegation()
    {
        SquadCapabilityProfile profile = Source.Value.CapabilityProfiles.Profiles["platform-reader"];

        Assert.Equal(SquadPermissionDecision.Deny, profile.Permissions["filesystem.write"]);
        Assert.Equal(SquadPermissionDecision.Deny, profile.Permissions["network.publish"]);
        Assert.Equal(SquadPermissionDecision.Deny, profile.Permissions["delegate"]);
    }

    /// <summary>
    /// A profile with a capability left undecided cannot be rendered for a harness, because the
    /// loader refuses it. An explicit decision for every declared capability is what makes the
    /// profile renderable on all thirteen.
    /// </summary>
    [Fact]
    public void EveryDeclaredCapabilityHasADecision()
    {
        SquadCapabilityProfile profile = Source.Value.CapabilityProfiles.Profiles["platform-reader"];

        Assert.Equal(
            Source.Value.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal),
            profile.Permissions.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheProfileIsSharedAndNeedsNoTargetEnvelope()
    {
        SquadCapabilityProfile profile = Source.Value.CapabilityProfiles.Profiles["platform-reader"];

        Assert.Null(profile.Target);
    }
}
