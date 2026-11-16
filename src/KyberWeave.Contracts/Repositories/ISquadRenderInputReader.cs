using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads, ahead of a render, what the render would otherwise read from disk, so the renderers stay pure.</summary>
public interface ISquadRenderInputReader
{
    /// <summary>Loads the canonical source, the worker baseline and the Copilot operator channel inputs a render needs.</summary>
    /// <param name="request">The render request.</param>
    /// <returns>
    /// The request carrying what was read. A failure to read is carried too, and surfaces when the render needs it,
    /// so a render fails where it always did. A request that already carries its inputs comes back as it is.
    /// </returns>
    SquadRenderRequest Prepare(SquadRenderRequest request);
}
