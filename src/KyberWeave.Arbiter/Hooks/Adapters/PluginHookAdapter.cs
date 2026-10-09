using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>
/// One plugin harness's hook dialect (design section 1.7): the shim writes a
/// <c>kyber-arbiter.plugin-event/v1</c> envelope and reads back
/// <c>{decision: allow|block, reason, args}</c>. It blocks by throwing (OpenCode,
/// Kilo) or by returning <c>{block: true, reason}</c> (Pi), and applies a strip
/// by assigning <c>args</c>.
/// </summary>
/// <remarks>
/// The adapter is parameterised by harness token, dispatch tool, target argument
/// and prompt argument because every plugin harness renames the same shape:
/// OpenCode dispatches on <c>task</c> with the target at
/// <c>args.subagent_type</c> and the prompt at <c>args.prompt</c>. An event whose
/// tool is not the dispatch tool, or whose args name no target, is not a dispatch
/// and passes through before configuration loads. <c>args</c> is answered only
/// when the header block was stripped, as the complete arguments: the shim
/// assigns them over the call, so every original field must survive.
/// </remarks>
public sealed class PluginHookAdapter : IHarnessHookAdapter
{
    private readonly string _harnessToken;
    private readonly string _dispatchTool;
    private readonly string _targetArgument;
    private readonly string _promptArgument;
    private readonly IHookDecisionEngine _engine;

    /// <summary>
    /// Creates an adapter for one plugin harness over the hook decision engine.
    /// </summary>
    public PluginHookAdapter(
        string harnessToken,
        string dispatchTool,
        string targetArgument,
        string promptArgument,
        IHookDecisionEngine engine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchTool);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetArgument);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptArgument);
        ArgumentNullException.ThrowIfNull(engine);
        _harnessToken = harnessToken;
        _dispatchTool = dispatchTool;
        _targetArgument = targetArgument;
        _promptArgument = promptArgument;
        _engine = engine;
    }

    /// <inheritdoc/>
    public string HarnessToken => _harnessToken;

    /// <inheritdoc/>
    public bool IsPassThrough(JsonElement payload, string? renderedCaller)
    {
        _ = renderedCaller;

        PluginEnvelope envelope;
        try
        {
            envelope = PluginEnvelope.Parse(payload);
        }
        catch (InvalidOperationException)
        {
            // Malformed is classified, not pass-through: Handle fails it closed.
            return false;
        }

        if (!string.Equals(envelope.Tool, _dispatchTool, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(TargetOf(envelope));
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

        PluginEnvelope envelope = PluginEnvelope.Parse(payload);
        if (!string.Equals(envelope.Tool, _dispatchTool, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string? target = TargetOf(envelope);
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        string caller = string.IsNullOrWhiteSpace(renderedCaller) ? "unidentified" : renderedCaller;
        return envelope.Phase == PluginPhase.Before
            ? HandleBefore(envelope, caller, target, config)
            : HandleAfter(envelope, caller, target, config);
    }

    /// <inheritdoc/>
    public string RenderFailClosed(string detail, string? renderedCaller, string rawStdin, string decisionId)
    {
        _ = rawStdin;
        try
        {
            string safe = (detail ?? string.Empty)
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\n', ' ')
                .Replace('\r', ' ');
            if (string.IsNullOrWhiteSpace(safe))
            {
                safe = "unknown error";
            }

            // The host throws config errors with the code already prefixed; strip it so
            // the envelope carries the code exactly once, in both ANSWER and EVIDENCE.
            string message = safe.StartsWith(
                HookCommand.FailClosedCode + ": ", StringComparison.Ordinal)
                ? safe.Substring((HookCommand.FailClosedCode + ": ").Length)
                : safe;
            string evidence = $"{HookCommand.FailClosedCode}: {message}";

            string caller = string.IsNullOrWhiteSpace(renderedCaller) ? "unidentified" : renderedCaller;
            string trigger = string.Equals(caller, "conductor", StringComparison.Ordinal)
                ? "delegate"
                : "investigate";

            // The harness token is recorded in the caller line, mirroring the command
            // adapters' "(harness)" suffix: the block says which shim was answered.
            string callerText = $"{caller} ({_harnessToken})";
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

            return RenderBlock(envelope.Render());
        }
        catch (Exception)
        {
            // The block renderer never throws by contract; this is the last resort so a
            // host failure still fails closed when the renderer itself breaks.
            return "{\"decision\":\"block\",\"reason\":\""
                + HookCommand.FailClosedCode
                + ": hook host failed (" + _harnessToken + ").\"}";
        }
    }

    private string HandleBefore(PluginEnvelope envelope, string caller, string target, KyberWeaveConfig config)
    {
        string? prompt = ArgString(envelope.Args, _promptArgument);
        if (prompt is null)
        {
            throw new InvalidOperationException(
                $"Malformed plugin hook event: args carry no '{_promptArgument}'.");
        }

        HookOutcome outcome = _engine.DecidePreDispatch(caller, target, prompt, config);
        return outcome.Kind switch
        {
            HookOutcomeKind.PassThrough => string.Empty,
            HookOutcomeKind.Allow => ReadGuard.IsImplementationSpecialist(target)
                && !string.Equals(HeaderBlock.Strip(prompt), prompt, StringComparison.Ordinal)
                ? RenderAllowWithArgs(envelope.Args, _promptArgument, HeaderBlock.Strip(prompt))
                : RenderAllow(),
            HookOutcomeKind.AllowWithRewrite => RenderAllowWithRewritten(
                outcome.RewrittenInputJson ?? throw new InvalidOperationException(
                    "Hook engine returned allow-with-rewrite without rewritten input.")),
            HookOutcomeKind.Deny => RenderBlock(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine denied without a reason.")),
            _ => throw new InvalidOperationException(
                $"Hook engine returned post-dispatch outcome '{outcome.Kind}' for a pre-dispatch event."),
        };
    }

    private string HandleAfter(PluginEnvelope envelope, string caller, string target, KyberWeaveConfig config)
    {
        // Any non-allow outcome blocks with the envelope or note as reason: the shim
        // appends it to the tool's output. There is no annotation shape to preserve.
        string toolOutput = envelope.Result.ValueKind != JsonValueKind.Undefined
            ? envelope.Result.GetRawText()
            : envelope.Args.ValueKind == JsonValueKind.Object
                ? envelope.Args.GetRawText()
                : string.Empty;
        HookOutcome outcome = _engine.DecidePostDispatch(caller, target, toolOutput, config);
        return outcome.Kind switch
        {
            HookOutcomeKind.PassThrough => string.Empty,
            HookOutcomeKind.Allow => RenderAllow(),
            HookOutcomeKind.Deny => RenderBlock(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine denied without a reason.")),
            HookOutcomeKind.PostBlock => RenderBlock(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine blocked without a reason.")),
            HookOutcomeKind.PostAnnotation => RenderBlock(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine annotated without a note.")),
            _ => throw new InvalidOperationException(
                $"Hook engine returned pre-dispatch outcome '{outcome.Kind}' for a post-dispatch event."),
        };
    }

    private string? TargetOf(PluginEnvelope envelope) => ArgString(envelope.Args, _targetArgument);

    private static string? ArgString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RenderAllow() =>
        new JsonObject
        {
            ["decision"] = "allow",
        }.ToJsonString();

    private static string RenderBlock(string reason) =>
        new JsonObject
        {
            ["decision"] = "block",
            ["reason"] = reason,
        }.ToJsonString();

    private static string RenderAllowWithArgs(JsonElement args, string promptArgument, string strippedPrompt)
    {
        JsonObject node = JsonNode.Parse(args.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException(
                "Malformed plugin hook event: args is not a JSON object.");
        node[promptArgument] = strippedPrompt;
        return new JsonObject
        {
            ["decision"] = "allow",
            ["args"] = node,
        }.ToJsonString();
    }

    private static string RenderAllowWithRewritten(string rewrittenInputJson)
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

        if (rewritten is not JsonObject)
        {
            throw new InvalidOperationException(
                "Hook engine rewritten input must be a JSON object.");
        }

        return new JsonObject
        {
            ["decision"] = "allow",
            ["args"] = rewritten,
        }.ToJsonString();
    }
}
