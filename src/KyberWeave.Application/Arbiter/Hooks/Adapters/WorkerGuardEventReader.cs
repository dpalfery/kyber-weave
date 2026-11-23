using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Reads a harness's own tool-call payload into the worker guard's typed call. This is the adapter boundary for the
/// guard: Claude's <c>tool_name</c>/<c>tool_input</c>, Copilot CLI's <c>toolName</c>/<c>toolArgs</c> and subagent
/// fields, and Factory's <c>tool_name</c>/<c>tool_input</c> joined through the session ledger all end here, so the
/// guard and its rules never parse a harness payload.
/// </summary>
internal static class WorkerGuardEventReader
{
    private const string CopilotCliHarness = WorkerGuardRules.CopilotCliHarness;
    private const string ClaudeHarness = WorkerGuardRules.ClaudeHarness;
    private const string FactoryHarness = WorkerGuardRules.FactoryHarness;

    /// <summary>How long a Factory child whose type is not known yet is given to resolve before its profile rules are skipped.</summary>
    private static readonly TimeSpan FactoryTypeWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The stand-in agent a Factory child carries while its type is unresolved: a capability profile no rule budgets,
    /// judges by a technology allow-list, or counts as a review seat, so only the every-profile rules apply.
    /// </summary>
    private static readonly ArbiterSquadAgent UnresolvedFactoryAgent =
        new("unresolved-factory-sub-agent", "A Factory child whose agent type is not known yet.", [], "unresolved", "unresolved");

    /// <summary>
    /// Reads one tool call. A root agent, an agent outside the Squad catalog, and an event with no tool are not
    /// guarded, so they read as null. Factory passes the session ledger it needs to tell a child from a root; the
    /// other harnesses read it from the payload alone.
    /// </summary>
    internal static WorkerGuardRules.ToolCall? Read(
        string harness,
        string stdin,
        IWorkerSessionStore? sessions = null,
        string? home = null,
        TextWriter? log = null)
    {
        using JsonDocument document = JsonDocument.Parse(stdin);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? cwd = Text(root, "cwd");
        if (string.Equals(harness, FactoryHarness, StringComparison.Ordinal))
        {
            return ReadFactory(root, cwd, sessions, home, log);
        }

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
            string? readPath = tool is "view" && args is not null ? NodeText(args, "path") : null;
            string? command = tool is "bash" or "powershell" && args is not null ? NodeText(args, "command") : null;

            // A read of a running shell's output waits on the process that shell started, so it is judged like the
            // command itself: a hung test shows up as a wait that never returns.
            bool startedProcess = tool is "bash" or "read_bash" or "powershell" or "read_powershell";
            return new WorkerGuardRules.ToolCall(
                harness, agentId, agent, cwd, path, command, startedProcess, Text(root, "sessionId"), readPath);
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
            string? readPath = input is null
                ? null
                : tool switch
                {
                    "Read" => NodeText(input, "file_path"),
                    "NotebookRead" => NodeText(input, "notebook_path"),
                    _ => null,
                };
            string? command = tool is "Bash" && input is not null ? NodeText(input, "command") : null;

            // BashOutput waits on a shell that is still running, so it is judged like the Bash call that started it.
            bool startedProcess = tool is "Bash" or "BashOutput";
            return new WorkerGuardRules.ToolCall(
                harness, agentId, agent, cwd, path, command, startedProcess, Text(root, "session_id"), readPath);
        }

        return null;
    }

    /// <summary>
    /// Reads a Factory PreToolUse event, but only for a session the ledger knows as a child. Factory's payload names
    /// no agent, so the type comes from the joined session events; a child whose type is still unresolved is polled
    /// for a short while, and then guarded by the every-profile rules only.
    /// </summary>
    private static WorkerGuardRules.ToolCall? ReadFactory(
        JsonElement root,
        string? cwd,
        IWorkerSessionStore? sessions,
        string? home,
        TextWriter? log)
    {
        if (sessions is null
            || string.IsNullOrWhiteSpace(home)
            || !string.Equals(Text(root, "hook_event_name"), "PreToolUse", StringComparison.Ordinal))
        {
            return null;
        }

        string? tool = Text(root, "tool_name");
        string? sessionId = Text(root, "session_id");
        if (tool is null || sessionId is null)
        {
            return null;
        }

        string key = WorkerSessionLedger.ChildKey(sessionId);
        WorkerSessionEntry? entry = sessions.Read(home, key);
        if (entry is null)
        {
            // A root session, or a child whose start has not arrived: never guarded.
            return null;
        }

        string? type = entry.AgentType;
        string parentSessionId = entry.ParentSessionId;
        if (type is null)
        {
            entry = sessions.AwaitType(home, key, FactoryTypeWait);
            if (entry is null)
            {
                // The child's entry went away while this call waited: it is no longer a known child.
                return null;
            }

            type = entry.AgentType;
            if (type is null)
            {
                LogSkippedProfileRules(log, sessionId);
                return CallOf(FactoryHarness, sessionId, UnresolvedFactoryAgent, parentSessionId, root, cwd, tool);
            }
        }

        return SquadAgentOf(type) is { } agent
            ? CallOf(FactoryHarness, sessionId, agent, parentSessionId, root, cwd, tool)
            : null;
    }

    private static void LogSkippedProfileRules(TextWriter? log, string sessionId)
    {
        if (log is null)
        {
            return;
        }

        try
        {
            log.WriteLine(
                $"arbiter: Factory session '{sessionId}' was still unresolved after 3 seconds; " +
                "KW-ARB-SECRET-001 and KW-ARB-CHECKOUT-001 still apply, and the profile-specific " +
                "worker guard rules were skipped for this call.");
        }
        catch (Exception)
        {
            // The log is best effort.
        }
    }

    private static WorkerGuardRules.ToolCall CallOf(
        string harness,
        string sessionId,
        ArbiterSquadAgent agent,
        string parentSessionId,
        JsonElement root,
        string? cwd,
        string tool)
    {
        JsonObject? input = ArgumentsOf(root, "tool_input");
        string? path = input is null ? null : PathOf(input);
        string? writePath = tool is "Edit" or "Create" or "ApplyPatch" ? path : null;
        string? readPath = tool is "Read" ? path : null;
        string? command = tool is "Execute" && input is not null ? NodeText(input, "command") : null;

        return new WorkerGuardRules.ToolCall(
            harness,
            sessionId,
            agent,
            cwd,
            writePath,
            command,
            StartedProcess: tool is "Execute",
            SessionId: parentSessionId,
            ReadPath: readPath);
    }

    /// <summary>
    /// The path a Factory file tool names. The hooks reference documents <c>file_path</c> for <c>Read</c>,
    /// <c>Edit</c> and <c>Create</c>; <c>path</c> is accepted too, and <c>ApplyPatch</c> documents neither, so a patch
    /// carrying no path is not judged by the path rules.
    /// </summary>
    private static string? PathOf(JsonObject input) => NodeText(input, "file_path") ?? NodeText(input, "path");

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
