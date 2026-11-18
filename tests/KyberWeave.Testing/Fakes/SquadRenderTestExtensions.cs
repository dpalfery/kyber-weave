using KyberWeave.Testing.Squad;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Tests.Fakes;

/// <summary>Prepares render requests and builds registries the way the host does, reading the disk inputs a render needs.</summary>
internal static class SquadRenderTestExtensions
{
    /// <summary>Reads the render's disk inputs onto the request, as the registry does before it renders.</summary>
    public static SquadRenderRequest Prepared(this SquadRenderRequest request) =>
        RealSquadServices.RenderInputReader().Prepare(request);
}

/// <summary>Builds a registry that prepares each request it renders.</summary>
internal static class SquadTestRegistry
{
    /// <summary>Creates a registry over the renderers that reads the disk inputs a render needs.</summary>
    public static SquadRendererRegistry Create(IEnumerable<ISquadRenderer> renderers) =>
        new(renderers, RealSquadServices.RenderInputReader());
}

/// <summary>Prepares render requests from an <see cref="KyberWeave.Tests.Fixtures.ArbiterSquadFixture"/>'s own file system.</summary>
internal static class ArbiterSquadFixtureExtensions
{
    /// <summary>Reads the render's inputs onto the request from the fixture's file system.</summary>
    /// <param name="request">The request to prepare.</param>
    /// <param name="fixture">The fixture whose file system holds the Squad tree.</param>
    public static SquadRenderRequest Prepared(this SquadRenderRequest request, KyberWeave.Tests.Fixtures.ArbiterSquadFixture fixture) =>
        fixture.Prepare(request);
}
