using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>
/// The Claude Code hook dialect ([F1] of the task packet): <c>PreToolUse</c> on
/// <c>Agent</c> (alias <c>Task</c>) for dispatch gating, the planning-path Read guard
/// on <c>Read</c>/<c>Grep</c>/<c>Glob</c>/<c>Bash</c> for implementation specialists,
/// <c>PostToolUse</c> on <c>Agent</c> for return events, and <c>PreToolUse</c> on
/// <c>SubagentHandback</c> for background-dispatch returns (Q15 decision, option (a)).
/// </summary>
/// <remarks>
/// The payload's <c>agent_type</c> outranks the rendered <c>--caller</c>. An
/// <c>Agent</c> input without <c>subagent_type</c> is not a dispatch and passes through
/// before configuration loads. <c>updatedInput</c> replaces the entire input, so the
/// strip path copies every <c>tool_input</c> field and only rewrites <c>prompt</c>.
/// Background launches (<c>status: async_launched</c>) record the
/// <c>tool_use_id</c>-to-<c>agentId</c> link and write nothing. A hand-back is keyed
/// by the payload's <c>agent_id</c>, joined to its dispatch through that link, and
/// carries <c>tool_input.message</c> as the output; it is never denied, so a
/// non-allow outcome returns <c>allow</c> with the envelope or note appended to
/// <c>message</c> after one blank line.
/// </remarks>
public sealed class ClaudeHookAdapter : IHarnessHookAdapter
{
    /// <summary>The harness token this adapter serves.</summary>
    public const string Token = "claude";

    private readonly IHookDecisionEngine _engine;

    /// <summary>
    /// Background-launch links by harness tool-call id. In-memory by design: the hook
    /// host is a short-lived process per event, and the link only correlates a launch
    /// with its later return within one session.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, string> LaunchLinks = new();

    /// <summary>
    /// Hand-back returns by returning <c>agent_id</c>, mapped to the joined dispatch
    /// <c>tool_use_id</c> where task 4.1's <c>agentId</c> link resolves one, and to
    /// <see cref="string.Empty"/> where the return arrived with no join (unpaired, for
    /// <c>KW-ARB-AUDIT-003</c> to report). A foreground <c>PostToolUse(Agent)</c> with
    /// <c>status: completed</c> for a recorded <c>agentId</c> is not recorded twice.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, string> HandbackReturns = new();

    /// <summary>Creates an adapter over the hook decision engine.</summary>
    public ClaudeHookAdapter(IHookDecisionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <inheritdoc/>
    public string HarnessToken => Token;

    /// <inheritdoc/>
    public bool IsPassThrough(JsonElement payload, string? renderedCaller)
    {
        string? eventName = GetString(payload, "hook_event_name");
        string? tool = GetString(payload, "tool_name");

        if (string.Equals(eventName, "PostToolUse", StringComparison.Ordinal))
        {
            return !IsDispatchTool(tool);
        }

        if (!string.Equals(eventName, "PreToolUse", StringComparison.Ordinal))
        {
            // Unknown or missing event names are malformed, not pass-through: Handle
            // fails them closed.
            return false;
        }

        if (IsDispatchTool(tool))
        {
            return string.IsNullOrWhiteSpace(SubagentTypeOf(payload));
        }

        if (IsHandbackTool(tool))
        {
            return false;
        }

        if (IsReadTool(tool))
        {
            return !ReadGuard.IsImplementationSpecialist(ResolveCaller(payload, renderedCaller));
        }

        return true;
    }

    /// <inheritdoc/>
    public string Handle(
        JsonElement payload,
        string rawJson,
        string? renderedCaller,
        KyberWeaveConfig config,
        HookContext context)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);
        _ = rawJson;

        string? eventName = GetString(payload, "hook_event_name");
        string? tool = GetString(payload, "tool_name");
        string caller = ResolveCaller(payload, renderedCaller) ?? "unidentified";

        if (string.Equals(eventName, "PreToolUse", StringComparison.Ordinal))
        {
            return HandlePre(payload, tool, caller, config);
        }

        if (string.Equals(eventName, "PostToolUse", StringComparison.Ordinal))
        {
            return HandlePost(payload, tool, caller, config);
        }

        throw new InvalidOperationException(
            $"Malformed Claude hook event: unknown hook_event_name '{eventName ?? "<missing>"}'.");
    }

    /// <inheritdoc/>
    public string RenderFailClosed(string detail, string? renderedCaller, string rawStdin, string decisionId)
    {
        string safe = (detail ?? string.Empty)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "unknown error";
        }

        string? eventName = null;
        string? agentType = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawStdin);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                eventName = GetString(doc.RootElement, "hook_event_name");
                agentType = GetString(doc.RootElement, "agent_type");
            }
        }
        catch (JsonException)
        {
            // Best effort only: the envelope carries what is known.
        }

        // The host throws config errors with the code already prefixed; strip it so the
        // envelope carries the code exactly once, in both ANSWER and EVIDENCE.
        string message = safe.StartsWith(
            HookCommand.FailClosedCode + ": ", StringComparison.Ordinal)
            ? safe.Substring((HookCommand.FailClosedCode + ": ").Length)
            : safe;
        string evidence = $"{HookCommand.FailClosedCode}: {message}";

        string? caller = !string.IsNullOrWhiteSpace(agentType) ? agentType : renderedCaller;
        string trigger = string.Equals(caller, "conductor", StringComparison.Ordinal)
            ? "delegate"
            : "investigate";
        string callerText = $"{(string.IsNullOrWhiteSpace(caller) ? "unidentified" : caller)} (claude)";
        ArbiterEscalationEnvelope envelope = new(
            trigger,
            callerText,
            null,
            null,
            null,
            [new ArbiterEnvelopeEntry(
                HookCommand.FailClosedCode,
                "Did the hook host fail before evaluation completed?",
                ArbiterAnswerText.Error(HookCommand.FailClosedCode, message),
                evidence)],
            string.IsNullOrWhiteSpace(decisionId) ? "unknown" : decisionId,
            1);

        return string.Equals(eventName, "PostToolUse", StringComparison.Ordinal)
            ? RenderPostBlock(envelope.Render())
            : RenderPreDeny(envelope.Render());
    }

    /// <summary>Resolves the caller: the payload's <c>agent_type</c> outranks <c>--caller</c>.</summary>
    internal static string? ResolveCaller(JsonElement payload, string? renderedCaller)
    {
        string? harnessCaller = GetString(payload, "agent_type");
        if (!string.IsNullOrWhiteSpace(harnessCaller))
        {
            return harnessCaller;
        }

        return string.IsNullOrWhiteSpace(renderedCaller) ? null : renderedCaller;
    }

    private string HandlePre(JsonElement payload, string? tool, string caller, KyberWeaveConfig config)
    {
        if (IsDispatchTool(tool))
        {
            JsonElement toolInput = RequireToolInput(payload);
            string? target = GetString(toolInput, "subagent_type");
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException(
                    "Malformed Claude hook event: Agent input carries no subagent_type.");
            }

            string prompt = GetString(toolInput, "prompt")
                ?? throw new InvalidOperationException(
                    "Malformed Claude hook event: Agent input carries no prompt.");
            HookOutcome outcome = _engine.DecidePreDispatch(caller, target, prompt, config);
            return outcome.Kind switch
            {
                HookOutcomeKind.PassThrough => string.Empty,
                HookOutcomeKind.Allow => ReadGuard.IsImplementationSpecialist(target)
                    ? RenderPreAllowStrip(toolInput, prompt)
                    : string.Empty,
                HookOutcomeKind.AllowWithRewrite => RenderPreAllowRaw(
                    outcome.RewrittenInputJson ?? throw new InvalidOperationException(
                        "Hook engine returned allow-with-rewrite without rewritten input.")),
                HookOutcomeKind.Deny => RenderPreDeny(
                    outcome.Reason ?? throw new InvalidOperationException(
                        "Hook engine denied without a reason.")),
                _ => throw new InvalidOperationException(
                    $"Hook engine returned post-dispatch outcome '{outcome.Kind}' for a pre-dispatch event."),
            };
        }

        if (IsReadTool(tool))
        {
            JsonElement toolInput = RequireToolInput(payload);
            IReadOnlyList<string> dirs = ReadGuard.ProtectedDirectories(config);
            ReadGuardResult verdict = ReadGuard.Check(
                tool!, toolInput, RepoRootOf(payload), dirs);
            return verdict.Allowed
                ? string.Empty
                : RenderPreDeny(verdict.Reason ?? "Denied by the Read guard (Req 25.2, Req 25.4).");
        }

        if (IsHandbackTool(tool))
        {
            return HandleHandback(payload, caller, config);
        }

        return string.Empty;
    }

    private string HandlePost(JsonElement payload, string? tool, string caller, KyberWeaveConfig config)
    {
        if (!IsDispatchTool(tool))
        {
            return string.Empty;
        }

        if (!payload.TryGetProperty("tool_response", out JsonElement response)
            || response.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Malformed Claude hook event: PostToolUse on Agent carries no tool_response.");
        }

        string? status = GetString(response, "status");
        if (string.Equals(status, "async_launched", StringComparison.Ordinal))
        {
            string? toolUseId = GetString(payload, "tool_use_id");
            string? agentId = GetString(response, "agentId");
            if (!string.IsNullOrWhiteSpace(toolUseId) && !string.IsNullOrWhiteSpace(agentId))
            {
                LaunchLinks[toolUseId] = agentId;
            }

            return string.Empty;
        }

        if (!string.Equals(status, "completed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Malformed Claude hook event: unknown PostToolUse status '{status ?? "<missing>"}'.");
        }

        string? target = null;
        if (payload.TryGetProperty("tool_input", out JsonElement toolInput)
            && toolInput.ValueKind == JsonValueKind.Object)
        {
            target = GetString(toolInput, "subagent_type");
        }

        // No double recording: a hand-back already delivered this return.
        string? completedAgentId = GetString(response, "agentId");
        if (!string.IsNullOrWhiteSpace(completedAgentId)
            && HandbackReturns.ContainsKey(completedAgentId))
        {
            return string.Empty;
        }

        HookOutcome outcome = _engine.DecidePostDispatch(caller, target, response.GetRawText(), config);
        return outcome.Kind switch
        {
            HookOutcomeKind.PassThrough => string.Empty,
            HookOutcomeKind.Allow => string.Empty,
            HookOutcomeKind.Deny => RenderPostBlock(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine denied without a reason.")),
            HookOutcomeKind.PostBlock => RenderPostBlock(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine blocked without a reason.")),
            HookOutcomeKind.PostAnnotation => RenderPostAnnotation(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine annotated without a note.")),
            _ => throw new InvalidOperationException(
                $"Hook engine returned pre-dispatch outcome '{outcome.Kind}' for a post-dispatch event."),
        };
    }

    /// <summary>
    /// Handles a background-dispatch return through <c>PreToolUse</c> on
    /// <c>SubagentHandback</c>: records it under the payload's <c>agent_id</c>,
    /// joined to its dispatch through task 4.1's <c>agentId</c> link, runs the
    /// post-dispatch rules over <c>tool_input.message</c>, and delivers a non-allow
    /// outcome as <c>allow</c> with the complete input (the envelope or note
    /// appended to <c>message</c> after one blank line). The hand-back is never
    /// denied: a return with no join is recorded unpaired and allowed.
    /// </summary>
    private string HandleHandback(JsonElement payload, string caller, KyberWeaveConfig config)
    {
        JsonElement toolInput = RequireToolInput(payload);
        string? agentId = GetString(payload, "agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new InvalidOperationException(
                "Malformed Claude hook event: SubagentHandback carries no agent_id.");
        }

        string? message = GetString(toolInput, "message");
        if (message is null)
        {
            throw new InvalidOperationException(
                "Malformed Claude hook event: SubagentHandback input carries no message.");
        }

        HookOutcome outcome = _engine.DecidePostDispatch(caller, caller, message, config);
        HandbackReturns[agentId] = JoinedToolUseId(agentId) ?? string.Empty;
        return outcome.Kind switch
        {
            HookOutcomeKind.PassThrough => string.Empty,
            HookOutcomeKind.Allow => string.Empty,
            HookOutcomeKind.Deny => RenderHandbackAllow(
                toolInput,
                message,
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine denied without a reason.")),
            HookOutcomeKind.PostBlock => RenderHandbackAllow(
                toolInput,
                message,
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine blocked without a reason.")),
            HookOutcomeKind.PostAnnotation => RenderHandbackAllow(
                toolInput,
                message,
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine annotated without a note.")),
            _ => throw new InvalidOperationException(
                $"Hook engine returned pre-dispatch outcome '{outcome.Kind}' for a post-dispatch event."),
        };
    }

    /// <summary>
    /// Joins a returning <c>agent_id</c> to its dispatch through task 4.1's
    /// <c>agentId</c> link. Null when the return arrived with no join: it is still
    /// recorded, unpaired, for <c>KW-ARB-AUDIT-003</c> to report.
    /// </summary>
    private static string? JoinedToolUseId(string agentId)
    {
        foreach (KeyValuePair<string, string> link in LaunchLinks)
        {
            if (string.Equals(link.Value, agentId, StringComparison.Ordinal))
            {
                return link.Key;
            }
        }

        return null;
    }

    private static string RepoRootOf(JsonElement payload)
    {
        string? cwd = GetString(payload, "cwd");
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            return cwd;
        }

        return Directory.GetCurrentDirectory();
    }

    private static JsonElement RequireToolInput(JsonElement payload)
    {
        if (payload.TryGetProperty("tool_input", out JsonElement toolInput)
            && toolInput.ValueKind == JsonValueKind.Object)
        {
            return toolInput;
        }

        throw new InvalidOperationException(
            "Malformed Claude hook event: the tool event carries no tool_input object.");
    }

    private static string? SubagentTypeOf(JsonElement payload) =>
        payload.TryGetProperty("tool_input", out JsonElement toolInput)
        && toolInput.ValueKind == JsonValueKind.Object
            ? GetString(toolInput, "subagent_type")
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsDispatchTool(string? tool) =>
        string.Equals(tool, "Agent", StringComparison.Ordinal)
        || string.Equals(tool, "Task", StringComparison.Ordinal);

    private static bool IsReadTool(string? tool) =>
        string.Equals(tool, "Read", StringComparison.Ordinal)
        || string.Equals(tool, "Grep", StringComparison.Ordinal)
        || string.Equals(tool, "Glob", StringComparison.Ordinal)
        || string.Equals(tool, "Bash", StringComparison.Ordinal);

    private static bool IsHandbackTool(string? tool) =>
        string.Equals(tool, "SubagentHandback", StringComparison.Ordinal);

    private static string RenderPreDeny(string reason) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            },
        }.ToJsonString();

    private static string RenderPreAllowStrip(JsonElement toolInput, string prompt) =>
        RenderPreAllow(JsonNode.Parse(toolInput.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException(
                "Malformed Claude hook event: tool_input is not a JSON object."),
            node => node["prompt"] = HeaderBlock.Strip(prompt));

    private static string RenderPreAllowRaw(string rewrittenInputJson)
    {
        JsonNode rewritten;
        try
        {
            rewritten = JsonNode.Parse(rewrittenInputJson)
                ?? throw new InvalidOperationException("Rewritten input parsed to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Hook engine returned invalid rewritten input JSON: {ex.Message}", ex);
        }

        return RenderPreAllow(rewritten, _ => { });
    }

    private static string RenderPreAllow(JsonNode updatedInput, Action<JsonObject> mutate)
    {
        if (updatedInput is JsonObject updated)
        {
            mutate(updated);
        }
        else
        {
            throw new InvalidOperationException(
                "Hook engine rewritten input must be a JSON object.");
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "allow",
                ["updatedInput"] = updated,
            },
        }.ToJsonString();
    }

    private static string RenderPostBlock(string reason) =>
        new JsonObject
        {
            ["decision"] = "block",
            ["reason"] = reason,
        }.ToJsonString();

    /// <summary>
    /// Delivers a non-allow post-dispatch outcome inside the hand-back: <c>allow</c>
    /// with the complete input, the envelope or note appended to <c>message</c>
    /// after one blank line. <c>updatedInput</c> replaces the entire input, so every
    /// unchanged field is copied and only <c>message</c> is rewritten.
    /// </summary>
    private static string RenderHandbackAllow(JsonElement toolInput, string message, string note)
    {
        JsonObject updated = JsonNode.Parse(toolInput.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException(
                "Malformed Claude hook event: tool_input is not a JSON object.");
        updated["message"] = message + "\n\n" + note;
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "allow",
                ["updatedInput"] = updated,
            },
        }.ToJsonString();
    }

    private static string RenderPostAnnotation(string note) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PostToolUse",
                ["additionalContext"] = note,
            },
        }.ToJsonString();
}
