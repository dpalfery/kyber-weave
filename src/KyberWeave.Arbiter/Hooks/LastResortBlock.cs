using System.Text.Json.Nodes;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The block a harness still understands when even its adapter cannot render one.
/// </summary>
/// <remarks>
/// Each harness reads a different deny document, so a single shape cannot serve them
/// all: a Claude-shaped document on Cursor or a plugin shim is not a block at all.
/// Unknown tokens get the Claude-shaped document, which is what the host used before
/// the harness was known.
/// </remarks>
internal static class LastResortBlock
{
    /// <summary>Builds the harness's deny document carrying <c>KW-ARB-HOOK-001</c>.</summary>
    public static string For(string? harness)
    {
        string reason = $"{HookCommand.FailClosedCode}: hook host failed" +
            (string.IsNullOrWhiteSpace(harness) ? "." : $" ({harness}).");

        return harness switch
        {
            "cursor" => new JsonObject
            {
                ["permission"] = "deny",
                ["agent_message"] = reason,
                ["user_message"] = reason,
            }.ToJsonString(),
            "opencode" or "pi" => new JsonObject
            {
                ["decision"] = "block",
                ["reason"] = reason,
            }.ToJsonString(),
            "copilot-cli" => new JsonObject
            {
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            }.ToJsonString(),
            _ => new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = reason,
                },
            }.ToJsonString(),
        };
    }
}
