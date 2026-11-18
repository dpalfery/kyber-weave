using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The shipped Squad source names which agents have their shell calls checked against a documented list.</summary>
public sealed class ArbiterShellGuardContentTests
{
    [Fact]
    public void ShellGuardedAgents_ShippedSource_IsExactlyTheArchitect()
    {
        // The architect is the only agent whose Bash and PowerShell calls are checked against its documented list.
        Assert.Equal(
            new HashSet<string>(["architect"], StringComparer.Ordinal),
            ArbiterHookWiring.ShellGuardedAgents(CanonicalSquadTree.SharedSource));
    }
}
