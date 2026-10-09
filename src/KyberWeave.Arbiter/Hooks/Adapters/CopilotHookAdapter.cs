using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>
/// The Copilot hook dialects ([F2] and [F3] of the task packet): the VS Code Local
/// schema (<c>hook_event_name</c>, <c>tool_name</c>, <c>tool_input</c>,
/// <c>tool_use_id</c>) for dispatch gating on <c>runSubagent</c> and the
/// planning-path Read guard, and the Copilot CLI schema (<c>toolName</c>,
/// <c>toolArgs</c> as a JSON string or an object) for dispatch gating on
/// <c>task</c>.
/// </summary>
/// <remarks>
/// One class serves both harness tokens because VS Code also loads the Copilot CLI
/// hook file: the adapter answers in the schema of the payload received, and a
/// Local-schema payload arriving under <c>--harness copilot-cli</c> is logged as
/// harness <c>copilot-vscode</c>. The payload's caller story differs per schema:
/// the Local payload carries no caller field, so the rendered <c>--caller</c> is
/// the caller; the CLI hook is project-wide, so the caller is unidentified and
/// header inference belongs to the engine. An absent Local <c>agentName</c> means
/// the calling agent, while an absent CLI <c>agent_type</c> on a marked dispatch
/// leaves the target fact absent for the rules (which answer
/// <c>undecidable</c>); an unmarked CLI dispatch without a target passes through
/// before configuration loads. VS Code ignores matchers, so the Read guard keys
/// on the input content rather than the tool name. The CLI payload carries no
/// call id, so return pairing uses <c>pair-digest</c> at the ledger layer and the
/// adapter keeps no post state. The project-wide evaluation reuses a
/// trusted-caller decision already logged for the same <c>tool_use_id</c>:
/// trusted pre-dispatch answers are kept in memory by tool-call id, and a repeat
/// for the same id returns the stored document without re-evaluating.
/// </remarks>
public sealed class CopilotHookAdapter : IHarnessHookAdapter
{
    /// <summary>The VS Code Local-schema harness token.</summary>
    public const string VsCodeToken = "copilot-vscode";

    /// <summary>The Copilot CLI-schema harness token.</summary>
    public const string CliToken = "copilot-cli";

    private readonly IHookDecisionEngine _engine;
    private readonly string _harnessToken;

    /// <summary>
    /// Trusted-caller pre-dispatch answers by harness tool-call id. In-memory by
    /// design: the hook host is a short-lived process per event, and the entry
    /// only correlates the trusted <c>.agent.md</c> evaluation with the
    /// project-wide re-fire for the same call.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, string> TrustedDecisions = new();

    /// <summary>Creates an adapter for one Copilot harness token over the hook decision engine.</summary>
    public CopilotHookAdapter(IHookDecisionEngine engine, string harnessToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessToken);
        if (!string.Equals(harnessToken, VsCodeToken, StringComparison.Ordinal)
            && !string.Equals(harnessToken, CliToken, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unknown Copilot harness token '{harnessToken}'. Expected '{VsCodeToken}' or '{CliToken}'.",
                nameof(harnessToken));
        }

        _engine = engine;
        _harnessToken = harnessToken;
    }

    /// <inheritdoc/>
    public string HarnessToken => _harnessToken;

    /// <inheritdoc/>
    public bool IsPassThrough(JsonElement payload, string? renderedCaller)
    {
        CopilotSchema schema;
        try
        {
            schema = DetectSchema(payload);
        }
        catch (InvalidOperationException)
        {
            // Malformed is classified, not pass-through: Handle fails it closed.
            return false;
        }

        return schema == CopilotSchema.Local
            ? IsLocalPassThrough(payload, renderedCaller)
            : IsCliPassThrough(payload);
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

        CopilotSchema schema = DetectSchema(payload);
        LogSchemaDetection(schema, context);

        return schema == CopilotSchema.Local
            ? HandleLocal(payload, renderedCaller, config, context)
            : HandleCli(payload, renderedCaller, config, context);
    }

    /// <inheritdoc/>
    public string RenderFailClosed(string detail, string? renderedCaller, string rawStdin, string decisionId)
    {
        try
        {
            CopilotSchema schema = SchemaOfStdin(rawStdin);
            string envelope = FailClosedEnvelope(detail, renderedCaller, decisionId, schema);

            if (schema == CopilotSchema.Local)
            {
                return IsPostStdin(rawStdin) ? RenderLocalPostBlock(envelope) : RenderLocalPreDeny(envelope);
            }

            return RenderCliDeny(envelope);
        }
        catch (Exception)
        {
            // The block renderer never throws by contract; this is the last resort so a
            // host failure still fails closed when the renderer itself breaks.
            return string.Equals(_harnessToken, CliToken, StringComparison.Ordinal)
                ? "{\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\""
                    + HookCommand.FailClosedCode
                    + ": hook host failed (copilot-cli).\"}"
                : """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"KW-ARB-HOOK-001: hook host failed (copilot-vscode)."}}""";
        }
    }

    private HookOutcome DecidePre(
        string harness,
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
                harness, caller, target, prompt, config, context,
                GetString(payload, "tool_use_id"), sessionId: null);
        }

        return _engine.DecidePreDispatch(caller, target, prompt, config);
    }

    private HookOutcome DecidePost(
        string harness,
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
                harness, caller, target, prompt, toolOutput, config, context,
                GetString(payload, "tool_use_id"), sessionId: null);
        }

        return _engine.DecidePostDispatch(caller, target, toolOutput, config);
    }

    private void LogSchemaDetection(CopilotSchema schema, HookContext context)
    {
        string detected = schema == CopilotSchema.Local ? VsCodeToken : CliToken;
        if (!string.Equals(detected, _harnessToken, StringComparison.Ordinal))
        {
            context.Log.WriteLine(
                $"Local-schema payload under --harness '{_harnessToken}' logged as harness '{detected}': " +
                "answering in the schema received.");
        }
    }

    private string HandleLocal(JsonElement payload, string? renderedCaller, KyberWeaveConfig config, HookContext context)
    {
        string? eventName = GetString(payload, "hook_event_name");
        string? tool = GetString(payload, "tool_name");
        string caller = string.IsNullOrWhiteSpace(renderedCaller) ? "unidentified" : renderedCaller;

        if (string.Equals(eventName, "PreToolUse", StringComparison.Ordinal))
        {
            return HandleLocalPre(payload, tool, caller, renderedCaller, config, context);
        }

        if (string.Equals(eventName, "PostToolUse", StringComparison.Ordinal))
        {
            return HandleLocalPost(payload, tool, caller, config, context);
        }

        throw new InvalidOperationException(
            $"Malformed Copilot hook event: unknown hook_event_name '{eventName ?? "<missing>"}'.");
    }

    private string HandleLocalPre(
        JsonElement payload,
        string? tool,
        string caller,
        string? renderedCaller,
        KyberWeaveConfig config,
        HookContext context)
    {
        if (IsLocalDispatchTool(tool))
        {
            string? toolUseId = GetString(payload, "tool_use_id");
            if (!string.IsNullOrWhiteSpace(toolUseId)
                && TrustedDecisions.TryGetValue(toolUseId, out string? stored))
            {
                context.Log.WriteLine(
                    $"Reusing trusted-caller decision already logged for tool_use_id '{toolUseId}'.");
                return stored;
            }

            JsonElement toolInput = RequireToolInput(payload);
            string? agentName = GetString(toolInput, "agentName");
            string? target = !string.IsNullOrWhiteSpace(agentName)
                ? agentName
                : (string.Equals(caller, "unidentified", StringComparison.Ordinal) ? null : caller);
            string prompt = GetString(toolInput, "prompt")
                ?? throw new InvalidOperationException(
                    "Malformed Copilot hook event: runSubagent input carries no prompt.");

            HookOutcome outcome = DecidePre(
                _harnessToken, payload, caller, target, prompt, config, context);
            string output = outcome.Kind switch
            {
                HookOutcomeKind.PassThrough => string.Empty,
                HookOutcomeKind.Allow => ReadGuard.IsImplementationSpecialist(target)
                    ? RenderLocalPreAllowStrip(toolInput, prompt)
                    : string.Empty,
                HookOutcomeKind.AllowWithRewrite => RenderLocalPreAllowRaw(
                    outcome.RewrittenInputJson ?? throw new InvalidOperationException(
                        "Hook engine returned allow-with-rewrite without rewritten input.")),
                HookOutcomeKind.Deny => RenderLocalPreDeny(
                    outcome.Reason ?? throw new InvalidOperationException(
                        "Hook engine denied without a reason.")),
                _ => throw new InvalidOperationException(
                    $"Hook engine returned post-dispatch outcome '{outcome.Kind}' for a pre-dispatch event."),
            };

            // Only a trusted evaluation is kept: the .agent.md hook renders
            // --caller, while the project-wide re-fire carries none.
            if (!string.IsNullOrWhiteSpace(toolUseId) && !string.IsNullOrWhiteSpace(renderedCaller))
            {
                TrustedDecisions[toolUseId] = output;
            }

            return output;
        }

        // VS Code ignores matchers, so every tool reaches the hook: the guard keys
        // on the input content, not on the undocumented tool name.
        JsonElement input = RequireToolInput(payload);
        IReadOnlyList<string> dirs = ReadGuard.ProtectedDirectories(config);
        string? toolName = tool ?? "<unknown>";
        ContentGuardResult verdict = CheckContent(toolName, input, RepoRootOf(payload), dirs);
        return verdict.Allowed
            ? string.Empty
            : RenderLocalPreDeny(verdict.Reason ?? "Denied by the Read guard (Req 25.2, Req 25.4).");
    }

    private string HandleLocalPost(JsonElement payload, string? tool, string caller, KyberWeaveConfig config, HookContext context)
    {
        if (!IsLocalDispatchTool(tool))
        {
            return string.Empty;
        }

        if (!payload.TryGetProperty("tool_response", out JsonElement response)
            || response.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Malformed Copilot hook event: PostToolUse on runSubagent carries no tool_response.");
        }

        string? target = null;
        string prompt = string.Empty;
        if (payload.TryGetProperty("tool_input", out JsonElement toolInput)
            && toolInput.ValueKind == JsonValueKind.Object)
        {
            string? agentName = GetString(toolInput, "agentName");
            target = !string.IsNullOrWhiteSpace(agentName)
                ? agentName
                : (string.Equals(caller, "unidentified", StringComparison.Ordinal) ? null : caller);
            prompt = GetString(toolInput, "prompt") ?? string.Empty;
        }

        HookOutcome outcome = DecidePost(
            _harnessToken, payload, caller, target, prompt, response.GetRawText(), config, context);
        return outcome.Kind switch
        {
            HookOutcomeKind.PassThrough => string.Empty,
            HookOutcomeKind.Allow => string.Empty,
            HookOutcomeKind.Deny => RenderLocalPostBlock(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine denied without a reason.")),
            HookOutcomeKind.PostBlock => RenderLocalPostBlock(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine blocked without a reason.")),
            HookOutcomeKind.PostAnnotation => RenderLocalPostAnnotation(
                outcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine annotated without a note.")),
            _ => throw new InvalidOperationException(
                $"Hook engine returned pre-dispatch outcome '{outcome.Kind}' for a post-dispatch event."),
        };
    }

    private string HandleCli(JsonElement payload, string? renderedCaller, KyberWeaveConfig config, HookContext context)
    {
        // The CLI hook is project-wide: no caller field is documented. A rendered
        // caller is honoured when present, but the file Squad renders carries none.
        string caller = string.IsNullOrWhiteSpace(renderedCaller) ? "unidentified" : renderedCaller;
        JsonObject args = ParseCliArgs(payload);
        string? target = GetNodeString(args, "agent_type");
        string prompt = GetNodeString(args, "prompt")
            ?? throw new InvalidOperationException(
                "Malformed Copilot CLI hook event: toolArgs carry no prompt.");
        bool isPost = payload.TryGetProperty("toolResult", out _);

        if (!isPost)
        {
            HookOutcome outcome = DecidePre(
                _harnessToken, payload, caller, target, prompt, config, context);
            return outcome.Kind switch
            {
                HookOutcomeKind.PassThrough => string.Empty,
                HookOutcomeKind.Allow => ReadGuard.IsImplementationSpecialist(target)
                    && !string.Equals(HeaderBlock.Strip(prompt), prompt, StringComparison.Ordinal)
                    ? RenderCliAllowStrip(args, prompt)
                    : string.Empty,
                HookOutcomeKind.AllowWithRewrite => RenderCliAllowRewritten(
                    outcome.RewrittenInputJson ?? throw new InvalidOperationException(
                        "Hook engine returned allow-with-rewrite without rewritten input.")),
                HookOutcomeKind.Deny => RenderCliDeny(
                    outcome.Reason ?? throw new InvalidOperationException(
                        "Hook engine denied without a reason.")),
                _ => throw new InvalidOperationException(
                    $"Hook engine returned post-dispatch outcome '{outcome.Kind}' for a pre-dispatch event."),
            };
        }

        if (!payload.TryGetProperty("toolResult", out JsonElement result)
            || result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Malformed Copilot CLI hook event: postToolUse carries no toolResult object.");
        }

        HookOutcome postOutcome = DecidePost(
            _harnessToken, payload, caller, target, prompt, result.GetRawText(), config, context);
        return postOutcome.Kind switch
        {
            HookOutcomeKind.PassThrough => string.Empty,
            HookOutcomeKind.Allow => string.Empty,
            HookOutcomeKind.Deny => RenderCliPostAnnotation(
                postOutcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine denied without a reason.")),
            HookOutcomeKind.PostBlock => RenderCliPostAnnotation(
                postOutcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine blocked without a reason.")),
            HookOutcomeKind.PostAnnotation => RenderCliPostAnnotation(
                postOutcome.Reason ?? throw new InvalidOperationException(
                    "Hook engine annotated without a note.")),
            _ => throw new InvalidOperationException(
                $"Hook engine returned pre-dispatch outcome '{postOutcome.Kind}' for a post-dispatch event."),
        };
    }

    private static bool IsLocalPassThrough(JsonElement payload, string? renderedCaller)
    {
        string? eventName = GetString(payload, "hook_event_name");
        string? tool = GetString(payload, "tool_name");

        if (string.Equals(eventName, "PostToolUse", StringComparison.Ordinal))
        {
            return !IsLocalDispatchTool(tool);
        }

        if (!string.Equals(eventName, "PreToolUse", StringComparison.Ordinal))
        {
            // Unknown or missing event names are malformed, not pass-through: Handle
            // fails them closed.
            return false;
        }

        if (IsLocalDispatchTool(tool))
        {
            return false;
        }

        // The payload carries no caller field: without a rendered caller no guard
        // could tell a worker from a planner, so the event passes through.
        return !ReadGuard.IsImplementationSpecialist(
            string.IsNullOrWhiteSpace(renderedCaller) ? null : renderedCaller);
    }

    private static bool IsCliPassThrough(JsonElement payload)
    {
        string? toolName = GetString(payload, "toolName");
        if (!string.Equals(toolName, "task", StringComparison.Ordinal))
        {
            return true;
        }

        JsonObject? args = TryParseCliArgs(payload);
        if (args is null)
        {
            // Malformed is classified, not pass-through: Handle fails it closed.
            return false;
        }

        if (!string.IsNullOrWhiteSpace(GetNodeString(args, "agent_type")))
        {
            return false;
        }

        // A marked dispatch with no target is classified with the target fact
        // absent, so the rules answer undecidable. An unmarked one passes through
        // untouched before configuration loads.
        string? prompt = GetNodeString(args, "prompt");
        return prompt is null || !HeaderBlock.Parse(prompt).Headers.ContainsKey("KYBER-ARBITER");
    }

    private enum CopilotSchema
    {
        Local,
        Cli,
    }

    private static CopilotSchema DetectSchema(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Malformed Copilot hook event: expected a JSON object on stdin.");
        }

        if (payload.TryGetProperty("hook_event_name", out _))
        {
            return CopilotSchema.Local;
        }

        if (payload.TryGetProperty("toolName", out _))
        {
            return CopilotSchema.Cli;
        }

        throw new InvalidOperationException(
            "Malformed Copilot hook event: neither hook_event_name (Local schema) nor toolName (CLI schema) is present.");
    }

    private CopilotSchema SchemaOfStdin(string rawStdin)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawStdin);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return DetectSchema(doc.RootElement);
            }
        }
        catch (JsonException)
        {
            // Best effort only: the fall-through answers in this adapter's schema.
        }

        return string.Equals(_harnessToken, CliToken, StringComparison.Ordinal)
            ? CopilotSchema.Cli
            : CopilotSchema.Local;
    }

    private static bool IsPostStdin(string rawStdin)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawStdin);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && string.Equals(GetString(doc.RootElement, "hook_event_name"), "PostToolUse", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string FailClosedEnvelope(string detail, string? renderedCaller, string decisionId, CopilotSchema schema)
    {
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
        string callerText = schema == CopilotSchema.Local ? $"{caller} (copilot-vscode)" : $"{caller} (copilot-cli)";
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
        return envelope.Render();
    }

    private static bool IsLocalDispatchTool(string? tool) =>
        string.Equals(tool, "runSubagent", StringComparison.Ordinal);

    private static JsonElement RequireToolInput(JsonElement payload)
    {
        if (payload.TryGetProperty("tool_input", out JsonElement toolInput)
            && toolInput.ValueKind == JsonValueKind.Object)
        {
            return toolInput;
        }

        throw new InvalidOperationException(
            "Malformed Copilot hook event: the tool event carries no tool_input object.");
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

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetNodeString(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out JsonNode? value)
        && value is JsonValue scalar
        && scalar.TryGetValue<string>(out string? text)
            ? text
            : null;

    private static JsonObject ParseCliArgs(JsonElement payload)
    {
        JsonObject? args = TryParseCliArgs(payload);
        if (args is null)
        {
            throw new InvalidOperationException(
                "Malformed Copilot CLI hook event: toolArgs is neither a JSON object nor a JSON-encoded object string.");
        }

        return args;
    }

    private static JsonObject? TryParseCliArgs(JsonElement payload)
    {
        if (!payload.TryGetProperty("toolArgs", out JsonElement toolArgs))
        {
            return null;
        }

        try
        {
            if (toolArgs.ValueKind == JsonValueKind.Object)
            {
                return JsonNode.Parse(toolArgs.GetRawText()) as JsonObject;
            }

            if (toolArgs.ValueKind == JsonValueKind.String
                && toolArgs.GetString() is string text
                && !string.IsNullOrWhiteSpace(text))
            {
                return JsonNode.Parse(text) as JsonObject;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private sealed record ContentGuardResult(bool Allowed, string? Reason)
    {
        public static ContentGuardResult Allow { get; } = new(true, null);
    }

    private static ContentGuardResult CheckContent(
        string toolName,
        JsonElement toolInput,
        string repoRoot,
        IReadOnlyList<string> protectedDirs)
    {
        if (protectedDirs.Count == 0)
        {
            return ContentGuardResult.Allow;
        }

        foreach (string value in StringsOf(toolInput))
        {
            string forward = value.Replace('\\', '/');
            foreach (string dir in protectedDirs)
            {
                // Shell commands match by substring; path-valued inputs resolve
                // after normalisation relative to the repository root.
                if (forward.Contains(dir, StringComparison.Ordinal) || ResolvesInside(forward, repoRoot, dir))
                {
                    return new ContentGuardResult(
                        false,
                        $"Kyber-Weave Arbiter Read guard denied this {toolName} call: '{value}' " +
                        $"resolves inside protected planning directory '{dir}' (Req 25.2, Req 25.4).");
                }
            }
        }

        return ContentGuardResult.Allow;
    }

    private static IEnumerable<string> StringsOf(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (element.GetString() is string text && text.Length > 0)
                {
                    yield return text;
                }

                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    foreach (string nested in StringsOf(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    foreach (string nested in StringsOf(item))
                    {
                        yield return nested;
                    }
                }

                break;
            default:
                break;
        }
    }

    private static bool ResolvesInside(string forward, string repoRoot, string dir)
    {
        string? relative = Path.IsPathFullyQualified(forward)
            ? ToRepoRelative(forward, repoRoot)
            : NormalizeLexically(forward);
        return relative is not null
            && (string.Equals(relative, dir, StringComparison.Ordinal)
                || relative.StartsWith(dir + "/", StringComparison.Ordinal));
    }

    private static string? ToRepoRelative(string forward, string repoRoot)
    {
        string relative = Path.GetRelativePath(repoRoot, forward).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizeLexically(relative);
    }

    private static string? NormalizeLexically(string path)
    {
        List<string> parts = [];
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return string.Join("/", parts);
    }

    private static string RenderLocalPreDeny(string reason) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            },
        }.ToJsonString();

    private static string RenderLocalPreAllowStrip(JsonElement toolInput, string prompt) =>
        RenderLocalPreAllow(JsonNode.Parse(toolInput.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException(
                "Malformed Copilot hook event: tool_input is not a JSON object."),
            node => node["prompt"] = HeaderBlock.Strip(prompt));

    private static string RenderLocalPreAllowRaw(string rewrittenInputJson)
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

        return RenderLocalPreAllow(rewritten, _ => { });
    }

    private static string RenderLocalPreAllow(JsonNode updatedInput, Action<JsonObject> mutate)
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

    private static string RenderLocalPostBlock(string reason) =>
        new JsonObject
        {
            ["decision"] = "block",
            ["reason"] = reason,
        }.ToJsonString();

    private static string RenderLocalPostAnnotation(string note) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PostToolUse",
                ["additionalContext"] = note,
            },
        }.ToJsonString();

    private static string RenderCliDeny(string reason) =>
        new JsonObject
        {
            ["permissionDecision"] = "deny",
            ["permissionDecisionReason"] = reason,
        }.ToJsonString();

    private static string RenderCliAllowStrip(JsonObject args, string prompt)
    {
        JsonObject node = JsonNode.Parse(args.ToJsonString()) as JsonObject
            ?? throw new InvalidOperationException(
                "Malformed Copilot CLI hook event: toolArgs is not a JSON object.");
        node["prompt"] = HeaderBlock.Strip(prompt);
        return new JsonObject
        {
            ["permissionDecision"] = "allow",
            ["modifiedArgs"] = node,
        }.ToJsonString();
    }

    private static string RenderCliAllowRewritten(string rewrittenInputJson)
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
            ["permissionDecision"] = "allow",
            ["modifiedArgs"] = rewritten,
        }.ToJsonString();
    }

    private static string RenderCliPostAnnotation(string note) =>
        new JsonObject
        {
            ["additionalContext"] = note,
        }.ToJsonString();
}
