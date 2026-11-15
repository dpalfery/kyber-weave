namespace KyberWeave.Core.Arbiter;

/// <summary>The size limits on client text that an Arbiter call or command accepts.</summary>
public static class ArbiterTextLimits
{
    /// <summary>
    /// The largest client text, in characters, that one Arbiter call or comment command embeds. The MCP tools and the
    /// <c>arbiter comment</c> command both refuse anything larger, so the limit is one number everywhere.
    /// </summary>
    public const int MaxTextChars = 256 * 1024;
}
