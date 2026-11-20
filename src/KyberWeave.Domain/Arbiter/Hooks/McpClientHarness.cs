namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Maps the name a harness sends in its MCP <c>initialize</c> request (<c>clientInfo.name</c>) to the harness token
/// the dispatch hook knows.
/// </summary>
/// <remarks>
/// <para>
/// A harness name typed by a model is a guess: a Copilot CLI session sent <c>copilot</c>, which no hook token
/// matches, so its dispatch was read as one no hook can rewrite. The name the client itself sends is the only
/// verified source.
/// </para>
/// <para>
/// A row is added only after a probe showed that client sending that name, and the comment on the row records the
/// probe: the client version and the date. A name with no row, or no name at all, has no token, and the caller
/// treats it as a dispatch no hook rewrites.
/// </para>
/// </remarks>
public static class McpClientHarness
{
    /// <summary>
    /// The token an unverified client is built for: no hook rewrites a dispatch under it, so the prompt is written
    /// to a file the conductor can name rather than being left out of the dispatch.
    /// </summary>
    public const string UnverifiedToken = "unverified";

    private static readonly Dictionary<string, string> Verified = new(StringComparer.OrdinalIgnoreCase)
    {
        // Copilot CLI 1.0.115, probed 2026-11-19.
        ["copilot-cli"] = "copilot-cli",

        // Claude Code 2.1.311, probed 2026-11-19.
        ["claude-code"] = "claude",

        // opencode 2.3.0, probed 2026-11-19.
        ["opencode"] = "opencode",
    };

    /// <summary>The hook's harness token for an MCP client name, or null when no probe has verified the name.</summary>
    /// <param name="clientName">The <c>clientInfo.name</c> a client sent in its MCP <c>initialize</c> request.</param>
    public static string? TokenFor(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return null;
        }

        return Verified.TryGetValue(clientName.Trim(), out string? token) ? token : null;
    }
}
