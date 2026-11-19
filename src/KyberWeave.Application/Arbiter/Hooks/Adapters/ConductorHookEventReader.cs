using System.Text.Json;
using System.Text.Json.Nodes;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>One tool call from a conductor's hook event, in the guard's own terms.</summary>
/// <param name="Name">The tool name.</param>
/// <param name="Path">The <c>path</c> argument of a <c>view</c> call, when it names one.</param>
/// <param name="Command">The <c>command</c> argument of a <c>bash</c> or <c>powershell</c> call, when it names one.</param>
/// <param name="AgentType">The <c>agent_type</c> argument of a <c>task</c> call, when it names one.</param>
/// <param name="Prompt">The <c>prompt</c> argument of a <c>task</c> call, when it carries one.</param>
/// <param name="HasArguments">Whether the call carried an arguments object at all.</param>
internal sealed record ConductorToolCall(
    string Name,
    string? Path = null,
    string? Command = null,
    string? AgentType = null,
    string? Prompt = null,
    bool HasArguments = false);

/// <summary>The conductor guard's typed event: what the adapter read from Copilot's payload.</summary>
/// <param name="Cwd">The working directory the payload names, or null.</param>
/// <param name="TransformedPrompt">The model-facing prompt a transform event carries, or null.</param>
/// <param name="Calls">The tool calls the event reports; empty for a prompt transform event.</param>
/// <param name="IsWorkerCall">Whether the event is a subagent's own call, which the guard leaves alone.</param>
internal sealed record ConductorGuardEvent(
    string? Cwd,
    string? TransformedPrompt,
    IReadOnlyList<ConductorToolCall> Calls,
    bool IsWorkerCall);

/// <summary>
/// Reads Copilot's conductor payload into the guard's typed event. The guard and its rules never read the harness's
/// field names again: <c>cwd</c>, <c>toolCalls</c>, <c>toolName</c> and the runtime's subagent fields end here.
/// </summary>
internal static class ConductorHookEventReader
{
    /// <summary>Reads one payload. A malformed payload throws, and the guard answers its fail-closed deny.</summary>
    internal static ConductorGuardEvent Read(string stdin)
    {
        using JsonDocument document = JsonDocument.Parse(stdin);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The conductor guard expected a JSON object on stdin.");
        }

        return new ConductorGuardEvent(
            Text(root, "cwd"),
            Text(root, "transformedPrompt"),
            ReadCalls(root),
            IsWorkerCall(root));
    }

    private static List<ConductorToolCall> ReadCalls(JsonElement root)
    {
        List<ConductorToolCall> calls = [];
        if (root.TryGetProperty("toolCalls", out JsonElement batch) && batch.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement call in batch.EnumerateArray())
            {
                calls.Add(Call(Text(call, "name") ?? string.Empty, ArgumentsOf(call, "args")));
            }

            return calls;
        }

        if (Text(root, "toolName") is { } toolName)
        {
            calls.Add(Call(toolName, ArgumentsOf(root, "toolArgs")));
        }
        else if (Text(root, "tool_name") is { } snakeName)
        {
            calls.Add(Call(snakeName, ArgumentsOf(root, "tool_input")));
        }

        return calls;
    }

    private static ConductorToolCall Call(string name, JsonElement? arguments)
    {
        if (arguments is not { } args)
        {
            return new ConductorToolCall(name);
        }

        return new ConductorToolCall(
            name,
            Path: Text(args, "path"),
            Command: Text(args, "command"),
            AgentType: Text(args, "agent_type"),
            Prompt: Text(args, "prompt"),
            HasArguments: true);
    }

    private static JsonElement? ArgumentsOf(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement arguments))
        {
            return null;
        }

        if (arguments.ValueKind != JsonValueKind.String)
        {
            return arguments;
        }

        using JsonDocument parsed = JsonDocument.Parse(arguments.GetString() ?? string.Empty);
        return parsed.RootElement.Clone();
    }

    /// <summary>
    /// A worker's tool call, by the runtime's own subagent fields. Copilot omits all three on a root call, writes them
    /// at the top level where tool arguments cannot reach, and in Copilot CLI 1.0.109 never sends a worker's call to the
    /// conductor's hooks at all. Requiring the full set, and a type other than the conductor, keeps a stray id from
    /// opening the guard.
    /// </summary>
    private static bool IsWorkerCall(JsonElement root)
    {
        bool camel = Text(root, "agentId") is not null
            && Text(root, "parentAgentId") is not null
            && Text(root, "agentType") is { } camelType
            && !IsConductor(camelType);
        bool snake = Text(root, "agent_id") is not null
            && Text(root, "parent_agent_id") is not null
            && Text(root, "agent_type") is { } snakeType
            && !IsConductor(snakeType);
        return camel || snake;

        static bool IsConductor(string type) => string.Equals(type, "conductor", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Text(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } found
            ? found
            : null;
}
