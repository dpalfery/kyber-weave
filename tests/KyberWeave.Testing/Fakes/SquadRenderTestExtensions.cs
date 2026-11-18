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
