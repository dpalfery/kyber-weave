namespace KyberWeave.Core.Squad.Rendering;

/// <summary>The tool allow-list entry and the body text of the overlay, ready to render.</summary>
public sealed record SquadOperatorChannelOverlay(string ToolEntry, string Body);

/// <summary>
/// The result of resolving the server name: a valid name, or the warning that turns the overlay off.
/// Exactly one of the two is set.
/// </summary>
public sealed record SquadOperatorChannelServerName(string? Name, SquadRenderWarning? Warning);
