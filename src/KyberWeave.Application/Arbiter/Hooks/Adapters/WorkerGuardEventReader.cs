using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Reads a harness's own tool-call payload into the worker guard's typed call. This is the adapter boundary for the
/// guard: Claude's <c>tool_name</c>/<c>tool_input</c> and Copilot CLI's <c>toolName</c>/<c>toolArgs</c> and subagent
/// fields end here, so the guard and its rules never parse a harness payload.
/// </summary>
internal static class WorkerGuardEventReader
{
    private const string CopilotCliHarness = WorkerGuardRules.CopilotCliHarness;
    private const string ClaudeHarness = WorkerGuardRules.ClaudeHarness;

    /// <summary>
    /// Reads one tool call. A root agent, an agent outside the Squad catalog, and an event with no tool are not
    /// guarded, so they read as null.
    /// </summary>
    internal static WorkerGuardRules.ToolCall? Read(string harness, string stdin)
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
            string? command = tool is "bash" or "powershell" && args is not null ? NodeText(args, "command") : null;

            // A read of a running shell's output waits on the process that shell started, so it is judged like the
            // command itself: a hung test shows up as a wait that never returns.
            bool startedProcess = tool is "bash" or "read_bash" or "powershell" or "read_powershell";
            return new WorkerGuardRules.ToolCall(
                harness, agentId, agent, cwd, path, command, startedProcess, Text(root, "sessionId"));
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
            string? command = tool is "Bash" && input is not null ? NodeText(input, "command") : null;

            // BashOutput waits on a shell that is still running, so it is judged like the Bash call that started it.
            bool startedProcess = tool is "Bash" or "BashOutput";
            return new WorkerGuardRules.ToolCall(
                harness, agentId, agent, cwd, path, command, startedProcess, Text(root, "session_id"));
        }

        return null;
    }

    /// <summary>
    /// The agent whose turn a Copilot CLI <c>subagentStop</c> event ends, or null for any other event. The event
    /// names an agent and a stop reason and no tool.
    /// </summary>
    internal static string? TurnEndOf(string harness, string stdin)
    {
        if (!string.Equals(harness, CopilotCliHarness, StringComparison.Ordinal))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(stdin);
        JsonElement root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && Text(root, "toolName") is null
            && Text(root, "stopReason") is not null
                ? Text(root, "agentId")
                : null;
    }

    /// <summary>The Squad agent a harness subagent type names, or null for the conductor and any other agent.</summary>
    internal static ArbiterSquadAgent? SquadAgentOf(string? type) =>
        type is not null
        && !string.Equals(type, "conductor", StringComparison.OrdinalIgnoreCase)
        && ArbiterSquadCatalog.LoadEmbedded().Agents.TryGetValue(type, out ArbiterSquadAgent? agent)
            ? agent
            : null;

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
}
