using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks.Adapters;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Reads one harness payload into the typed <see cref="HookEvent"/> every guard, rule and the hook host consume.
/// This is the adapter boundary: the raw field names of each harness end here.
/// </summary>
/// <remarks>
/// The shapes are the ones the harness adapters already parse: Claude and Copilot VS Code use
/// <c>hook_event_name</c>/<c>tool_name</c>/<c>tool_input</c>, Copilot CLI uses <c>toolName</c>/<c>toolArgs</c>, and a
/// plugin shim writes the <c>kyber-arbiter.plugin-event/v1</c> envelope. Every other harness contributes the same
/// fields through the generic names, so an unknown token still yields cwd, tool and prompt rather than nothing.
/// </remarks>
public static class HarnessHookEventReader
{
    /// <summary>Reads a payload into the typed event.</summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="payload">The raw harness payload.</param>
    /// <param name="renderedCaller">The <c>--caller</c> the hook was given, used as the agent type when the payload names none.</param>
    public static HookEvent Read(string harness, JsonElement payload, string? renderedCaller = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return new HookEvent(harness);
        }

        if (string.Equals(Text(payload, "schema"), PluginEnvelope.SchemaV1, StringComparison.Ordinal))
        {
            return Plugin(harness, payload);
        }

        return string.Equals(harness, "copilot-cli", StringComparison.Ordinal)
            ? CopilotCli(harness, payload, renderedCaller)
            : ClaudeFamily(harness, payload, renderedCaller);
    }

    private static HookEvent Plugin(string harness, JsonElement payload)
    {
        JsonElement args = ArgumentsOf(payload, "args");
        return new HookEvent(
            harness,
            Cwd: Text(payload, "cwd"),
            WorkspaceRoot: Workspace(payload),
            ToolName: Text(payload, "tool"),
            CallId: Text(payload, "call-id") ?? Text(payload, "callId") ?? Text(payload, "callID"),
            SessionId: Text(payload, "session") ?? Text(payload, "sessionId") ?? Text(payload, "sessionID"),
            Prompt: ArgText(args, "prompt"),
            Arguments: args);
    }

    private static HookEvent CopilotCli(string harness, JsonElement payload, string? renderedCaller)
    {
        // Copilot CLI 1.x sends a pre-tool-use event as a `toolCalls` batch; older and non-batched events carry
        // `toolName` with `toolArgs`. Both are the same event to the Arbiter, so the first call is read.
        if (payload.TryGetProperty("toolCalls", out JsonElement batch)
            && batch.ValueKind == JsonValueKind.Array
            && batch.GetArrayLength() > 0
            && batch[0].ValueKind == JsonValueKind.Object)
        {
            JsonElement first = batch[0];
            JsonElement callArgs = ArgumentsOf(first, "args");
            return new HookEvent(
                harness,
                Cwd: Text(payload, "cwd"),
                WorkspaceRoot: Workspace(payload),
                AgentId: Text(payload, "agentId") ?? Text(payload, "agent_id"),
                AgentType: Text(payload, "agentType") ?? Text(payload, "agent_type") ?? renderedCaller,
                ToolName: Text(first, "name"),
                CallId: Text(first, "id") ?? Text(payload, "toolCallId"),
                SessionId: Text(payload, "sessionId"),
                Prompt: ArgText(callArgs, "prompt"),
                Arguments: callArgs);
        }

        JsonElement args = ArgumentsOf(payload, "toolArgs");
        return new HookEvent(
            harness,
            Cwd: Text(payload, "cwd"),
            WorkspaceRoot: Workspace(payload),
            AgentId: Text(payload, "agentId") ?? Text(payload, "agent_id"),
            AgentType: Text(payload, "agentType") ?? Text(payload, "agent_type") ?? renderedCaller,
            ToolName: Text(payload, "toolName"),
            CallId: Text(payload, "toolCallId") ?? Text(payload, "callId"),
            SessionId: Text(payload, "sessionId"),
            Prompt: ArgText(args, "prompt"),
            Arguments: args);
    }

    private static HookEvent ClaudeFamily(string harness, JsonElement payload, string? renderedCaller)
    {
        JsonElement input = ArgumentsOf(payload, "tool_input");
        if (input.ValueKind != JsonValueKind.Object)
        {
            input = ArgumentsOf(payload, "toolArgs");
        }

        return new HookEvent(
            harness,
            Cwd: Text(payload, "cwd"),
            WorkspaceRoot: Workspace(payload),
            AgentId: Text(payload, "agent_id") ?? Text(payload, "agentId"),
            AgentType: Text(payload, "agent_type") ?? Text(payload, "agentType") ?? renderedCaller,
            ToolName: Text(payload, "tool_name") ?? Text(payload, "toolName"),
            CallId: Text(payload, "tool_use_id") ?? Text(payload, "toolCallId") ?? Text(payload, "callId"),
            SessionId: Text(payload, "session_id") ?? Text(payload, "sessionId"),
            Prompt: ArgText(input, "prompt"),
            Arguments: input);
    }

    /// <summary>The tool arguments, as an object whether the harness sent one or a JSON string.</summary>
    private static JsonElement ArgumentsOf(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out JsonElement value))
        {
            return default;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.Clone();
        }

        return value.ValueKind == JsonValueKind.String
            && TryParseObject(value.GetString()) is { } parsed
                ? JsonSerializer.SerializeToElement(parsed)
                : default;
    }

    /// <summary>
    /// The arguments a harness sent as a JSON string, or null when the string is empty or not a JSON object: a
    /// malformed argument string is missing input, which reads as no arguments rather than failing the hook.
    /// </summary>
    private static JsonObject? TryParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ArgText(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object ? Text(arguments, name) : null;

    // Antigravity names the workspace list workspacePaths; Cursor names it workspace_roots and sends no cwd, so a
    // user-level Cursor hook, which runs from ~/.cursor, finds the repository only here.
    private static string? Workspace(JsonElement payload) =>
        FirstPath(payload, "workspacePaths") ?? FirstPath(payload, "workspace_roots");

    private static string? FirstPath(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out JsonElement paths)
        && paths.ValueKind == JsonValueKind.Array
        && paths.GetArrayLength() > 0
        && paths[0].ValueKind == JsonValueKind.String
        && paths[0].GetString() is { } workspace
        && !string.IsNullOrWhiteSpace(workspace)
            ? workspace
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
