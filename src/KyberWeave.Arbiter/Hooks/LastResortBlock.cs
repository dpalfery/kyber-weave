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
/// <para>
/// Tokens are matched case-insensitively because
/// <see cref="HarnessAdapterRegistry"/> resolves them that way. Matching here with
/// Ordinal made <c>--harness OpenCode</c> find its adapter yet fall through to the
/// Claude shape, and a plugin shim reads that as no block: a double fault that failed
/// open. Trimming keeps the same property for a token that arrived padded.
/// </para>
/// </remarks>
internal static class LastResortBlock
{
    /// <summary>Builds the harness's deny document carrying <c>KW-ARB-HOOK-001</c>.</summary>
    public static string For(string? harness)
    {
        string token = harness?.Trim() ?? string.Empty;
        string reason = $"{HookCommand.FailClosedCode}: hook host failed" +
            (token.Length == 0 ? "." : $" ({harness}).");

        if (token.Length > 0 && PluginHookAdapters.Tokens.Contains(token))
        {
            return new JsonObject
            {
                ["decision"] = "block",
                ["reason"] = reason,
            }.ToJsonString();
        }

        if (string.Equals(token, "cursor", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["permission"] = "deny",
                ["agent_message"] = reason,
                ["user_message"] = reason,
            }.ToJsonString();
        }

        if (string.Equals(token, "copilot-cli", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            }.ToJsonString();
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            },
        }.ToJsonString();
    }
}
