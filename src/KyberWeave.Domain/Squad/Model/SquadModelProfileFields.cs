namespace KyberWeave.Core.Squad.Model;

/// <summary>The keys a model profile may carry: its default and one model per harness.</summary>
public static class SquadModelProfileFields
{
    /// <summary>The keys a model profile may carry, compared ordinally.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "default", "codex", "cursor", "claude", "copilot", "opencode", "kilo",
        "antigravity", "warp", "factory", "pi", "zcode", "devin", "commandcode"
    };
}
