using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>
/// The Codex hook dialect ([F7] of the task packet): <c>PreToolUse</c> on
/// <c>spawn_agent</c> (also matched as <c>Agent</c>) for dispatch gating, with
/// the target from <c>tool_input.agent_type</c> and the prompt from
/// <c>tool_input.message</c>, and <c>PostToolUse</c> on the same tool for
/// return events paired by <c>tool_use_id</c>.
/// </summary>
/// <remarks>
/// The dispatch matcher is <c>^(Agent|(.*[._:/])?spawn_agent)$</c>, so a
/// namespaced <c>spawn_agent</c> still gates while unrelated tools pass through
/// before configuration loads. An event with no sub-agent target passes through
/// untouched. <c>updatedInput</c> is valid only with <c>allow</c> and replaces
/// the entire arguments object, so the strip path copies every
/// <c>tool_input</c> field and only rewrites <c>message</c>. A post-dispatch
/// outcome is always delivered as <c>hookSpecificOutput.additionalContext</c>:
/// <c>decision: block</c> would replace the sub-agent's result. The hook is
/// project-wide with no caller identity, so the rendered <c>--caller</c> is
/// honoured when present and the caller is otherwise unidentified; plan reads
/// stay advisory and header stripping rides the dispatch allow path.
/// </remarks>
public sealed partial class CodexHookAdapter : IHarnessHookAdapter
{
    /// <summary>The harness token this adapter serves.</summary>
    public const string Token = HarnessRegistry.CodexToken;

    private readonly IHookDecisionEngine _engine;

    /// <summary>Creates an adapter over the hook decision engine.</summary>
    public CodexHookAdapter(IHookDecisionEngine engine)
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

        if (IsDispatchTool(tool))
        {
            return string.IsNullOrWhiteSpace(TargetOf(payload));
        }

        return true;
    }

    /// <inheritdoc/>
    public async Task<string> HandleAsync(
        JsonElement payload,
        string rawJson,
        string? renderedCaller,
        KyberWeaveConfig config,
        HookContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);
        _ = rawJson;

        string? eventName = GetString(payload, "hook_event_name");
        string? tool = GetString(payload, "tool_name");
        string caller = string.IsNullOrWhiteSpace(renderedCaller) ? "unidentified" : renderedCaller;

        if (string.Equals(eventName, "PreToolUse", StringComparison.Ordinal))
        {
            return await HandlePreAsync(payload, tool, caller, config, context, cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(eventName, "PostToolUse", StringComparison.Ordinal))
        {
            return await HandlePostAsync(payload, tool, caller, config, context, cancellationToken)
                .ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Malformed Codex hook event: unknown hook_event_name '{eventName ?? "<missing>"}'.");
    }

    /// <inheritdoc/>
    public string? RenderPromptInsertion(JsonElement payload, string? renderedCaller, HookContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = renderedCaller;

        if (!string.Equals(GetString(payload, "hook_event_name"), "PreToolUse", StringComparison.Ordinal)
            || !IsDispatchTool(GetString(payload, "tool_name")))
        {
            return null;
        }

        JsonElement toolInput = RequireToolInput(payload);
        string message = GetString(toolInput, "message") ?? string.Empty;
        DispatchInsertion insertion = DispatchPromptInsertion.Expand(message, context, GetString(toolInput, "agent_type"));
        return insertion.Kind switch
        {
            DispatchInsertionKind.Inserted => RenderPreAllowPrompt(toolInput, insertion.Text),
            DispatchInsertionKind.Stopped => RenderPreDeny(insertion.StopReason),
            _ => null,
        };
    }

    private async Task<string> HandlePreAsync(
        JsonElement payload,
        string? tool,
        string caller,
        KyberWeaveConfig config,
        HookContext context,
        CancellationToken cancellationToken)
    {
        if (!IsDispatchTool(tool))
        {
            return string.Empty;
        }

        JsonElement toolInput = RequireToolInput(payload);
        string? target = GetString(toolInput, "agent_type");
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException(
                "Malformed Codex hook event: spawn_agent input carries no agent_type.");
        }

        string message = GetString(toolInput, "message")
            ?? throw new InvalidOperationException(
                "Malformed Codex hook event: spawn_agent input carries no message.");
        HookOutcome outcome = await DecidePreAsync(
            payload, caller, target, message, config, context, cancellationToken).ConfigureAwait(false);

        // The rules judge the short dispatch first; the prompt is inserted only for a dispatch they allowed.
        DispatchInsertion insertion = DispatchPromptInsertion.Expand(message, context, target);
        if (outcome.Kind == HookOutcomeKind.Deny)
        {
            return RenderPreDeny(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine denied without a reason."));
        }

        if (insertion.Kind == DispatchInsertionKind.Stopped)
        {
            return RenderPreDeny(insertion.StopReason);
        }

        bool inserted = insertion.Kind == DispatchInsertionKind.Inserted;
        return outcome.Kind switch
        {
            HookOutcomeKind.PassThrough => inserted
                ? RenderPreAllowPrompt(toolInput, insertion.Text)
                : string.Empty,
            HookOutcomeKind.Allow => inserted
                ? RenderPreAllowPrompt(toolInput, insertion.Text)
                : ReadGuard.IsImplementationSpecialist(target)
                    ? RenderPreAllowStrip(toolInput, message)
                    : string.Empty,
            HookOutcomeKind.AllowWithRewrite => RenderPreAllowRaw(
                outcome.RewrittenInputJson ?? throw new InvalidOperationException(
                    "Hook engine returned allow-with-rewrite without rewritten input."),
                inserted ? insertion.Text : null),
            HookOutcomeKind.AllowWithNote => RenderPreAllowNote(
                toolInput,
                inserted ? insertion.Text : null,
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine allowed with a note but wrote none.")),
            HookOutcomeKind.Deny => RenderPreDeny(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine denied without a reason.")),
            _ => throw new InvalidOperationException(
                $"Hook engine returned post-dispatch outcome '{outcome.Kind}' for a pre-dispatch event."),
        };
    }

    private async Task<string> HandlePostAsync(
        JsonElement payload,
        string? tool,
        string caller,
        KyberWeaveConfig config,
        HookContext context,
        CancellationToken cancellationToken)
    {
        if (!IsDispatchTool(tool))
        {
            return string.Empty;
        }

        if (!payload.TryGetProperty("tool_response", out JsonElement response)
            || response.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Malformed Codex hook event: PostToolUse on spawn_agent carries no tool_response.");
        }

        string? target = null;
        string message = string.Empty;
        if (payload.TryGetProperty("tool_input", out JsonElement toolInput)
            && toolInput.ValueKind == JsonValueKind.Object)
        {
            target = GetString(toolInput, "agent_type");
            // The return classifies to its dispatch's trigger family, so the
            // original message rides along best effort; without it the engine
            // classifies from caller and target alone.
            message = GetString(toolInput, "message") ?? string.Empty;
        }

        HookOutcome outcome = await DecidePostAsync(
            payload, caller, target, message, response.GetRawText(), config, context, cancellationToken)
            .ConfigureAwait(false);
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

    private async Task<HookOutcome> DecidePreAsync(
        JsonElement payload,
        string caller,
        string? target,
        string message,
        KyberWeaveConfig config,
        HookContext context,
        CancellationToken cancellationToken)
    {
        if (_engine is IContextualHookDecisionEngine contextual)
        {
            return await contextual.DecidePreDispatchAsync(
                Token, caller, target, message, config, context,
                GetString(payload, "tool_use_id"), GetString(payload, "session_id"),
                cancellationToken).ConfigureAwait(false);
        }

        return await _engine.DecidePreDispatchAsync(
            caller, target, message, config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HookOutcome> DecidePostAsync(
        JsonElement payload,
        string caller,
        string? target,
        string message,
        string toolOutput,
        KyberWeaveConfig config,
        HookContext context,
        CancellationToken cancellationToken)
    {
        if (_engine is IContextualHookDecisionEngine contextual)
        {
            return await contextual.DecidePostDispatchAsync(
                Token, caller, target, message, toolOutput, config, context,
                GetString(payload, "tool_use_id"), GetString(payload, "session_id"),
                cancellationToken).ConfigureAwait(false);
        }

        return await _engine.DecidePostDispatchAsync(
            caller, target, toolOutput, config, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement RequireToolInput(JsonElement payload)
    {
        if (payload.TryGetProperty("tool_input", out JsonElement toolInput)
            && toolInput.ValueKind == JsonValueKind.Object)
        {
            return toolInput;
        }

        throw new InvalidOperationException(
            "Malformed Codex hook event: the tool event carries no tool_input object.");
    }

    private static string? TargetOf(JsonElement payload) =>
        payload.TryGetProperty("tool_input", out JsonElement toolInput)
        && toolInput.ValueKind == JsonValueKind.Object
            ? GetString(toolInput, "agent_type")
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsDispatchTool(string? tool) =>
        !string.IsNullOrEmpty(tool) && DispatchRegex().IsMatch(tool);

    private static string RenderPreAllowNote(JsonElement toolInput, string? insertedPrompt, string note)
    {
        JsonObject output = new()
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = "allow",
            ["permissionDecisionReason"] = note,
        };
        if (insertedPrompt is not null)
        {
            output["updatedInput"] = UpdatedInput(toolInput, insertedPrompt);
        }

        return new JsonObject { ["hookSpecificOutput"] = output }.ToJsonString();
    }

    private static JsonObject UpdatedInput(JsonElement toolInput, string message)
    {
        JsonObject updated = JsonNode.Parse(toolInput.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException(
                "Malformed Codex hook event: tool_input is not a JSON object.");
        updated["message"] = message;
        return updated;
    }

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

    private static string RenderPreAllowStrip(JsonElement toolInput, string message) =>
        RenderPreAllow(JsonNode.Parse(toolInput.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException(
                "Malformed Codex hook event: tool_input is not a JSON object."),
            node => node["message"] = HeaderBlock.Strip(message));

    /// <summary>The allow that writes the prompt in place of the dispatch's identity line, every other field kept.</summary>
    private static string RenderPreAllowPrompt(JsonElement toolInput, string message) =>
        RenderPreAllow(JsonNode.Parse(toolInput.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException(
                "Malformed Codex hook event: tool_input is not a JSON object."),
            node => node["message"] = message);

    private static string RenderPreAllowRaw(string rewrittenInputJson, string? insertedPrompt)
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

        return RenderPreAllow(rewritten, node =>
        {
            if (insertedPrompt is not null)
            {
                node["message"] = insertedPrompt;
            }
        });
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

    [GeneratedRegex(
        @"^(Agent|(.*[._:/])?spawn_agent)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DispatchRegex();
}
