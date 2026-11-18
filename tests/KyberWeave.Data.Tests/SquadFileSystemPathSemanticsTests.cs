using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Pins how the deployment view of the path seam reports a root it cannot bind: every caller of
/// <see cref="IFileSystemPathSemantics.ResolvePhysicalRoot" /> handles a deployment conflict, so nothing the resolver
/// says about a bad root may escape as a different exception.
/// </summary>
public sealed class SquadFileSystemPathSemanticsTests
{
    [Fact]
    public void AnExistingRootIsTheResolversCanonicalAnswer()
    {
        FakePathResolver resolver = FakePathResolver.OverEmptyFileSystem();
        resolver.Files.CreateDirectory("/real");
        SquadFileSystemPathSemantics semantics = new(resolver);

        Assert.Equal("/real", semantics.ResolvePhysicalRoot("/real"));
    }

    [Fact]
    public void AnAbsoluteRootThatNamesNoDirectoryIsADeploymentConflict()
    {
        SquadFileSystemPathSemantics semantics = new(FakePathResolver.OverEmptyFileSystem());

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(
            () => semantics.ResolvePhysicalRoot("/missing"));

        Assert.Contains("/missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeRootIsADeploymentConflictRatherThanAReadOfTheWorkingDirectory()
    {
        SquadFileSystemPathSemantics semantics = new(FakePathResolver.OverEmptyFileSystem());

        _ = Assert.Throws<SquadDeploymentConflictException>(() => semantics.ResolvePhysicalRoot("relative/root"));
    }

    [Fact]
    public void ABlankRootStaysAnArgumentError()
    {
        SquadFileSystemPathSemantics semantics = new(FakePathResolver.OverEmptyFileSystem());

        _ = Assert.Throws<ArgumentException>(() => semantics.ResolvePhysicalRoot(" "));
    }
}
