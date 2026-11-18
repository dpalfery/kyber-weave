using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the operator channel overlay from a Squad source tree.</summary>
public interface ISquadOperatorChannelOverlayFile
{
    /// <summary>Reads the overlay file's text.</summary>
    /// <param name="sourceDirectory">The Squad source root.</param>
    /// <returns>The file's text.</returns>
    /// <exception cref="SquadRenderValidationException">The overlay is missing or cannot be read.</exception>
    string ReadText(string sourceDirectory);

    /// <summary>Reads the overlay and substitutes the validated server name for its placeholder.</summary>
    /// <param name="sourceDirectory">The Squad source root.</param>
    /// <param name="serverName">The validated server name.</param>
    /// <returns>The tool entry and body text, ready to render.</returns>
    /// <exception cref="SquadRenderValidationException">The overlay is missing or cannot be read.</exception>
    SquadOperatorChannelOverlay LoadOverlay(string sourceDirectory, string serverName);
}
