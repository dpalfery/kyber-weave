using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>
/// Pins the gates that decide whether the PATH lookup is used and whether the missing-executable warning fires, with a
/// lookup that is wired in every case so a gate cannot hide behind the lookup being absent.
/// </summary>
public sealed class SquadDocsServerCommandTests
{
    private static readonly string Found = Path.Combine(Path.GetTempPath(), "kw-bin", SquadDocsServerCommand.Executable);

    private static string? Lookup(string name) => name == SquadDocsServerCommand.Executable ? Found : null;

    private static string? Nothing(string name) => null;

    /// <summary>Only <see cref="Found"/> exists, so a lookup answer naming any other file is one that is not there.</summary>
    private static bool OnlyFoundExists(string path) => path == Found;

    [Fact]
    public void Resolve_GlobalScopeWithWiredLookup_ReturnsThePathPathNames()
    {
        Assert.Equal(Found, SquadDocsServerCommand.Resolve(SquadDeploymentScope.Global, Lookup, OnlyFoundExists));
    }

    [Theory]
    [InlineData(SquadDeploymentScope.Project)]
    [InlineData(SquadDeploymentScope.Instance)]
    public void Resolve_NonGlobalRenderScope_ReturnsNullEvenWithWiredLookup(SquadDeploymentScope scope)
    {
        Assert.Null(SquadDocsServerCommand.Resolve(scope, Lookup, OnlyFoundExists));
    }

    [Fact]
    public void Resolve_WithoutALookup_ReturnsNull()
    {
        Assert.Null(SquadDocsServerCommand.Resolve(SquadDeploymentScope.Global, null, OnlyFoundExists));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankLookupResult_ReturnsNull(string blank)
    {
        Assert.Null(SquadDocsServerCommand.Resolve(SquadDeploymentScope.Global, _ => blank, OnlyFoundExists));
    }

    /// <summary>
    /// A lookup answer that is not fully qualified would resolve against whatever directory Antigravity starts in, so
    /// it is treated as not found even when a file of that name exists.
    /// </summary>
    [Theory]
    [InlineData("kyber-weave-mcp")]
    [InlineData("bin/kyber-weave-mcp")]
    [InlineData("./kyber-weave-mcp")]
    public void Resolve_RelativeLookupResult_ReturnsNull(string relative)
    {
        Assert.Null(SquadDocsServerCommand.Resolve(SquadDeploymentScope.Global, _ => relative, _ => true));
    }

    /// <summary>A fully qualified lookup answer naming no existing file is treated as not found.</summary>
    [Fact]
    public void Resolve_LookupResultNamingNoFile_ReturnsNull()
    {
        string elsewhere = Path.Combine(Path.GetTempPath(), "kw-elsewhere", SquadDocsServerCommand.Executable);

        Assert.Null(SquadDocsServerCommand.Resolve(SquadDeploymentScope.Global, _ => elsewhere, OnlyFoundExists));
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
