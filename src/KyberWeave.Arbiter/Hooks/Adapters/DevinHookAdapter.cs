using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>
/// The Devin hook dialect ([F12] of the task packet): <c>PreToolUse</c> on
/// <c>run_subagent</c> for dispatch gating, with the target from
/// <c>tool_input.profile</c> — the vendor documents only that the tool "takes
/// a profile" — and the prompt from <c>tool_input.prompt</c>, and
/// <c>PostToolUse</c> on the same tool reading <c>tool_response.output</c> as
/// the return.
/// </summary>
/// <remarks>
/// The dispatch tool matches exactly (<c>tool_name == "run_subagent"</c>), so
/// similarly named tools do not gate. A dispatch whose profile or prompt is
/// absent is not malformed: the vendor documents only "a profile", so an
/// argument cannot be relied on to exist. Such a dispatch is treated as
/// unmarked — it is still classified, so the ledger records it and
/// <c>audit</c> reports it, and the missing field is logged to stderr — which
/// is why <see cref="IsPassThrough"/> classifies every <c>run_subagent</c>
/// event, unlike the adapters whose missing target passes through before
/// configuration loads. Devin outputs the Claude Code format, but it documents
/// no <c>permissionDecision</c> ([F12]): a deny writes the top-level
/// <c>{"decision":"block","reason":…}</c>, and a strip writes only
/// <c>hookSpecificOutput.updatedInput</c> with the prompt, because Devin merges
/// that object into the arguments — the profile and every other original field
/// survive without being re-sent. PostToolUse documents no output field at
/// all, so every post-dispatch outcome writes nothing; the dropped decision is
/// logged so it is not silent, and a malformed return still classifies with an
/// empty output rather than failing closed, because there is no channel the
/// failure could close. Devin documents no call id, so the adapter fabricates
/// none: return pairing uses <c>pair-digest</c> at the ledger layer, with the
/// documented <c>session_id</c> riding along. The hook is project-wide with no
/// caller identity, so the rendered <c>--caller</c> is honoured when present
/// and the caller is otherwise unidentified; plan reads stay advisory and
/// header stripping rides the dispatch allow path (design §12).
/// </remarks>
public sealed class DevinHookAdapter : IHarnessHookAdapter
{
    /// <summary>The harness token this adapter serves.</summary>
    public const string Token = "devin";

    private const string DispatchTool = "run_subagent";

    private readonly IHookDecisionEngine _engine;

    /// <summary>Creates an adapter over the hook decision engine.</summary>
    public DevinHookAdapter(IHookDecisionEngine engine)
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

        // Every run_subagent event is classified, including one whose profile or
        // prompt is absent: it is treated as unmarked, so the ledger records it
        // and audit reports it (design §1.7's pass-through is for events with no
        // sub-agent target, not for a target Devin failed to name).
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

        if (string.Equals(eventName, "PreToolUse", StringComparison.Ordinal))
        {
            return HandlePre(payload, tool, caller, config, context);
        }

        if (string.Equals(eventName, "PostToolUse", StringComparison.Ordinal))
        {
            return HandlePost(payload, tool, caller, config, context);
        }

        throw new InvalidOperationException(
            $"Malformed Devin hook event: unknown hook_event_name '{eventName ?? "<missing>"}'.");
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
        string callerText = $"{caller} (devin)";
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
        // it is the one shape Devin guarantees to gate with.
        return RenderPreDeny(envelope.Render());
    }

    private string HandlePre(
        JsonElement payload,
        string? tool,
        string caller,
        KyberWeaveConfig config,
        HookContext context)
    {
        if (!IsDispatchTool(tool))
        {
            return string.Empty;
        }

        JsonElement toolInput = RequireToolInput(payload);
        string? target = GetString(toolInput, "profile");
        bool hasPrompt = toolInput.TryGetProperty("prompt", out JsonElement promptElement)
            && promptElement.ValueKind == JsonValueKind.String;
        string prompt = hasPrompt ? promptElement.GetString() ?? string.Empty : string.Empty;

        // The vendor documents only that run_subagent "takes a profile" ([F12]):
        // an absent field means the dispatch cannot be marked, so it is treated
        // as unmarked — logged here, classified below, and reported by audit —
        // rather than failed as malformed.
        if (target is null || !hasPrompt)
        {
            context.Log.WriteLine(
                "Devin run_subagent dispatch without "
                + (target is null ? "profile" : "prompt")
                + "; treated as unmarked and recorded for audit.");
        }

        HookOutcome outcome = DecidePre(payload, caller, target, prompt, config, context);
        return outcome.Kind switch
        {
            HookOutcomeKind.PassThrough => string.Empty,

            // The strip exists for Req 25.1 header stripping, so it applies only
            // when a prompt was sent: a promptless dispatch carries no headers,
            // and merging an empty prompt would add a field Devin never sent.
            HookOutcomeKind.Allow => ReadGuard.IsImplementationSpecialist(target) && hasPrompt
                ? RenderMergeStrip(HeaderBlock.Strip(prompt))
                : string.Empty,
            HookOutcomeKind.AllowWithRewrite => RenderMergeStrip(
                RewrittenPromptOf(outcome.RewrittenInputJson
                    ?? throw new InvalidOperationException(
                        "Hook engine returned allow-with-rewrite without rewritten input."))),
            HookOutcomeKind.Deny => RenderPreDeny(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine denied without a reason.")),
            _ => throw new InvalidOperationException(
                $"Hook engine returned post-dispatch outcome '{outcome.Kind}' for a pre-dispatch event."),
        };
    }

    private string HandlePost(
        JsonElement payload,
        string? tool,
        string caller,
        KyberWeaveConfig config,
        HookContext context)
    {
        if (!IsDispatchTool(tool))
        {
            return string.Empty;
        }

        // A malformed return still classifies with an empty output: PostToolUse
        // documents no output field ([F12]), so there is no channel a fail could
        // close, while the classification is what pairs the return in the ledger.
        string toolOutput = payload.TryGetProperty("tool_response", out JsonElement response)
            && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("output", out JsonElement output)
            && output.ValueKind == JsonValueKind.String
                ? output.GetString() ?? string.Empty
                : string.Empty;

        string? target = null;
        string prompt = string.Empty;
        if (payload.TryGetProperty("tool_input", out JsonElement toolInput)
            && toolInput.ValueKind == JsonValueKind.Object)
        {
            target = GetString(toolInput, "profile");
            // The return classifies to its dispatch's trigger family, so the
            // original prompt rides along best effort; without it the engine
            // classifies from caller and target alone.
            prompt = GetString(toolInput, "prompt") ?? string.Empty;
        }

        HookOutcome outcome = DecidePost(payload, caller, target, prompt, toolOutput, config, context);
        switch (outcome.Kind)
        {
            case HookOutcomeKind.PassThrough:
            case HookOutcomeKind.Allow:
                break;
            default:
                context.Log.WriteLine(
                    $"Arbiter post-dispatch decision ({outcome.Kind}) for "
                    + $"'{target ?? "<none>"}' not delivered: Devin documents no PostToolUse output field.");
                break;
        }

        return string.Empty;
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
            // Devin documents no call id ([F12]): null keeps pairing on
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

    private static string RewrittenPromptOf(string rewrittenInputJson)
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

        return rewritten is JsonObject updated
            ? updated.TryGetPropertyValue("prompt", out JsonNode? prompt)
                && prompt is JsonValue value
                && value.TryGetValue<string>(out string? text)
                    ? text
                    : throw new InvalidOperationException(
                        "Hook engine rewritten input carries no prompt to merge.")
            : throw new InvalidOperationException(
                "Hook engine rewritten input must be a JSON object.");
    }

    private static JsonElement RequireToolInput(JsonElement payload)
    {
        if (payload.TryGetProperty("tool_input", out JsonElement toolInput)
            && toolInput.ValueKind == JsonValueKind.Object)
        {
            return toolInput;
        }

        throw new InvalidOperationException(
            "Malformed Devin hook event: the tool event carries no tool_input object.");
    }

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
            // [F12]: the top-level decision block is the only deny shape Devin
            // documents; permissionDecision is not documented for it.
            ["decision"] = "block",
            ["reason"] = reason,
        }.ToJsonString();

    private static string RenderMergeStrip(string strippedPrompt) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",

                // Only the prompt is sent: Devin merges updatedInput into the
                // arguments, so the profile and every other original field
                // survive without being re-sent.
                ["updatedInput"] = new JsonObject
                {
                    ["prompt"] = strippedPrompt,
                },
            },
        }.ToJsonString();
}
