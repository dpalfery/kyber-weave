using System.Text.Json.Nodes;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The block a harness still understands when even its adapter cannot render one.
/// </summary>
/// <remarks>
/// Each harness reads a different deny document, so a single shape cannot serve them
/// all: a Claude-shaped document on Cursor or a plugin shim is not a block at all.
/// Every registered harness token has an explicit entry keyed by token, matching the
/// deny its adapter's <c>RenderFailClosed</c> emits; a new harness must add one (and a
/// row in <c>LastResortBlockTests</c>) or the contract test fails instead of the new
/// token silently inheriting the fallback. Unknown tokens — null, or one the registry
/// does not serve — keep the Claude-shaped document, which is what the host used before
/// the harness was known.
/// </remarks>
internal static class LastResortBlock
{
    // Keyed by token so a new harness cannot fall through to a shape its host does not
    // read. The renderers are the deny shapes the adapters themselves emit fail-closed:
    // the pre-dispatch deny is the one shape every command harness guarantees to gate
    // with, even on a return event.
    private static readonly IReadOnlyDictionary<string, Func<string, string>> Dialects = CreateDialects();

    /// <summary>Builds the harness's deny document carrying <c>KW-ARB-HOOK-001</c>.</summary>
    public static string For(string? harness)
    {
        string reason = $"{HookCommand.FailClosedCode}: hook host failed" +
            (string.IsNullOrWhiteSpace(harness) ? "." : $" ({harness}).");

        return harness is not null && Dialects.TryGetValue(harness, out Func<string, string>? render)
            ? render!(reason)
            : HookSpecificDeny(reason);
    }

    private static IReadOnlyDictionary<string, Func<string, string>> CreateDialects()
    {
        Dictionary<string, Func<string, string>> dialects = new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = HookSpecificDeny,
            ["codex"] = HookSpecificDeny,
            ["copilot-vscode"] = HookSpecificDeny,
            ["factory"] = HookSpecificDeny,
            ["cursor"] = CursorDeny,
            ["copilot-cli"] = CliDeny,
            ["antigravity"] = DecisionDeny,
            ["devin"] = DecisionBlock,
        };

        // The shim list stays the single source for its tokens: adding a harness there
        // also gives it the plugin deny dialect here.
        foreach (string token in PluginHookAdapters.Tokens)
        {
            dialects[token] = DecisionBlock;
        }

        return dialects;
    }

    private static string HookSpecificDeny(string reason) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            },
        }.ToJsonString();

    private static string CursorDeny(string reason) =>
        new JsonObject
        {
            ["permission"] = "deny",
            ["agent_message"] = reason,
            ["user_message"] = reason,
        }.ToJsonString();

    private static string CliDeny(string reason) =>
        new JsonObject
        {
            ["permissionDecision"] = "deny",
            ["permissionDecisionReason"] = reason,
        }.ToJsonString();

    // [F12]: Devin documents only the top-level decision block; [F10]: Antigravity's
    // deny shape is decision deny (never allow); the plugin shims read decision block.
    private static string DecisionBlock(string reason) =>
        new JsonObject
        {
            ["decision"] = "block",
            ["reason"] = reason,
        }.ToJsonString();

    private static string DecisionDeny(string reason) =>
        new JsonObject
        {
            ["decision"] = "deny",
            ["reason"] = reason,
        }.ToJsonString();
}
