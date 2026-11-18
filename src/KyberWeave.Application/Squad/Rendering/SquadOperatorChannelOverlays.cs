namespace KyberWeave.Core.Squad.Rendering;

/// <summary>Builds the operator-channel overlay from the overlay file's text.</summary>
public static class SquadOperatorChannelOverlays
{
    /// <summary>Substitutes the validated server name for its placeholder in the overlay file's text.</summary>
    /// <param name="rawText">The overlay file's text.</param>
    /// <param name="serverName">The validated server name.</param>
    /// <returns>The tool entry and the body text.</returns>
    public static SquadOperatorChannelOverlay FromFileText(string rawText, string serverName)
    {
        string text = rawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace(SquadOperatorChannel.ServerPlaceholder, serverName, StringComparison.Ordinal)
            .TrimEnd();
        return new SquadOperatorChannelOverlay($"{serverName}/*", text);
    }
}
