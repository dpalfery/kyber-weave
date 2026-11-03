using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>
/// The Factory hook dialect ([F11] of the task packet): <c>PreToolUse</c> on
/// <c>Task</c> for dispatch gating, with the target from
/// <c>tool_input.subagent_type</c> and the prompt from
/// <c>tool_input.prompt</c>, and <c>PostToolUse</c> on the same tool for
/// return events.
/// </summary>
/// <remarks>
/// The dispatch tool matches exactly (<c>tool_name == "Task"</c>), so task-list
/// tools whose names merely start with <c>Task</c> do not gate. An event with no
/// sub-agent target — a non-<c>Task</c> tool, or a <c>Task</c> input with no
/// <c>subagent_type</c> — passes through untouched before configuration loads
/// (design §1.7): empty stdout on exit 0, which is Factory's documented proceed
/// (exit code 0 is success). A plain allow writes nothing for the same reason:
/// an explicit <c>permissionDecision: "allow"</c> would auto-approve the call.
/// Factory outputs the Claude Code format, so a deny writes
/// <c>hookSpecificOutput.permissionDecision: "deny"</c> with
/// <c>permissionDecisionReason</c>, a strip writes <c>"allow"</c> with the
/// complete <c>updatedInput</c> (every <c>tool_input</c> field copied, only
/// <c>prompt</c> rewritten), and post-dispatch outcomes ride
/// <c>hookSpecificOutput.additionalContext</c> — Factory documents
/// <c>decision: "block"</c> for that phase too, but the block's reason reaches
/// the conductor only as context after the tool has already run, so every
/// non-allow outcome is delivered the same way. Factory documents no call id,
/// so the adapter fabricates none: return pairing uses <c>pair-digest</c> at
/// the ledger layer, with the documented <c>session_id</c> riding along. The
/// hook is project-wide with no caller identity, so the rendered
/// <c>--caller</c> is honoured when present and the caller is otherwise
/// unidentified; plan reads stay advisory and header stripping rides the
/// dispatch allow path (design §12).
/// </remarks>
public sealed class FactoryHookAdapter : IHarnessHookAdapter
{
    /// <summary>The harness token this adapter serves.</summary>
    public const string Token = "factory";

    private const string DispatchTool = "Task";

    private readonly IHookDecisionEngine _engine;

    /// <summary>Creates an adapter over the hook decision engine.</summary>
    public FactoryHookAdapter(IHookDecisionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <inheritdoc/>
    public string HarnessToken => Token;

    /// <inheritdoc/>
    public bool IsPassThrough(JsonElement payload, string? renderedCaller)
    {
        _ = renderedCaller;
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

        if (!IsDispatchTool(tool))
        {
            return true;
        }

        // An event with no sub-agent target passes through untouched before
        // configuration loads (design §1.7).
        return string.IsNullOrWhiteSpace(SubagentTypeOf(payload));
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
        string caller = string.IsNullOrWhiteSpace(renderedCaller) ? "unidentified" : renderedCaller;

        if (string.Equals(eventName, "PreToolUse", StringComparison.Ordinal))
        {
            return HandlePre(payload, tool, caller, config, context);
        }

        if (string.Equals(eventName, "PostToolUse", StringComparison.Ordinal))
        {
            return HandlePost(payload, tool, caller, config, context);
        }

        throw new InvalidOperationException(
            $"Malformed Factory hook event: unknown hook_event_name '{eventName ?? "<missing>"}'.");
    }

    /// <inheritdoc/>
    public string RenderFailClosed(string detail, string? renderedCaller, string rawStdin, string decisionId)
    {
        _ = rawStdin;
        string safe = (detail ?? string.Empty)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "unknown error";
        }

        // The host throws config errors with the code already prefixed; strip it so the
        // envelope carries the code exactly once, in both ANSWER and EVIDENCE.
        string message = safe.StartsWith(
            HookCommand.FailClosedCode + ": ", StringComparison.Ordinal)
            ? safe.Substring((HookCommand.FailClosedCode + ": ").Length)
            : safe;
        string evidence = $"{HookCommand.FailClosedCode}: {message}";

        string caller = string.IsNullOrWhiteSpace(renderedCaller) ? "unidentified" : renderedCaller;
        string trigger = string.Equals(caller, "conductor", StringComparison.Ordinal)
            ? "delegate"
            : "investigate";
        string callerText = $"{caller} (factory)";
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

        // An internal error always writes the deny shape, even for a return event:
        // it is the one shape Factory guarantees to gate with.
        return RenderPreDeny(envelope.Render());
    }

    private string HandlePre(JsonElement payload, string? tool, string caller, KyberWeaveConfig config, HookContext context)
    {
        if (!IsDispatchTool(tool))
        {
            return string.Empty;
        }

        JsonElement toolInput = RequireToolInput(payload);
        string? target = GetString(toolInput, "subagent_type");
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException(
                "Malformed Factory hook event: Task input carries no subagent_type.");
        }

        string prompt = GetString(toolInput, "prompt")
            ?? throw new InvalidOperationException(
                "Malformed Factory hook event: Task input carries no prompt.");
        HookOutcome outcome = DecidePre(payload, caller, target, prompt, config, context);
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

    private string HandlePost(JsonElement payload, string? tool, string caller, KyberWeaveConfig config, HookContext context)
    {
        if (!IsDispatchTool(tool))
        {
            return string.Empty;
        }

        if (!payload.TryGetProperty("tool_response", out JsonElement response))
        {
            throw new InvalidOperationException(
                "Malformed Factory hook event: PostToolUse on Task carries no tool_response.");
        }

        string toolOutput = response.ValueKind == JsonValueKind.String
            ? response.GetString() ?? string.Empty
            : response.GetRawText();

        string? target = null;
        string prompt = string.Empty;
        if (payload.TryGetProperty("tool_input", out JsonElement toolInput)
            && toolInput.ValueKind == JsonValueKind.Object)
        {
            target = GetString(toolInput, "subagent_type");
            // The return classifies to its dispatch's trigger family, so the
            // original prompt rides along best effort; without it the engine
            // classifies from caller and target alone.
            prompt = GetString(toolInput, "prompt") ?? string.Empty;
        }

        HookOutcome outcome = DecidePost(payload, caller, target, prompt, toolOutput, config, context);
        return outcome.Kind switch
        {
            HookOutcomeKind.PassThrough => string.Empty,
            HookOutcomeKind.Allow => string.Empty,
            HookOutcomeKind.Deny => RenderPostAnnotation(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine denied without a reason.")),
            HookOutcomeKind.PostBlock => RenderPostAnnotation(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine blocked without a reason.")),
            HookOutcomeKind.PostAnnotation => RenderPostAnnotation(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine annotated without a note.")),
            _ => throw new InvalidOperationException(
                $"Hook engine returned pre-dispatch outcome '{outcome.Kind}' for a post-dispatch event."),
        };
    }

    private HookOutcome DecidePre(
        JsonElement payload,
        string caller,
        string target,
        string prompt,
        KyberWeaveConfig config,
        HookContext context)
    {
        if (_engine is IContextualHookDecisionEngine contextual)
        {
            // Factory documents no call id ([F11]): null keeps pairing on
            // pair-digest at the ledger layer, with session_id for scoping.
            return contextual.DecidePreDispatch(
                Token, caller, target, prompt, config, context,
                toolCallId: null, GetString(payload, "session_id"));
        }

        return _engine.DecidePreDispatch(caller, target, prompt, config);
    }

    private HookOutcome DecidePost(
        JsonElement payload,
        string caller,
        string? target,
        string prompt,
        string toolOutput,
        KyberWeaveConfig config,
        HookContext context)
    {
        if (_engine is IContextualHookDecisionEngine contextual)
        {
            return contextual.DecidePostDispatch(
                Token, caller, target, prompt, toolOutput, config, context,
                toolCallId: null, GetString(payload, "session_id"));
        }

        return _engine.DecidePostDispatch(caller, target, toolOutput, config);
    }

    private static JsonElement RequireToolInput(JsonElement payload)
    {
        if (payload.TryGetProperty("tool_input", out JsonElement toolInput)
            && toolInput.ValueKind == JsonValueKind.Object)
        {
            return toolInput;
        }

        throw new InvalidOperationException(
            "Malformed Factory hook event: the tool event carries no tool_input object.");
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
        string.Equals(tool, DispatchTool, StringComparison.Ordinal);

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
                "Malformed Factory hook event: tool_input is not a JSON object."),
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
