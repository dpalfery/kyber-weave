namespace KyberWeave.Core.Squad.Rendering;

/// <summary>The tool allow-list entry and the body text of the overlay, ready to render.</summary>
public sealed record SquadOperatorChannelOverlay(string ToolEntry, string Body)
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

/// <summary>
/// The result of resolving the server name: a valid name, or the warning that turns the overlay off.
/// Exactly one of the two is set.
/// </summary>
public sealed record SquadOperatorChannelServerName(string? Name, SquadRenderWarning? Warning);
