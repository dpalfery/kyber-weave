using System.Globalization;
using System.Numerics;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Typed native representation of a decoded <see cref="OtlpSpan"/> extracted using the canonical
/// OTLP attribute key lists from <see cref="CanonAdapters"/> and KyberDash OTLP conventions.
/// </summary>
/// <param name="SpanId">Span identifier.</param>
/// <param name="TraceId">Optional trace identifier.</param>
/// <param name="ParentSpanId">Optional parent span identifier.</param>
/// <param name="Source">Emitting <c>service.name</c>, or <c>"otlp"</c> when absent.</param>
/// <param name="Name">Original span name.</param>
/// <param name="Kind">Normalized lowercase OTLP span kind.</param>
/// <param name="StartedAt">Parsed UTC start timestamp.</param>
/// <param name="EndedAt">Optional UTC end timestamp derived when duration is positive.</param>
/// <param name="ReportedDurationMs">Optional non-negative rounded duration in milliseconds.</param>
/// <param name="StatusCode">Normalized lowercase OTLP status code.</param>
/// <param name="StatusMessage">Optional OTLP status message.</param>
/// <param name="OperationName">Optional <c>gen_ai.operation.name</c> value.</param>
/// <param name="SpanType">Optional <c>span.type</c> discriminator.</param>
/// <param name="SessionId">Extracted native session/conversation identifier, if present.</param>
/// <param name="Model">Extracted model identifier, if present.</param>
/// <param name="InputTokens">Optional input token counter.</param>
/// <param name="OutputTokens">Optional output token counter.</param>
/// <param name="CacheReadTokens">Optional cache-read token counter.</param>
/// <param name="CacheCreationTokens">Optional cache-creation token counter.</param>
/// <param name="ReasoningTokens">Optional reasoning token counter.</param>
/// <param name="ThoughtTokens">Optional Gemini thought token counter.</param>
/// <param name="ReportedCostUsd">Optional harness-reported finite USD cost.</param>
/// <param name="DeclaredContextWindow">Optional harness-declared context window size.</param>
/// <param name="ToolName">Optional invoked tool name.</param>
/// <param name="ToolCallId">Optional tool call correlation identifier.</param>
/// <param name="ToolMcpServerName">Optional MCP server name for the tool call.</param>
/// <param name="ToolMcpToolName">Optional MCP tool name for the tool call.</param>
/// <param name="ToolRawInput">Optional raw tool arguments or input payload.</param>
/// <param name="ToolRawResult">Optional raw tool result or output payload.</param>
/// <param name="ToolStatus">Optional tool status string.</param>
/// <param name="ToolIsError">Optional boolean tool error flag.</param>
/// <param name="ToolErrorCode">Optional tool error code or type.</param>
/// <param name="ToolExitCode">Optional tool process exit code.</param>
/// <param name="AgentName">Optional invoked agent name.</param>
/// <param name="AgentInstanceId">Optional agent instance identifier.</param>
/// <param name="AgentSpawningCallId">Optional spawning record identifier.</param>
/// <param name="AgentStatus">Optional agent invocation status string.</param>
/// <param name="AgentError">Optional agent invocation error message.</param>
/// <param name="ErrorMessage">Optional record-level error message.</param>
/// <param name="EditFailures">Optional edit failure count.</param>
/// <param name="CorrectionRule">Optional user-correction rule identifier.</param>
/// <param name="SkillsInvoked">Invoked skill names extracted from span attributes.</param>
/// <param name="Parts">Canonical content parts extracted from span attributes.</param>
/// <param name="Attributes">Cloned attribute dictionary from the source span.</param>
public record OtlpSpanNative(
    string SpanId,
    string? TraceId,
    string? ParentSpanId,
    string Source,
    string Name,
    string Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long? ReportedDurationMs,
    string StatusCode,
    string? StatusMessage,
    string? OperationName,
    string? SpanType,
    string? SessionId,
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheCreationTokens,
    long? ReasoningTokens,
    long? ThoughtTokens,
    double? ReportedCostUsd,
    long? DeclaredContextWindow,
    string? ToolName,
    string? ToolCallId,
    string? ToolMcpServerName,
    string? ToolMcpToolName,
    string? ToolRawInput,
    string? ToolRawResult,
    string? ToolStatus,
    bool? ToolIsError,
    string? ToolErrorCode,
    long? ToolExitCode,
    string? AgentName,
    string? AgentInstanceId,
    string? AgentSpawningCallId,
    string? AgentStatus,
    string? AgentError,
    string? ErrorMessage,
    long? EditFailures,
    string? CorrectionRule,
    IReadOnlyList<string> SkillsInvoked,
    IReadOnlyList<ContentPart> Parts,
    IReadOnlyDictionary<string, JsonElement> Attributes)
{
    private const string UnnamedSource = "otlp";

    /// <summary>Attribute key naming the OpenCode plugin wire contract version.</summary>
    public const string OpencodeContractVersionKey = CanonAdapters.OpencodeContractVersionKey;

    /// <summary>Attribute key naming the Factory Droid plugin wire contract version.</summary>
    public const string DroidContractVersionKey = CanonAdapters.DroidContractVersionKey;

    /// <summary>Attribute key naming the GenAI operation (<c>"invoke_agent"</c>, etc.).</summary>
    public const string OperationNameKey = "gen_ai.operation.name";

    /// <summary>Attribute key naming the span type discriminator (<c>"tool"</c>, <c>"interaction"</c>, etc.).</summary>
    public const string SpanTypeKey = "span.type";

    /// <summary>Canonical input token attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> InputTokenKeys = ["gen_ai.usage.input_tokens", "input_tokens"];

    /// <summary>Canonical output token attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> OutputTokenKeys = ["gen_ai.usage.output_tokens", "output_tokens"];

    /// <summary>Canonical cache-read token attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> CacheReadKeys =
    [
        "gen_ai.usage.cache_read.input_tokens",
        "gen_ai.usage.cache_read_input_tokens",
        "gen_ai.usage.cached_tokens",
        "cache_read_tokens",
    ];

    /// <summary>Canonical cache-creation token attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> CacheCreationKeys =
    [
        "gen_ai.usage.cache_creation.input_tokens",
        "gen_ai.usage.cache_creation_input_tokens",
        "cache_creation_tokens",
    ];

    /// <summary>Canonical reasoning token attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> ReasoningKeys = ["gen_ai.usage.reasoning_tokens"];

    /// <summary>Gemini/Antigravity thought token attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> ThoughtKeys = ["gemini.usage.thoughts_tokens"];

    /// <summary>Codex OTLP cache-write token attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> CodexCacheWriteKeys = ["gen_ai.usage.cache_write.input_tokens"];

    /// <summary>Codex OTLP reasoning token attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> CodexReasoningKeys = ["codex.usage.reasoning_output_tokens", "gen_ai.usage.reasoning_tokens"];

    /// <summary>Canonical session identifier attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> SessionIdKeys =
    [
        "gen_ai.session.id",
        "session.id",
        "copilot_chat.chat_session_id",
        "copilot_chat.session_id",
        "gen_ai.conversation.id",
    ];

    /// <summary>Claude-specific session identifier fallback keys.</summary>
    public static readonly IReadOnlyList<string> ClaudeSessionKeys = ["claude.session_id", "claude_code.session_id"];

    /// <summary>Canonical system instruction attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> SystemInstructionKeys = ["gen_ai.system_instructions"];

    /// <summary>Canonical input message array attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> InputMessageKeys = ["gen_ai.input.messages"];

    /// <summary>Canonical instruction rule attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> InstructionRuleKeys = ["gen_ai.rules"];

    /// <summary>Canonical listed-skill context attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> SkillKeys = ["gen_ai.skills"];

    /// <summary>Canonical tool definitions attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> ToolDefinitionKeys = ["gen_ai.tool.definitions", "gen_ai.request.tools"];

    /// <summary>Canonical tool result attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> ToolResultKeys = ["gen_ai.tool.call.result"];

    /// <summary>Canonical flat prompt attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> FlatPromptKeys = ["gen_ai.prompt"];

    /// <summary>JSON property names identifying an MCP server inside a tool definition entry.</summary>
    public static readonly IReadOnlyList<string> ToolServerFields = ["server", "mcp_server", "mcpServer", "server_name"];

    /// <summary>Canonical system-prompt token count attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> SystemTokenKeys = ["gen_ai.usage.sys_tokens"];

    /// <summary>Canonical tool-definition token count attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> ToolDefinitionTokenKeys = ["gen_ai.usage.tool_tokens"];

    /// <summary>Canonical skill-context token count attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> SkillTokenKeys = ["gen_ai.usage.skill_tokens"];

    /// <summary>Canonical instruction-rule token count attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> RuleTokenKeys = ["gen_ai.usage.rule_tokens"];

    /// <summary>Canonical input-message token count attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> MessageTokenKeys = ["gen_ai.usage.msg_tokens"];

    /// <summary>Canonical model identifier attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> CanonicalModelKeys = ["gen_ai.response.model", "gen_ai.request.model", "model"];

    /// <summary>Factory Droid session identifier attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> DroidSessionKeys = ["factory.session.id", "factory.session_id", "factory.sessionId"];

    /// <summary>Factory Droid model identifier attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> DroidModelKeys = ["factory.model.id", "factory.model"];

    /// <summary>Factory Droid tool name attribute keys from <see cref="CanonAdapters"/>.</summary>
    public static readonly IReadOnlyList<string> DroidToolNameKeys = ["factory.tool.name", "factory.tool_name"];

    /// <summary>OTLP tool name attribute keys.</summary>
    public static readonly IReadOnlyList<string> ToolNameKeys = ["gen_ai.tool.name", "tool.name", "tool_name"];

    /// <summary>OTLP tool call correlation identifier attribute keys.</summary>
    public static readonly IReadOnlyList<string> ToolCallIdKeys =
    [
        "gen_ai.tool.call.id",
        "gen_ai.tool.call_id",
        "tool.call.id",
        "tool_call_id",
        "call_id",
    ];

    /// <summary>OTLP MCP server name attribute keys.</summary>
    public static readonly IReadOnlyList<string> ToolMcpServerKeys =
    [
        "gen_ai.tool.mcp.server",
        "mcp.server.name",
        "mcp_server",
        "server_name",
    ];

    /// <summary>OTLP MCP tool name attribute keys.</summary>
    public static readonly IReadOnlyList<string> ToolMcpToolKeys =
    [
        "gen_ai.tool.mcp.tool",
        "mcp.tool.name",
        "mcp_tool_name",
    ];

    /// <summary>OTLP tool input/arguments attribute keys.</summary>
    public static readonly IReadOnlyList<string> ToolInputKeys =
    [
        "gen_ai.tool.call.arguments",
        "gen_ai.tool.arguments",
        "tool.arguments",
        "tool_arguments",
        "arguments",
        "input",
    ];

    /// <summary>OTLP tool result/output attribute keys.</summary>
    public static readonly IReadOnlyList<string> ToolResultAttributeKeys =
    [
        "gen_ai.tool.call.result",
        "gen_ai.tool.result",
        "tool.result",
        "tool_result",
        "output",
    ];

    /// <summary>OTLP tool status attribute keys.</summary>
    public static readonly IReadOnlyList<string> ToolStatusKeys = ["gen_ai.tool.status", "status"];

    /// <summary>OTLP tool error code attribute keys.</summary>
    public static readonly IReadOnlyList<string> ToolErrorCodeKeys = ["gen_ai.tool.error.code", "error.code", "error.type"];

    /// <summary>OTLP tool process exit code attribute keys.</summary>
    public static readonly IReadOnlyList<string> ToolExitCodeKeys = ["gen_ai.tool.exit_code", "tool.exit_code", "exit_code"];

    /// <summary>OTLP agent name attribute keys.</summary>
    public static readonly IReadOnlyList<string> AgentNameKeys =
    [
        "gen_ai.agent.name",
        "agent.name",
        "agentName",
        "agent_name",
        "gen_ai.agent.type",
        "agent_type",
        "agentType",
        "agent",
    ];

    /// <summary>OTLP agent instance identifier attribute keys.</summary>
    public static readonly IReadOnlyList<string> AgentIdKeys =
    [
        "gen_ai.agent.id",
        "agent.id",
        "agent_id",
        "agentId",
        "subagent_id",
        "subagentId",
    ];

    /// <summary>OTLP spawning record identifier attribute keys for agent invocations.</summary>
    public static readonly IReadOnlyList<string> AgentSpawningRecordIdKeys =
    [
        "gen_ai.agent.spawning_record_id",
        "spawning_record_id",
        "spawning_call_id",
    ];

    /// <summary>OTLP agent invocation status attribute keys.</summary>
    public static readonly IReadOnlyList<string> AgentStatusKeys = ["gen_ai.agent.status"];

    /// <summary>OTLP agent invocation error message attribute keys.</summary>
    public static readonly IReadOnlyList<string> AgentErrorKeys = ["gen_ai.agent.error"];

    /// <summary>OTLP error message attribute keys.</summary>
    public static readonly IReadOnlyList<string> ErrorMessageKeys =
    [
        "error.message",
        "gen_ai.tool.error.message",
        "tool.error.message",
    ];

    /// <summary>OTLP declared context window attribute keys.</summary>
    public static readonly IReadOnlyList<string> DeclaredContextWindowKeys =
    [
        "gen_ai.request.max_context_tokens",
        "gen_ai.request.context_window",
    ];

    /// <summary>OTLP harness-reported USD cost attribute keys.</summary>
    public static readonly IReadOnlyList<string> CostUsdKeys = ["gen_ai.usage.cost", "cost_usd", "cost"];

    /// <summary>OTLP invoked skill attribute keys.</summary>
    public static readonly IReadOnlyList<string> SkillInvokedKeys = ["gen_ai.skill.name", "skill.name", "skill_name"];

    /// <summary>OTLP edit failure count attribute keys.</summary>
    public static readonly IReadOnlyList<string> EditFailureKeys = ["gen_ai.edit.failures", "edit_failures"];

    /// <summary>OTLP user correction rule attribute keys.</summary>
    public static readonly IReadOnlyList<string> CorrectionRuleKeys = ["gen_ai.correction.rule", "correction_rule"];

    /// <summary>
    /// Parses a decoded <see cref="OtlpSpan"/> into an <see cref="OtlpSpanNative"/> using the
    /// canonical key lists and attribute extraction rules.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Typed native OTLP span.</returns>
    public static OtlpSpanNative Parse(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);

        IReadOnlyDictionary<string, JsonElement> attributes = span.Attributes
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, JsonElement> resource = span.Resource
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        Dictionary<string, JsonElement> clonedAttributes = new(attributes.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> entry in attributes)
        {
            clonedAttributes[entry.Key] = entry.Value.Clone();
        }

        string source = FirstSafeString(resource, ["service.name"]) ?? UnnamedSource;
        DateTimeOffset startedAt = ParseTimestamp(span.Timestamp, span.StartTimeUnixNano);
        long? reportedDurationMs = double.IsFinite(span.DurationMs) && span.DurationMs >= 0
            ? (long)Math.Round(span.DurationMs, MidpointRounding.AwayFromZero)
            : null;
        DateTimeOffset? endedAt = reportedDurationMs is > 0
            ? startedAt.AddMilliseconds(reportedDurationMs.Value)
            : null;

        string? sessionId = FirstSafeString(clonedAttributes, SessionIdKeys)
            ?? FirstSafeString(clonedAttributes, ClaudeSessionKeys);

        long? declaredContextWindow = ReadOptionalCounter(clonedAttributes, DeclaredContextWindowKeys);
        if (declaredContextWindow is <= 0)
        {
            declaredContextWindow = null;
        }

        long? editFailures = ReadOptionalCounter(clonedAttributes, EditFailureKeys);
        if (editFailures is < 0)
        {
            editFailures = null;
        }

        return new OtlpSpanNative(
            SpanId: span.SpanId,
            TraceId: SafeOptionalText(span.TraceId),
            ParentSpanId: SafeOptionalText(span.ParentSpanId),
            Source: source,
            Name: span.Name,
            Kind: span.Kind,
            StartedAt: startedAt,
            EndedAt: endedAt,
            ReportedDurationMs: reportedDurationMs,
            StatusCode: span.Status.Code,
            StatusMessage: SafeOptionalText(span.Status.Message),
            OperationName: FirstSafeString(clonedAttributes, [OperationNameKey]),
            SpanType: FirstSafeString(clonedAttributes, [SpanTypeKey]),
            SessionId: sessionId,
            Model: FirstSafeString(clonedAttributes, CanonicalModelKeys),
            InputTokens: ReadOptionalCounter(clonedAttributes, InputTokenKeys),
            OutputTokens: ReadOptionalCounter(clonedAttributes, OutputTokenKeys),
            CacheReadTokens: ReadOptionalCounter(clonedAttributes, CacheReadKeys),
            CacheCreationTokens: ReadOptionalCounter(clonedAttributes, CacheCreationKeys),
            ReasoningTokens: ReadOptionalCounter(clonedAttributes, ReasoningKeys),
            ThoughtTokens: ReadOptionalCounter(clonedAttributes, ThoughtKeys),
            ReportedCostUsd: ReadFiniteDouble(clonedAttributes, CostUsdKeys),
            DeclaredContextWindow: declaredContextWindow,
            ToolName: FirstSafeString(clonedAttributes, ToolNameKeys),
            ToolCallId: FirstSafeString(clonedAttributes, ToolCallIdKeys),
            ToolMcpServerName: FirstSafeString(clonedAttributes, ToolMcpServerKeys),
            ToolMcpToolName: FirstSafeString(clonedAttributes, ToolMcpToolKeys),
            ToolRawInput: ReadRawTextOrJson(clonedAttributes, ToolInputKeys),
            ToolRawResult: ReadRawTextOrJson(clonedAttributes, ToolResultAttributeKeys),
            ToolStatus: FirstSafeString(clonedAttributes, ToolStatusKeys),
            ToolIsError: ReadOptionalBool(clonedAttributes, ["is_error", "isError"]),
            ToolErrorCode: FirstSafeString(clonedAttributes, ToolErrorCodeKeys),
            ToolExitCode: ReadOptionalCounter(clonedAttributes, ToolExitCodeKeys),
            AgentName: FirstSafeString(clonedAttributes, AgentNameKeys),
            AgentInstanceId: FirstSafeString(clonedAttributes, AgentIdKeys),
            AgentSpawningCallId: FirstSafeString(clonedAttributes, AgentSpawningRecordIdKeys),
            AgentStatus: FirstSafeString(clonedAttributes, AgentStatusKeys),
            AgentError: FirstSafeString(clonedAttributes, AgentErrorKeys),
            ErrorMessage: FirstSafeString(clonedAttributes, ErrorMessageKeys),
            EditFailures: editFailures,
            CorrectionRule: FirstSafeString(clonedAttributes, CorrectionRuleKeys),
            SkillsInvoked: ReadSafeStringList(clonedAttributes, SkillInvokedKeys),
            Parts: ExtractCanonicalParts(clonedAttributes),
            Attributes: clonedAttributes);
    }

    /// <summary>Returns whether any attribute key starts with one of <paramref name="namespaces"/> followed by <c>'.'</c>.</summary>
    public static bool HasNamespace(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> namespaces)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(namespaces);

        foreach (string key in attributes.Keys)
        {
            foreach (string ns in namespaces)
            {
                if (key.StartsWith(ns + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether <paramref name="native"/> carries Gemini attribution, either via
    /// <c>gen_ai.system == "gemini"</c> or any <c>gemini.*</c> attribute key.
    /// </summary>
    /// <param name="native">Parsed OTLP span.</param>
    /// <returns><see langword="true"/> when the span is Gemini-attributed and must be excluded.</returns>
    public static bool IsGeminiAttributed(OtlpSpanNative native)
    {
        ArgumentNullException.ThrowIfNull(native);

        string? system = FirstSafeString(native.Attributes, ["gen_ai.system"]);
        if (string.Equals(system, "gemini", StringComparison.Ordinal))
        {
            return true;
        }

        return HasNamespace(native.Attributes, ["gemini"]);
    }

    /// <summary>Returns whether <paramref name="attributes"/> contains at least one key in <paramref name="keys"/>.</summary>
    public static bool HasAnyKey(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(keys);

        foreach (string key in keys)
        {
            if (attributes.ContainsKey(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a non-empty string attribute under <paramref name="key"/>, or <see langword="null"/>.</summary>
    public static string? ReadNonEmptyString(IReadOnlyDictionary<string, JsonElement> attributes, string key)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(key);

        if (!attributes.TryGetValue(key, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = element.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Returns the first non-empty string value among <paramref name="keys"/>.</summary>
    public static string? FirstNonEmptyString(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(keys);

        foreach (string key in keys)
        {
            string? value = ReadNonEmptyString(attributes, key);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the first non-whitespace string value among <paramref name="keys"/> that is not a JSON
    /// object or array document.
    /// </summary>
    public static string? FirstSafeString(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(keys);

        foreach (string key in keys)
        {
            string? value = ReadNonEmptyString(attributes, key);
            if (value is not null && !string.IsNullOrWhiteSpace(value) && !JsonDocumentText.IsDocument(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Reads the first integer/numeric counter value among <paramref name="keys"/>.</summary>
    public static long? ReadOptionalCounter(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(keys);

        foreach (string key in keys)
        {
            if (!attributes.TryGetValue(key, out JsonElement element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt64(out long longValue))
                {
                    return longValue;
                }

                if (element.TryGetDouble(out double doubleValue) && TryTruncateToCounter(doubleValue, out long truncated))
                {
                    return truncated;
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                string? raw = element.GetString();
                if (!string.IsNullOrWhiteSpace(raw)
                    && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    && TryTruncateToCounter(parsed, out long truncated))
                {
                    return truncated;
                }
            }
        }

        return null;
    }

    /// <summary>Reads the first finite floating-point number among <paramref name="keys"/>.</summary>
    public static double? ReadFiniteDouble(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(keys);

        foreach (string key in keys)
        {
            if (!attributes.TryGetValue(key, out JsonElement element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double value) && double.IsFinite(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Truncates <paramref name="value"/> toward zero when it fits in a 64-bit counter.
    /// </summary>
    /// <param name="value">Floating-point counter candidate.</param>
    /// <param name="result">The truncated value when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is finite and in <see cref="long"/> range.</returns>
    public static bool TryTruncateToCounter(double value, out long result)
    {
        const double UpperExclusive = 9223372036854775808d;
        const double LowerInclusive = -9223372036854775808d;
        if (!double.IsFinite(value) || value >= UpperExclusive || value < LowerInclusive)
        {
            result = 0;
            return false;
        }

        result = (long)Math.Truncate(value);
        return true;
    }

    private static bool? ReadOptionalBool(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        foreach (string key in keys)
        {
            if (!attributes.TryGetValue(key, out JsonElement element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

    private static List<string> ReadSafeStringList(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        List<string> values = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string key in keys)
        {
            if (!attributes.TryGetValue(key, out JsonElement element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                string? item = element.GetString();
                if (!string.IsNullOrWhiteSpace(item) && !JsonDocumentText.IsDocument(item) && seen.Add(item))
                {
                    values.Add(item);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    if (child.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? item = child.GetString();
                    if (!string.IsNullOrWhiteSpace(item) && !JsonDocumentText.IsDocument(item) && seen.Add(item))
                    {
                        values.Add(item);
                    }
                }
            }
        }

        return values;
    }

    private static string? ReadRawTextOrJson(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        foreach (string key in keys)
        {
            if (!attributes.TryGetValue(key, out JsonElement element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                string? value = element.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            else if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return element.GetRawText();
            }
        }

        return null;
    }

    private static DateTimeOffset ParseTimestamp(string? timestamp, string? startTimeUnixNano)
    {
        if (!string.IsNullOrWhiteSpace(timestamp)
            && DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime();
        }

        if (!string.IsNullOrWhiteSpace(startTimeUnixNano)
            && BigInteger.TryParse(startTimeUnixNano, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger nanos)
            && nanos >= 0)
        {
            BigInteger ticksFromEpoch = nanos / 100;
            if (ticksFromEpoch <= (BigInteger)(DateTimeOffset.MaxValue.Ticks - DateTimeOffset.UnixEpoch.Ticks))
            {
                return DateTimeOffset.UnixEpoch.AddTicks((long)ticksFromEpoch);
            }
        }

        return DateTimeOffset.UnixEpoch;
    }

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;

    private static JsonElement? TryParseStructured(string raw)
    {
        string trimmed = raw.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '[' && trimmed[0] != '{'))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? SystemPromptText(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        string? raw = ReadRawTextOrJson(attributes, SystemInstructionKeys);
        if (raw is null)
        {
            return null;
        }

        JsonElement? parsed = TryParseStructured(raw);
        if (parsed is null || parsed.Value.ValueKind != JsonValueKind.Array || parsed.Value.GetArrayLength() == 0)
        {
            return raw;
        }

        List<string> texts = [];
        foreach (JsonElement piece in parsed.Value.EnumerateArray())
        {
            if (piece.ValueKind != JsonValueKind.Object)
            {
                return raw;
            }

            string? value = null;
            if (piece.TryGetProperty("text", out JsonElement textProp) && textProp.ValueKind == JsonValueKind.String)
            {
                value = textProp.GetString();
            }
            else if (piece.TryGetProperty("content", out JsonElement contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                value = contentProp.GetString();
            }

            if (value is null)
            {
                return raw;
            }

            if (value.Length > 0)
            {
                texts.Add(value);
            }
        }

        return texts.Count > 0 ? string.Join("\n\n", texts) : raw;
    }

    private static List<ContentPart> ExtractCanonicalParts(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        int order = 0;
        List<ContentPart> parts = [];

        string? system = SystemPromptText(attributes);
        if (system is not null)
        {
            long? reported = ReadOptionalCounter(attributes, SystemTokenKeys);
            parts.Add(new ContentPart("system_prompt", system, Tokens: reported, Order: order++));
        }

        string? rules = ReadRawTextOrJson(attributes, InstructionRuleKeys);
        if (rules is not null)
        {
            long? reported = ReadOptionalCounter(attributes, RuleTokenKeys);
            parts.Add(new ContentPart("instruction_context", rules, Tokens: reported, Order: order++));
        }

        string? skills = ReadRawTextOrJson(attributes, SkillKeys);
        if (skills is not null)
        {
            long? reported = ReadOptionalCounter(attributes, SkillTokenKeys);
            parts.Add(new ContentPart("instruction_context", skills, Tokens: reported, Order: order++));
        }

        AppendToolDefinitionParts(attributes, parts, ref order);
        AppendMessageParts(attributes, parts, system is not null, ref order);

        string? toolResult = ReadRawTextOrJson(attributes, ToolResultKeys);
        if (toolResult is not null)
        {
            parts.Add(new ContentPart("tool_result_content", toolResult, Order: order++));
        }

        if (!HasAnyKey(attributes, InputMessageKeys))
        {
            string? flat = ReadRawTextOrJson(attributes, FlatPromptKeys);
            if (flat is not null)
            {
                parts.Add(new ContentPart("conversation_history", flat, Order: order++));
            }
        }

        return parts;
    }

    private static void AppendToolDefinitionParts(
        IReadOnlyDictionary<string, JsonElement> attributes,
        List<ContentPart> parts,
        ref int order)
    {
        string? raw = ReadRawTextOrJson(attributes, ToolDefinitionKeys);
        if (raw is null)
        {
            return;
        }

        long? reported = ReadOptionalCounter(attributes, ToolDefinitionTokenKeys);
        JsonElement? parsed = TryParseStructured(raw);
        if (parsed is { ValueKind: JsonValueKind.Array } array)
        {
            List<string?> servers = [];
            bool anyServer = false;
            foreach (JsonElement tool in array.EnumerateArray())
            {
                string? server = ExtractToolServer(tool);
                if (server is not null)
                {
                    anyServer = true;
                }

                servers.Add(server);
            }

            if (anyServer)
            {
                int index = 0;
                foreach (JsonElement tool in array.EnumerateArray())
                {
                    string text = tool.ValueKind == JsonValueKind.String
                        ? tool.GetString() ?? string.Empty
                        : tool.GetRawText();
                    parts.Add(new ContentPart("tool_definitions", text, Server: servers[index], Order: order++));
                    index++;
                }

                return;
            }
        }

        parts.Add(new ContentPart("tool_definitions", raw, Tokens: reported, Order: order++));
    }

    /// <summary>
    /// Extracts the MCP/server name from a tool definition entry by scanning
    /// <see cref="ToolServerFields"/> for the first non-empty string property.
    /// </summary>
    /// <param name="tool">Tool definition entry.</param>
    /// <returns>The server name, or <see langword="null"/> when absent.</returns>
    public static string? ExtractToolServer(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string field in ToolServerFields)
        {
            if (tool.TryGetProperty(field, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? value = prop.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static void AppendMessageParts(
        IReadOnlyDictionary<string, JsonElement> attributes,
        List<ContentPart> parts,
        bool hasSystemInstructions,
        ref int order)
    {
        string? raw = ReadRawTextOrJson(attributes, InputMessageKeys);
        if (raw is null)
        {
            return;
        }

        long? reported = ReadOptionalCounter(attributes, MessageTokenKeys);
        JsonElement? parsed = TryParseStructured(raw);
        if (parsed is not { ValueKind: JsonValueKind.Array } array)
        {
            parts.Add(new ContentPart("conversation_history", raw, Tokens: reported, Order: order++));
            return;
        }

        List<ContentPart> messageParts = [];
        foreach (JsonElement message in array.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? role = message.TryGetProperty("role", out JsonElement roleProp) && roleProp.ValueKind == JsonValueKind.String
                ? roleProp.GetString()
                : null;
            if (hasSystemInstructions && string.Equals(role, "system", StringComparison.Ordinal))
            {
                continue;
            }

            if (!message.TryGetProperty("parts", out JsonElement innerParts) || innerParts.ValueKind != JsonValueKind.Array)
            {
                messageParts.Add(new ContentPart("conversation_history", message.GetRawText(), Order: order++));
                continue;
            }

            foreach (JsonElement piece in innerParts.EnumerateArray())
            {
                if (piece.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? type = piece.TryGetProperty("type", out JsonElement typeProp) && typeProp.ValueKind == JsonValueKind.String
                    ? typeProp.GetString()
                    : null;
                string bucket = string.Equals(type, "tool_result", StringComparison.Ordinal)
                    ? "tool_result_content"
                    : "conversation_history";

                string pieceText = piece.TryGetProperty("text", out JsonElement textProp) && textProp.ValueKind == JsonValueKind.String
                    ? textProp.GetString() ?? string.Empty
                    : piece.TryGetProperty("content", out JsonElement contentProp) && contentProp.ValueKind == JsonValueKind.String
                        ? contentProp.GetString() ?? string.Empty
                        : piece.GetRawText();

                messageParts.Add(new ContentPart(bucket, pieceText, Order: order++));
            }
        }

        if (reported.HasValue
            && messageParts.Count == 1
            && string.Equals(messageParts[0].Part, "conversation_history", StringComparison.Ordinal))
        {
            parts.Add(messageParts[0] with { Tokens = reported });
            return;
        }

        parts.AddRange(messageParts);
    }
}
