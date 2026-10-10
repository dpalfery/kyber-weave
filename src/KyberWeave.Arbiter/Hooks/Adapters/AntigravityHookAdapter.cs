using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>
/// The Antigravity hook dialect ([F10] of the task packet): <c>PreToolUse</c> on
/// <c>invoke_subagent</c> for dispatch gating, where each element of
/// <c>toolCall.args.Subagents</c> is one dispatch (target <c>TypeName</c>, prompt
/// <c>Prompt</c>), and <c>PostToolUse</c> on the same tool to record the return.
/// </summary>
/// <remarks>
/// A call is denied when any of its dispatches escalates, and every dispatch is
/// evaluated so each one is recorded. The harness has no rewrite field in
/// <c>hooks.json</c>, so header stripping does not apply and an allowed call writes
/// the empty document. <c>allow</c> is never written: it would auto-approve the call
/// and bypass the user's approval prompts. The call id is
/// <c>&lt;conversationId&gt;:&lt;stepIdx&gt;</c> and the repository root is
/// <c>workspacePaths[0]</c>. The hook is project-wide with no caller identity, so the
/// rendered <c>--caller</c> is honoured when present and the caller is otherwise
/// unidentified.
/// <para>
/// Every event writes a document, including a non-dispatch one. <c>hooks.json</c>
/// documents no proceed shape, so an empty stdout is not a documented allow, and the
/// plain allow shape is <c>{}</c>. The rendered matcher limits events to
/// <c>invoke_subagent</c>, so a non-dispatch event arrives only through misconfiguration.
/// </para>
/// </remarks>
public sealed class AntigravityHookAdapter : IHarnessHookAdapter
{
    /// <summary>The harness token this adapter serves.</summary>
    public const string Token = "antigravity";

    private const string DispatchTool = "invoke_subagent";

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

    /// <inheritdoc/>
    public bool IsPassThrough(JsonElement payload, string? renderedCaller)
    {
        _ = payload;
        _ = renderedCaller;
        return false;
    }

    /// <inheritdoc/>
    public string? RepoRootOf(JsonElement payload) => WorkspaceOf(payload);

    /// <inheritdoc/>
    public string? AnswerWithoutConfig(JsonElement payload, string? renderedCaller)
    {
        _ = renderedCaller;
        string? eventName = GetString(payload, "hook_event_name");
        bool classified = string.Equals(eventName, "PreToolUse", StringComparison.Ordinal)
            || string.Equals(eventName, "PostToolUse", StringComparison.Ordinal);

        // A non-dispatch event is answered exactly as Handle answers it, only earlier:
        // the rendered matcher limits events to invoke_subagent, so anything else here
        // arrives through misconfiguration and must not force a configuration load.
        // Events outside the two known event names still take the classified path, so
        // an unknown name keeps failing closed.
        return classified && !IsDispatchTool(ToolNameOf(payload)) ? EmptyDocument() : null;
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
            $"Malformed Antigravity hook event: unknown hook_event_name '{eventName ?? "<missing>"}'.");
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
        string callerText = $"{caller} (antigravity)";
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
        return RenderDeny(envelope.Render());
    }

    private string HandlePre(JsonElement payload, string? tool, string caller, KyberWeaveConfig config, HookContext context)
    {
        if (!IsDispatchTool(tool))
        {
            return EmptyDocument();
        }

        IReadOnlyList<Dispatch> dispatches = DispatchesOf(payload);
        if (dispatches.Count == 0)
        {
            return EmptyDocument();
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
                case HookOutcomeKind.Deny:
                    denial ??= outcome.Reason ?? throw new InvalidOperationException(
                        "Hook engine denied without a reason.");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Hook engine returned post-dispatch outcome '{outcome.Kind}' for a pre-dispatch event.");
            }
        }

        return denial is null ? EmptyDocument() : RenderDeny(denial);
    }

    private string HandlePost(JsonElement payload, string? tool, string caller, KyberWeaveConfig config, HookContext context)
    {
        if (!IsDispatchTool(tool))
        {
            return EmptyDocument();
        }

        IReadOnlyList<Dispatch> dispatches = DispatchesOf(payload);
        if (dispatches.Count == 0)
        {
            return EmptyDocument();
        }

        string callId = CallIdOf(payload);
        string? sessionId = GetString(payload, "conversationId");
        HookContext scoped = context with { RepoRoot = WorkspaceOf(payload) ?? context.RepoRoot };

        // The return is recorded only: a post event has no way to deliver a note, so
        // any note the engine produces goes to the diagnostics sink.
        string toolOutput = GetString(payload, "error") ?? string.Empty;
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

        if (!args.TryGetProperty("Subagents", out JsonElement subagents)
            || subagents.ValueKind != JsonValueKind.Array)
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

            string prompt = GetString(subagent, "Prompt")
                ?? throw new InvalidOperationException(
                    "Malformed Antigravity hook event: a Subagents element carries no Prompt.");
            dispatches.Add(new Dispatch(GetString(subagent, "TypeName"), prompt));
        }

        return dispatches;
    }

    private static string CallIdOf(JsonElement payload)
    {
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
            return paths[0].GetString();
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

    private static string EmptyDocument() =>
        new JsonObject().ToJsonString();

    private sealed record Dispatch(string? Target, string Prompt);
}
