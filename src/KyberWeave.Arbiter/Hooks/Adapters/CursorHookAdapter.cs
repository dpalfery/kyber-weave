using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>
/// The Cursor hook dialect ([F6] of the task packet): <c>preToolUse</c> on
/// <c>Task</c> for dispatch gating, with the target from the undocumented
/// <c>tool_input.subagent_type</c> and the prompt from
/// <c>tool_input.prompt</c>, and <c>postToolUse</c> on the same tool for
/// return events paired by <c>tool_use_id</c>.
/// </summary>
/// <remarks>
/// The dispatch tool matches exactly (<c>tool_name == "Task"</c>), so task-list
/// tools whose names merely start with <c>Task</c> do not gate. A
/// <c>Task</c> event with no <c>subagent_type</c> is still classified: the
/// target fact is absent and the rules answer <c>undecidable</c>. Cursor hooks
/// fail open by default, so entries set <c>failClosed: true</c> and a missing
/// output blocks: every classified invocation therefore writes JSON, where the
/// sibling dialects write nothing on a plain pass. A non-<c>Task</c> event
/// passes through untouched before configuration loads; the rendered hook entry
/// matches on <c>Task</c>, so such payloads only arrive through
/// misconfiguration. <c>updated_input</c> rides on <c>allow</c> only and
/// replaces the input, so the strip path copies every <c>tool_input</c> field
/// and only rewrites <c>prompt</c>. The hook is project-wide with no caller
/// identity, so the rendered <c>--caller</c> is honoured when present and the
/// caller is otherwise unidentified; plan reads stay advisory and header
/// stripping rides the dispatch allow path.
/// </remarks>
public sealed class CursorHookAdapter : IHarnessHookAdapter
{
    /// <summary>The harness token this adapter serves.</summary>
    public const string Token = "cursor";

    private readonly IHookDecisionEngine _engine;

    /// <summary>Creates an adapter over the hook decision engine.</summary>
    public CursorHookAdapter(IHookDecisionEngine engine)
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

        if (IsPostEvent(eventName))
        {
            return !IsDispatchTool(tool);
        }

        if (!IsPreEvent(eventName))
        {
            // Unknown or missing event names are malformed, not pass-through: Handle
            // fails them closed.
            return false;
        }

        // A Task dispatch is always classified, even with no subagent_type: the
        // target fact stays absent and the rules answer undecidable.
        return !IsDispatchTool(tool);
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

        if (IsPreEvent(eventName))
        {
            return HandlePre(payload, tool, caller, config, context);
        }

        if (IsPostEvent(eventName))
        {
            return HandlePost(payload, tool, caller, config, context);
        }

        throw new InvalidOperationException(
            $"Malformed Cursor hook event: unknown hook_event_name '{eventName ?? "<missing>"}'.");
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
        string callerText = $"{caller} (cursor)";
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

        // An internal error always writes the deny shape, even for a return event.
        string rendered = envelope.Render();
        return RenderPreDeny(rendered, FirstLine(rendered));
    }

    private string HandlePre(JsonElement payload, string? tool, string caller, KyberWeaveConfig config, HookContext context)
    {
        if (!IsDispatchTool(tool))
        {
            return string.Empty;
        }

        JsonElement toolInput = RequireToolInput(payload);
        string? target = GetString(toolInput, "subagent_type");
        string prompt = GetString(toolInput, "prompt")
            ?? throw new InvalidOperationException(
                "Malformed Cursor hook event: Task input carries no prompt.");
        HookOutcome outcome = DecidePre(payload, caller, target, prompt, config, context);
        return outcome.Kind switch
        {
            HookOutcomeKind.PassThrough => RenderPreAllow(),
            HookOutcomeKind.Allow => ReadGuard.IsImplementationSpecialist(target)
                ? RenderPreAllowStrip(toolInput, prompt)
                : RenderPreAllow(),
            HookOutcomeKind.AllowWithRewrite => RenderPreAllowRaw(
                outcome.RewrittenInputJson ?? throw new InvalidOperationException(
                    "Hook engine returned allow-with-rewrite without rewritten input.")),
            HookOutcomeKind.Deny => RenderPreDenyFor(outcome.Reason),
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

        if (!payload.TryGetProperty("tool_output", out JsonElement output))
        {
            throw new InvalidOperationException(
                "Malformed Cursor hook event: postToolUse on Task carries no tool_output.");
        }

        string toolOutput = output.ValueKind == JsonValueKind.String
            ? output.GetString() ?? string.Empty
            : output.GetRawText();

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
            HookOutcomeKind.PassThrough => RenderPostAllow(),
            HookOutcomeKind.Allow => RenderPostAllow(),
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
        string? target,
        string prompt,
        KyberWeaveConfig config,
        HookContext context)
    {
        if (_engine is IContextualHookDecisionEngine contextual)
        {
            return contextual.DecidePreDispatch(
                Token, caller, target, prompt, config, context,
                GetString(payload, "tool_use_id"), SessionOf(payload));
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
                GetString(payload, "tool_use_id"), SessionOf(payload));
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
            "Malformed Cursor hook event: the tool event carries no tool_input object.");
    }

    private static string? SessionOf(JsonElement payload) =>
        GetString(payload, "session_id") ?? GetString(payload, "conversation_id");

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsDispatchTool(string? tool) =>
        string.Equals(tool, "Task", StringComparison.Ordinal);

    private static bool IsPreEvent(string? eventName) =>
        string.Equals(eventName, "preToolUse", StringComparison.OrdinalIgnoreCase);

    private static bool IsPostEvent(string? eventName) =>
        string.Equals(eventName, "postToolUse", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        string first = newline < 0 ? text : text.Substring(0, newline);
        return first.TrimEnd('\r');
    }

    private static string RenderPreDenyFor(string? reason)
    {
        if (reason is null)
        {
            throw new InvalidOperationException(
                "Hook engine denied without a reason.");
        }

        return RenderPreDeny(reason, FirstLine(reason));
    }

    private static string RenderPreDeny(string agentMessage, string userMessage) =>
        new JsonObject
        {
            ["permission"] = "deny",
            ["agent_message"] = agentMessage,
            ["user_message"] = userMessage,
        }.ToJsonString();

    private static string RenderPreAllow() =>
        new JsonObject
        {
            ["permission"] = "allow",
        }.ToJsonString();

    private static string RenderPreAllowStrip(JsonElement toolInput, string prompt) =>
        RenderPreAllowWithInput(JsonNode.Parse(toolInput.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException(
                "Malformed Cursor hook event: tool_input is not a JSON object."),
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

        if (rewritten is not JsonObject updated)
        {
            throw new InvalidOperationException(
                "Hook engine rewritten input must be a JSON object.");
        }

        return RenderPreAllowWithInput(updated, _ => { });
    }

    private static string RenderPreAllowWithInput(JsonObject updatedInput, Action<JsonObject> mutate)
    {
        mutate(updatedInput);
        return new JsonObject
        {
            ["permission"] = "allow",
            ["updated_input"] = updatedInput,
        }.ToJsonString();
    }

    private static string RenderPostAnnotation(string note) =>
        new JsonObject
        {
            ["additional_context"] = note,
        }.ToJsonString();

    private static string RenderPostAllow() =>
        new JsonObject().ToJsonString();
}
