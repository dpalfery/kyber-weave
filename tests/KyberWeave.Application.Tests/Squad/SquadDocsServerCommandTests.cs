using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>
/// Pins the gates that decide whether the PATH lookup is used and whether the missing-executable warning fires, with a
/// lookup that is wired in every case so a gate cannot hide behind the lookup being absent.
/// </summary>
public sealed class SquadDocsServerCommandTests
{
    private const string Found = "/home/me/.local/bin/kyber-weave-mcp";

    private static string? Lookup(string name) => name == SquadDocsServerCommand.Executable ? Found : null;

    private static string? Nothing(string name) => null;

    [Fact]
    public void Resolve_GlobalScopeWithWiredLookup_ReturnsThePathPathNames()
    {
        Assert.Equal(Found, SquadDocsServerCommand.Resolve(SquadDeploymentScope.Global, Lookup));
    }

    [Theory]
    [InlineData(SquadDeploymentScope.Project)]
    [InlineData(SquadDeploymentScope.Instance)]
    public void Resolve_NonGlobalRenderScope_ReturnsNullEvenWithWiredLookup(SquadDeploymentScope scope)
    {
        Assert.Null(SquadDocsServerCommand.Resolve(scope, Lookup));
    }

    [Fact]
    public void Resolve_WithoutALookup_ReturnsNull()
    {
        Assert.Null(SquadDocsServerCommand.Resolve(SquadDeploymentScope.Global, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankLookupResult_ReturnsNull(string blank)
    {
        Assert.Null(SquadDocsServerCommand.Resolve(SquadDeploymentScope.Global, _ => blank));
    }

    [Fact]
    public void MissingWarnings_GlobalAntigravityWithUnresolvedLookup_ReportsOneWarning()
    {
        IReadOnlyList<string> warnings = SquadDocsServerCommand.MissingWarnings(
            SquadDeploymentScope.Global, [SquadTarget.Antigravity], Nothing, null);

        string warning = Assert.Single(warnings);
        Assert.Contains(SquadDocsServerCommand.Executable, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingWarnings_AntigravityAmongOtherTargets_ReportsOneWarning()
    {
        IReadOnlyList<string> warnings = SquadDocsServerCommand.MissingWarnings(
            SquadDeploymentScope.Global, [SquadTarget.Claude, SquadTarget.Antigravity], Nothing, null);

        Assert.Single(warnings);
    }

    [Fact]
    public void MissingWarnings_NoAntigravityTarget_ReportsNothing()
    {
        IReadOnlyList<string> warnings = SquadDocsServerCommand.MissingWarnings(
            SquadDeploymentScope.Global, [SquadTarget.Claude, SquadTarget.Factory], Nothing, null);

        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(SquadDeploymentScope.Project)]
    [InlineData(SquadDeploymentScope.Instance)]
    public void MissingWarnings_NonGlobalRenderScope_ReportsNothing(SquadDeploymentScope scope)
    {
        IReadOnlyList<string> warnings = SquadDocsServerCommand.MissingWarnings(
            scope, [SquadTarget.Antigravity], Nothing, null);

        Assert.Empty(warnings);
    }

    [Fact]
    public void MissingWarnings_WithoutALookup_ReportsNothing()
    {
        IReadOnlyList<string> warnings = SquadDocsServerCommand.MissingWarnings(
            SquadDeploymentScope.Global, [SquadTarget.Antigravity], null, null);

        Assert.Empty(warnings);
    }

    [Fact]
    public void MissingWarnings_ResolvedPath_ReportsNothing()
    {
        IReadOnlyList<string> warnings = SquadDocsServerCommand.MissingWarnings(
            SquadDeploymentScope.Global, [SquadTarget.Antigravity], Lookup, Found);

        Assert.Empty(warnings);
    }
}
