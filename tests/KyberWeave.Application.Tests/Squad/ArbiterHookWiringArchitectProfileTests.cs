using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// The hook wiring keeps its own copy of the architect capability profile name, because the Arbiter catalog it
/// came from is not in a layer the wiring can reference. This pins the copy to the catalog.
/// </summary>
public sealed class ArbiterHookWiringArchitectProfileTests
{
    [Fact]
    public void TheWiringsArchitectProfileIsTheArbiterCatalogsArchitectProfile()
    {
        Assert.Equal(ArbiterSquadCatalog.ArchitectCapabilityProfile, ArbiterHookWiring.ArchitectCapabilityProfile);
    }
}
