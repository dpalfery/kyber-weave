using System.Text.Json.Nodes;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>The decision documents several Arbiter surfaces share, so one harness's deny shape is written once.</summary>
internal static class HookDecisionDocuments
{
    /// <summary>
    /// The <c>PreToolUse</c> deny Claude Code and Factory both read: a <c>hookSpecificOutput</c> object carrying the
    /// event name, the decision and its reason. A plain allow writes nothing, because an explicit allow would
    /// auto-approve the call.
    /// </summary>
    internal static string PreToolUsePermissionDeny(string reason) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            },
        }.ToJsonString();
}
