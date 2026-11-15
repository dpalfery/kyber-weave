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
/// <c>tool_input.prompt</c>, <c>PostToolUse</c> on the same tool for foreground
/// return events, and <c>SubagentStop</c> for background returns.
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
/// dispatch allow path (design §12). <c>Task</c> runs in the background by default,
/// so its <c>PostToolUse</c> fires at launch with no report: that event records
/// nothing, and the return is recorded on <c>SubagentStop</c>, which carries the
/// report but names the task only by its description.
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

        if (string.Equals(eventName, "SubagentStop", StringComparison.Ordinal))
        {
            // A field of the wrong type is malformed, not absent: Handle fails it closed.
            return IsAbsentOrBlank(payload, "task_name") || IsAbsentOrBlank(payload, "transcript_path");
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

        if (string.Equals(eventName, "SubagentStop", StringComparison.Ordinal))
        {
            return HandleSubagentStop(payload, caller, config, context);
        }

        throw new InvalidOperationException(
            $"Malformed Factory hook event: unknown hook_event_name '{eventName ?? "<missing>"}'.");
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
            HookOutcomeKind.AllowWithNote => RenderPreAllowNote(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine allowed with a note but wrote none.")),
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

        // A background launch carries no report. Recording it as the return would close
        // the dispatch with nothing in it, and the real return arrives on SubagentStop.
        if (FactoryTranscript.IsBackgroundLaunch(response))
        {
            return string.Empty;
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
            HookOutcomeKind.AllowWithNote => RenderPostAnnotation(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine allowed with a note but wrote none.")),
            _ => throw new InvalidOperationException(
                $"Hook engine returned pre-dispatch outcome '{outcome.Kind}' for a post-dispatch event."),
        };
    }

    /// <summary>
    /// Records a background task's return. Writes nothing: the event ends a sub-agent, so
    /// its output has no conductor context to reach, and the engine logs any non-allow.
    /// </summary>
    private string HandleSubagentStop(JsonElement payload, string caller, KyberWeaveConfig config, HookContext context)
    {
        string? description = GetString(payload, "task_name");
        string? transcriptPath = GetString(payload, "transcript_path");
        if (description is null || transcriptPath is null)
        {
            throw new InvalidOperationException(
                "Malformed Factory hook event: SubagentStop's task_name and transcript_path must be strings.");
        }

        FactoryBackgroundDispatch? dispatch = FactoryTranscript.FindBackgroundDispatch(transcriptPath, description);
        if (dispatch is null)
        {
            return string.Empty;
        }

        // The transcript keeps the prompt as written, while a specialist ran it stripped.
        string prompt = ReadGuard.IsImplementationSpecialist(dispatch.Target)
            ? HeaderBlock.Strip(dispatch.Prompt)
            : dispatch.Prompt;
        string? result = GetString(payload, "task_result");
        string output = string.IsNullOrWhiteSpace(result) ? GetString(payload, "task_error") ?? string.Empty : result;
        _ = DecidePost(payload, caller, dispatch.Target, prompt, output, config, context);
        return string.Empty;
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

    private static bool IsAbsentOrBlank(JsonElement element, string name) =>
        !element.TryGetProperty(name, out JsonElement value)
        || value.ValueKind == JsonValueKind.Null
        || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsDispatchTool(string? tool) =>
        string.Equals(tool, DispatchTool, StringComparison.Ordinal);

    private static string RenderPreAllowNote(string note) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "allow",
                ["permissionDecisionReason"] = note,
            },
        }.ToJsonString();

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
