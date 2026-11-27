using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>
/// The Antigravity hook dialect, as the real <c>agy</c> CLI sends it: <c>PreToolUse</c> on
/// <c>invoke_subagent</c> for dispatch gating and <c>PostToolUse</c> on the same tool to record the return.
/// </summary>
/// <remarks>
/// The event name is <c>hookEventName</c> (the older <c>hook_event_name</c> is still read). A dispatch is the flat
/// <c>toolCall.args</c> object <c>{TypeName, Prompt, Role}</c>, or each element of a <c>toolCall.args.Subagents</c>
/// list. The payload names no caller, so the owner's rule applies: a dispatch whose prompt does not start with the
/// <c>KYBER-ARBITER: true</c> marker comes from the standard Antigravity agent and passes through untouched, with an
/// explicit allow, nothing recorded and no rule applied. A marked dispatch is judged as on every harness. A call is
/// denied when any marked dispatch escalates, and every marked dispatch is evaluated so each one is recorded.
/// <para>
/// Antigravity requires a <c>decision</c> on every <c>PreToolUse</c> answer: an empty object is reported as
/// "tool call denied by pre-tool hook" with an empty reason. So every allow on a pre-tool event writes
/// <c>{"decision":"allow"}</c>, which the documentation defines as delegating to the next hook or the default policy
/// and so keeps the user's approval prompts. A <c>PostToolUse</c> answer has no decision field and is <c>{}</c>.
/// </para>
/// <para>
/// The harness's <c>overwrite</c> field replaces <c>toolCall.args</c> by a shallow top-level merge, so an inserted
/// prompt resends <c>Prompt</c> for the flat shape, or the whole <c>Subagents</c> array with each element kept and
/// only its <c>Prompt</c> replaced. The insertion document carries <c>decision: ask</c>, never <c>auto_approve</c>.
/// The call id is <c>toolCall.id</c>, else <c>&lt;conversationId&gt;:&lt;stepIdx&gt;</c>, and the repository root is
/// <c>workspacePaths[0]</c>. The rendered <c>--caller</c> is honoured when present and the caller is otherwise
/// unidentified.
/// </para>
/// </remarks>
public sealed class AntigravityHookAdapter : IHarnessHookAdapter
{
    /// <summary>The harness token this adapter serves.</summary>
    public const string Token = HarnessRegistry.AntigravityToken;

    private const string DispatchTool = "invoke_subagent";

    /// <summary>The longest tool-call id the adapter accepts from the payload; a longer one falls back to the step key.</summary>
    private const int MaxCallIdCharacters = 256;

    private readonly IHookDecisionEngine _engine;

    /// <summary>Creates an adapter over the hook decision engine.</summary>
    public AntigravityHookAdapter(IHookDecisionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <inheritdoc/>
    public string HarnessToken => Token;

    /// <remarks>
    /// Antigravity's <c>PostToolUse</c> input carries no field holding the sub-agent's
    /// result (Q17 evidence, [F10]), so returns are unobservable there.
    /// </remarks>
    public bool ObservesReturns => false;

    /// <summary>
    /// The explicit allow: even with the Arbiter disabled the host writes it, because empty output and
    /// <c>{}</c> carry no decision and Antigravity reports them as a denial.
    /// </summary>
    public string? DisabledAllowDocument => RenderAllow();

    /// <inheritdoc/>
    public string? AllowDocument(JsonElement payload) =>
        string.Equals(EventNameOf(payload), "PostToolUse", StringComparison.Ordinal)
            ? EmptyDocument()
            : RenderAllow();

    /// <summary>
    /// A dispatch with no marked prompt is the standard Antigravity agent's: it passes through untouched. Anything
    /// malformed is not decided here, so the host reports it and allows.
    /// </summary>
    public bool IsPassThrough(JsonElement payload, string? renderedCaller)
    {
        _ = renderedCaller;
        if (!IsKnownEvent(EventNameOf(payload)) || !IsDispatchTool(ToolNameOf(payload)))
        {
            return false;
        }

        try
        {
            // The same strict reader the decided path uses: a malformed dispatch is not the
            // standard agent's, so it must be decided rather than passed through.
            return MarkedDispatchesOf(payload).Count == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public string? RepoRootOf(JsonElement payload) => WorkspaceOf(payload);

    /// <inheritdoc/>
    public string? AnswerWithoutConfig(JsonElement payload, string? renderedCaller)
    {
        _ = renderedCaller;
        bool classified = IsKnownEvent(EventNameOf(payload));

        // A non-dispatch event is answered exactly as Handle answers it, only earlier:
        // the rendered matcher limits events to invoke_subagent, so anything else here
        // arrives through misconfiguration and must not force a configuration load.
        // Events outside the two known event names still take the classified path, so
        // an unknown name keeps failing closed.
        return classified && !IsDispatchTool(ToolNameOf(payload)) ? AllowDocument(payload) : null;
    }

    /// <inheritdoc/>
    public string? RenderPromptInsertion(JsonElement payload, string? renderedCaller, HookContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = renderedCaller;

        return string.Equals(EventNameOf(payload), "PreToolUse", StringComparison.Ordinal)
            && IsDispatchTool(ToolNameOf(payload))
                ? InsertionDocument(payload, context)
                : null;
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

        string? eventName = EventNameOf(payload);
        string? tool = ToolNameOf(payload);
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
            $"Malformed Antigravity hook event: unknown hookEventName '{eventName ?? "<missing>"}'.");
    }

    private string HandlePre(JsonElement payload, string? tool, string caller, KyberWeaveConfig config, HookContext context)
    {
        if (!IsDispatchTool(tool))
        {
            return RenderAllow();
        }

        IReadOnlyList<Dispatch> dispatches = MarkedDispatchesOf(payload);
        if (dispatches.Count == 0)
        {
            return RenderAllow();
        }

        string callId = CallIdOf(payload);
        string? sessionId = GetString(payload, "conversationId");
        HookContext scoped = context with { RepoRoot = WorkspaceOf(payload) ?? context.RepoRoot };

        string? denial = null;
        foreach (Dispatch dispatch in dispatches)
        {
            HookOutcome outcome = DecidePre(caller, dispatch, callId, sessionId, config, scoped);
            switch (outcome.Kind)
            {
                case HookOutcomeKind.PassThrough:
                case HookOutcomeKind.Allow:
                case HookOutcomeKind.AllowWithRewrite:
                    break;
                case HookOutcomeKind.AllowWithNote:
                    scoped.Log.WriteLine(outcome.Reason ?? throw new InvalidOperationException(
                        "Hook engine allowed with a note but wrote none."));
                    break;
                case HookOutcomeKind.Deny:
                    denial ??= outcome.Reason ?? throw new InvalidOperationException(
                        "Hook engine denied without a reason.");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Hook engine returned post-dispatch outcome '{outcome.Kind}' for a pre-dispatch event.");
            }
        }

        return denial is not null
            ? RenderDeny(denial)
            : InsertionDocument(payload, context) ?? RenderAllow();
    }

    /// <summary>
    /// The allow document that carries every dispatch's inserted prompt. Antigravity merges <c>overwrite</c> into
    /// <c>toolCall.args</c> at the top level, so the whole <c>Subagents</c> array is resent, every element kept and
    /// only its <c>Prompt</c> replaced. <c>decision</c> is required, and <c>ask</c> keeps the user's approval prompt.
    /// </summary>
    /// <param name="payload">The dispatch event.</param>
    /// <param name="context">The hook context carrying the prompt service.</param>
    /// <returns>The overwrite document, a deny when a prompt cannot be built, or null when no dispatch needs one.</returns>
    private static string? InsertionDocument(JsonElement payload, HookContext context)
    {
        if (!payload.TryGetProperty("toolCall", out JsonElement toolCall)
            || toolCall.ValueKind != JsonValueKind.Object
            || !toolCall.TryGetProperty("args", out JsonElement args)
            || args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!args.TryGetProperty("Subagents", out JsonElement subagents))
        {
            return FlatInsertion(args, context);
        }

        if (subagents.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonArray rewritten = [];
        bool subagentsInserted = false;
        foreach (JsonElement subagent in subagents.EnumerateArray())
        {
            JsonObject copy = JsonNode.Parse(subagent.GetRawText()) as JsonObject
                ?? throw new InvalidOperationException(
                    "Malformed Antigravity hook event: a Subagents element is not an object.");
            string prompt = GetString(subagent, "Prompt") ?? string.Empty;
            string? subagentType = GetString(subagent, "TypeName");
            if (IsMarked(prompt))
            {
                DispatchInsertion insertion = DispatchPromptInsertion.Expand(prompt, context, subagentType);
                if (insertion.Kind == DispatchInsertionKind.Stopped)
                {
                    return RenderDeny(StopReason(insertion));
                }

                if (insertion.Kind == DispatchInsertionKind.Inserted)
                {
                    copy["Prompt"] = insertion.Text;
                    subagentsInserted = true;
                }
            }

            rewritten.Add(copy);
        }

        // A payload may carry both shapes; the flat prompt is inserted too, never dropped.
        JsonObject overwrite = [];
        if (subagentsInserted)
        {
            overwrite["Subagents"] = rewritten;
        }

        string flatPrompt = GetString(args, "Prompt") ?? string.Empty;
        string? flatType = GetString(args, "TypeName");
        if (IsMarked(flatPrompt))
        {
            DispatchInsertion flat = DispatchPromptInsertion.Expand(flatPrompt, context, flatType);
            if (flat.Kind == DispatchInsertionKind.Stopped)
            {
                return RenderDeny(StopReason(flat));
            }

            if (flat.Kind == DispatchInsertionKind.Inserted)
            {
                overwrite["Prompt"] = flat.Text;
            }
        }

        return overwrite.Count > 0
            ? new JsonObject { ["decision"] = "ask", ["overwrite"] = overwrite }.ToJsonString()
            : null;
    }

    private static string? FlatInsertion(JsonElement args, HookContext context)
    {
        string prompt = GetString(args, "Prompt") ?? string.Empty;
        string? type = GetString(args, "TypeName");
        if (!IsMarked(prompt))
        {
            return null;
        }

        DispatchInsertion insertion = DispatchPromptInsertion.Expand(prompt, context, type);
        return insertion.Kind switch
        {
            DispatchInsertionKind.Stopped => RenderDeny(StopReason(insertion)),
            DispatchInsertionKind.Inserted => new JsonObject
            {
                ["decision"] = "ask",
                ["overwrite"] = new JsonObject { ["Prompt"] = insertion.Text },
            }.ToJsonString(),
            _ => null,
        };
    }

    private string HandlePost(JsonElement payload, string? tool, string caller, KyberWeaveConfig config, HookContext context)
    {
        if (!IsDispatchTool(tool))
        {
            return EmptyDocument();
        }

        HookContext scoped = context with { RepoRoot = WorkspaceOf(payload) ?? context.RepoRoot };

        // A post event never blocks: one that cannot be read is logged and answered like any other.
        IReadOnlyList<Dispatch> dispatches;
        string callId;
        try
        {
            dispatches = MarkedDispatchesOf(payload);
            callId = CallIdOf(payload);
        }
        catch (InvalidOperationException ex)
        {
            scoped.Log.WriteLine($"{HookCommand.FailClosedCode}: {ex.Message} The return is not recorded.");
            return EmptyDocument();
        }

        if (dispatches.Count == 0)
        {
            return EmptyDocument();
        }

        string? sessionId = GetString(payload, "conversationId");

        // The return is recorded only: a post event has no way to deliver a note, so
        // any note the engine produces goes to the diagnostics sink.
        string toolOutput = FirstNonEmpty(GetString(payload, "error"), GetString(payload, "result"));
        foreach (Dispatch dispatch in dispatches)
        {
            HookOutcome outcome = DecidePost(caller, dispatch, callId, sessionId, toolOutput, config, scoped);
            switch (outcome.Kind)
            {
                case HookOutcomeKind.PassThrough:
                case HookOutcomeKind.Allow:
                    break;
                case HookOutcomeKind.Deny:
                case HookOutcomeKind.PostBlock:
                case HookOutcomeKind.PostAnnotation:
                case HookOutcomeKind.AllowWithNote:
                    scoped.Log.WriteLine(outcome.Reason ?? throw new InvalidOperationException(
                        "Hook engine returned a post-dispatch note without text."));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Hook engine returned pre-dispatch outcome '{outcome.Kind}' for a post-dispatch event.");
            }
        }

        return EmptyDocument();
    }

    private HookOutcome DecidePre(
        string caller,
        Dispatch dispatch,
        string callId,
        string? sessionId,
        KyberWeaveConfig config,
        HookContext context)
    {
        if (_engine is IContextualHookDecisionEngine contextual)
        {
            return contextual.DecidePreDispatch(
                Token, caller, dispatch.Target, dispatch.Prompt, config, context, callId, sessionId);
        }

        return _engine.DecidePreDispatch(caller, dispatch.Target, dispatch.Prompt, config);
    }

    private HookOutcome DecidePost(
        string caller,
        Dispatch dispatch,
        string callId,
        string? sessionId,
        string toolOutput,
        KyberWeaveConfig config,
        HookContext context)
    {
        if (_engine is IContextualHookDecisionEngine contextual)
        {
            return contextual.DecidePostDispatch(
                Token, caller, dispatch.Target, dispatch.Prompt, toolOutput, config, context, callId, sessionId);
        }

        return _engine.DecidePostDispatch(caller, dispatch.Target, toolOutput, config);
    }

    private static IReadOnlyList<Dispatch> MarkedDispatchesOf(JsonElement payload) =>
        [.. DispatchesOf(payload).Where(dispatch => IsMarked(dispatch.Prompt))];

    private static IReadOnlyList<Dispatch> DispatchesOf(JsonElement payload)
    {
        if (!payload.TryGetProperty("toolCall", out JsonElement toolCall)
            || toolCall.ValueKind != JsonValueKind.Object
            || !toolCall.TryGetProperty("args", out JsonElement args)
            || args.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Malformed Antigravity hook event: the tool event carries no toolCall.args object.");
        }

        if (!args.TryGetProperty("Subagents", out JsonElement subagents))
        {
            return [ReadDispatch(args)];
        }

        if (subagents.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Malformed Antigravity hook event: invoke_subagent args carry no Subagents array.");
        }

        List<Dispatch> dispatches = [];
        foreach (JsonElement subagent in subagents.EnumerateArray())
        {
            if (subagent.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Malformed Antigravity hook event: a Subagents element is not an object.");
            }

            dispatches.Add(ReadDispatch(subagent));
        }

        // A flat Prompt beside a Subagents array is a dispatch too: judging only the list would let it through.
        if (args.TryGetProperty("Prompt", out _))
        {
            dispatches.Add(ReadDispatch(args));
        }

        return dispatches;
    }

    private static Dispatch ReadDispatch(JsonElement spec)
    {
        string? prompt = GetString(spec, "Prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException(
                "Malformed Antigravity hook event: a dispatch carries no Prompt.");
        }

        string? target = GetString(spec, "TypeName");
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException(
                "Malformed Antigravity hook event: a dispatch carries no TypeName.");
        }

        return new Dispatch(target, prompt);
    }

    private static bool IsMarked(string prompt) => HeaderBlock.Parse(prompt).IsMarked;

    private static string? EventNameOf(JsonElement payload)
    {
        string? camel = GetString(payload, "hookEventName");
        string? snake = GetString(payload, "hook_event_name");
        if (camel is not null && snake is not null && !string.Equals(camel, snake, StringComparison.Ordinal))
        {
            // Two names that disagree cannot say which event this is; treating either as
            // authoritative would let a post event masquerade as a pre event, so it is unknown.
            return null;
        }

        return camel ?? snake;
    }

    private static bool IsKnownEvent(string? eventName) =>
        string.Equals(eventName, "PreToolUse", StringComparison.Ordinal)
        || string.Equals(eventName, "PostToolUse", StringComparison.Ordinal);

    private static string CallIdOf(JsonElement payload)
    {
        if (payload.TryGetProperty("toolCall", out JsonElement call)
            && GetString(call, "id") is { } toolCallId
            && !string.IsNullOrWhiteSpace(toolCallId)
            && toolCallId.Length <= MaxCallIdCharacters)
        {
            return toolCallId;
        }

        string conversation = GetString(payload, "conversationId")
            ?? throw new InvalidOperationException(
                "Malformed Antigravity hook event: the tool event carries no conversationId.");
        if (!payload.TryGetProperty("stepIdx", out JsonElement step)
            || step.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException(
                "Malformed Antigravity hook event: the tool event carries no stepIdx.");
        }

        return $"{conversation}:{step.GetRawText()}";
    }

    private static string? WorkspaceOf(JsonElement payload)
    {
        if (payload.TryGetProperty("workspacePaths", out JsonElement paths)
            && paths.ValueKind == JsonValueKind.Array
            && paths.GetArrayLength() > 0
            && paths[0].ValueKind == JsonValueKind.String)
        {
            // A blank workspace path names no repository. Returning it verbatim makes
            // the root "" and reads the host configuration from the process directory,
            // which is the one root the payload cwd exists to avoid, so a blank is
            // reported as absent and the caller falls through to it.
            string? workspace = paths[0].GetString();
            return string.IsNullOrWhiteSpace(workspace) ? null : workspace;
        }

        return null;
    }

    private static string? ToolNameOf(JsonElement payload) =>
        payload.TryGetProperty("toolCall", out JsonElement toolCall)
        && toolCall.ValueKind == JsonValueKind.Object
            ? GetString(toolCall, "name")
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsDispatchTool(string? tool) =>
        string.Equals(tool, DispatchTool, StringComparison.Ordinal);

    private static string RenderDeny(string reason) =>
        new JsonObject
        {
            ["decision"] = "deny",
            ["reason"] = reason,
        }.ToJsonString();

    /// <summary>The reason a stopped insertion carries; only a <see cref="DispatchInsertionKind.Stopped"/> asks for one.</summary>
    private static string StopReason(DispatchInsertion insertion) =>
        insertion.Reason ?? throw new InvalidOperationException(
            "A dispatch insertion stopped without a reason.");

    /// <summary>The first value that carries text, so an empty <c>error</c> never hides a non-empty <c>result</c>.</summary>
    /// <param name="values">The candidate values, in priority order.</param>
    /// <returns>The first non-blank value, or an empty string.</returns>
    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string RenderAllow() =>
        new JsonObject { ["decision"] = "allow" }.ToJsonString();

    private static string EmptyDocument() =>
        new JsonObject().ToJsonString();

    private sealed record Dispatch(string? Target, string Prompt);
}
