using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The guard on a Squad subagent's own tool calls, from machine facts only. It refuses a write into the
/// repository's canonical checkout while a live run works in a worktree of its own
/// (<c>KW-ARB-CHECKOUT-001</c>).
/// </summary>
/// <remarks>
/// It runs on every write tool call a harness reports, so it starts no process and calls no model: the run
/// records and the checkout's git files are all it reads. A root agent (a
/// person's own session, or the conductor) is never guarded, and neither is an agent outside the Squad
/// catalog. Any error allows, and the log says why (ADR 0072).
/// </remarks>
public static class WorkerGuard
{
    /// <summary>The rule id a stray write is refused under.</summary>
    public const string CheckoutRuleId = "KW-ARB-CHECKOUT-001";

    /// <summary>The Copilot CLI harness token.</summary>
    public const string CopilotCliHarness = "copilot-cli";

    /// <summary>The Claude Code harness token.</summary>
    public const string ClaudeHarness = "claude";

    /// <summary>Answers one tool-call event. Always returns 0: a deny rides stdout.</summary>
    /// <param name="harness">The harness token: <c>copilot-cli</c> or <c>claude</c>.</param>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <param name="stdout">Receives the deny document, and nothing for an allow.</param>
    /// <param name="log">Receives diagnostics.</param>
    /// <param name="homeDirectory">The operator's home, or null for the user profile.</param>
    public static int Run(
        string harness,
        string stdin,
        TextWriter stdout,
        TextWriter log,
        string? homeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            string home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ToolCall? call = Read(harness, stdin);
            if (call is null)
            {
                return 0;
            }

            string? reason = WriteRefusal(call, home);
            if (reason is not null)
            {
                stdout.Write(Deny(harness, reason));
            }
        }
        catch (Exception ex)
        {
            TryLog(log, $"{HookCommand.FailClosedCode}: worker guard could not decide ({OneLine(ex.Message)}); the tool call continues.");
        }

        return 0;
    }

    private static string? WriteRefusal(ToolCall call, string home) =>
        call.WritePath is null ? null : RunCheckout.StrayWriteRefusal(call.WritePath, call.Cwd ?? string.Empty, home);

    private static ToolCall? Read(string harness, string stdin)
    {
        using JsonDocument document = JsonDocument.Parse(stdin);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? cwd = Text(root, "cwd");
        if (string.Equals(harness, CopilotCliHarness, StringComparison.Ordinal))
        {
            // A subagent's call carries the runtime's subagent fields at the top level; a root call carries none.
            string? agentId = Text(root, "agentId") ?? Text(root, "agent_id");
            string? parent = Text(root, "parentAgentId") ?? Text(root, "parent_agent_id");
            string? type = Text(root, "agentType") ?? Text(root, "agent_type");
            string? tool = Text(root, "toolName");
            if (agentId is null || parent is null || SquadAgentOf(type) is not { } agent || tool is null)
            {
                return null;
            }

            JsonObject? args = ArgumentsOf(root, "toolArgs");
            string? path = tool is "edit" or "create" && args is not null ? NodeText(args, "path") : null;
            return new ToolCall(harness, agentId, agent, cwd, path);
        }

        if (string.Equals(harness, ClaudeHarness, StringComparison.Ordinal))
        {
            // Claude adds agent_id and agent_type only to a call made inside a subagent.
            string? agentId = Text(root, "agent_id");
            string? tool = Text(root, "tool_name");
            if (agentId is null || SquadAgentOf(Text(root, "agent_type")) is not { } agent || tool is null)
            {
                return null;
            }

            JsonObject? input = ArgumentsOf(root, "tool_input");
            string? path = input is null
                ? null
                : tool switch
                {
                    "Edit" or "Write" or "MultiEdit" => NodeText(input, "file_path"),
                    "NotebookEdit" => NodeText(input, "notebook_path"),
                    _ => null,
                };
            return new ToolCall(harness, agentId, agent, cwd, path);
        }

        return null;
    }

    private static ArbiterSquadAgent? SquadAgentOf(string? type) =>
        type is not null
        && !string.Equals(type, "conductor", StringComparison.OrdinalIgnoreCase)
        && ArbiterSquadCatalog.LoadEmbedded().Agents.TryGetValue(type, out ArbiterSquadAgent? agent)
            ? agent
            : null;

    private static string Deny(string harness, string reason) =>
        string.Equals(harness, ClaudeHarness, StringComparison.Ordinal)
            ? new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = reason,
                },
            }.ToJsonString()
            : new JsonObject
            {
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            }.ToJsonString();

    private static JsonObject? ArgumentsOf(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Object => JsonNode.Parse(value.GetRawText()) as JsonObject,
                JsonValueKind.String => JsonNode.Parse(value.GetString() ?? string.Empty) as JsonObject,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string? NodeText(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out JsonNode? value) && value is JsonValue scalar
        && scalar.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static void TryLog(TextWriter log, string message)
    {
        try
        {
            log.WriteLine(message);
        }
        catch (Exception)
        {
            // The log is best effort.
        }
    }

    private static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    private sealed record ToolCall(string Harness, string AgentId, ArbiterSquadAgent Agent, string? Cwd, string? WritePath);
}
