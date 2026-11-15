using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberDash.Domain;

/// <summary>The three span kinds of the Factory (Droid) capture plugin wire contract v1.</summary>
public enum FactorySpanKind
{
    /// <summary>A Factory session lifecycle span (<c>factory.session</c>).</summary>
    Session,

    /// <summary>A Factory prompt or completion turn span (<c>factory.turn</c>).</summary>
    Turn,

    /// <summary>A Factory tool execution span (<c>factory.tool</c>).</summary>
    Tool,
}

/// <summary>Role qualifying a <see cref="FactorySpanKind.Turn"/> span inside one turn.</summary>
public enum FactoryTurnRole
{
    /// <summary>No role qualification (used by session and tool spans).</summary>
    None,

    /// <summary>Prompt submission span (<c>UserPromptSubmit</c>).</summary>
    Prompt,

    /// <summary>Turn completion span (<c>Stop</c>).</summary>
    Completion,
}

/// <summary>Options controlling what the Factory hook mapper emits onto wire spans.</summary>
public record FactoryMapOptions(bool ContentAllowed = false);

/// <summary>One attribute entry of a mapped Factory wire span.</summary>
public record FactoryWireEntry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] FactoryWireValue Value);

/// <summary>Typed OTLP/JSON attribute value carried on a mapped Factory wire span.</summary>
public record FactoryWireValue(
    [property: JsonPropertyName("stringValue"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StringValue = null,
    [property: JsonPropertyName("intValue"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IntValue = null,
    [property: JsonPropertyName("doubleValue"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DoubleValue = null)
{
    /// <summary>Creates a string OTLP wire attribute value.</summary>
    public static FactoryWireValue FromString(string value) => new(StringValue: value);

    /// <summary>Creates an integer OTLP wire attribute value serialized as a decimal string.</summary>
    public static FactoryWireValue FromInt(long value) =>
        new(IntValue: value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>One pure OTLP-ready Factory capture span emitted by <see cref="FactoryHookMapper"/>.</summary>
public record FactoryWireSpan(
    [property: JsonPropertyName("traceId")] string TraceId,
    [property: JsonPropertyName("spanId")] string SpanId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("attributes")] IReadOnlyList<FactoryWireEntry> Attributes);

/// <summary>Span attribute keys belonging to the <c>factory.*</c> wire namespace.</summary>
public static class FactoryKeys
{
    /// <summary>Contract version key.</summary>
    public const string ContractVersion = "factory.contract_version";

    /// <summary>Session identifier key.</summary>
    public const string SessionId = "factory.session.id";

    /// <summary>Working directory key.</summary>
    public const string Cwd = "factory.cwd";

    /// <summary>Message identifier key.</summary>
    public const string MessageId = "factory.message.id";

    /// <summary>Turn operation key.</summary>
    public const string TurnOperation = "factory.turn.operation";

    /// <summary>Prompt text key.</summary>
    public const string PromptText = "factory.turn.prompt.text";

    /// <summary>Model identifier key.</summary>
    public const string ModelId = "factory.model.id";

    /// <summary>Tool name key.</summary>
    public const string ToolName = "factory.tool.name";

    /// <summary>Tool call identifier key.</summary>
    public const string ToolCallId = "factory.tool.call_id";

    /// <summary>Tool status key.</summary>
    public const string ToolStatus = "factory.tool.status";

    /// <summary>Tool error type key.</summary>
    public const string ToolErrorType = "factory.tool.error_type";

    /// <summary>Tool input UTF-8 byte count key.</summary>
    public const string ToolInputBytes = "factory.tool.input_bytes";

    /// <summary>Tool response UTF-8 byte count key.</summary>
    public const string ToolResultBytes = "factory.tool.result_bytes";
}

/// <summary>
/// Pure contract-v1 mapper for Factory (Droid) capture hook events onto <c>factory.*</c> OTLP spans
/// (<c>dash/src/capture/harnesses/factory-plugin/contract.ts</c> &amp; <c>mapper.ts</c>).
/// </summary>
public static class FactoryHookMapper
{
    /// <summary>Wire contract version stamped on every emitted span.</summary>
    public const string ContractVersion = "1";

    /// <summary>Maximum UTF-8 byte length permitted for captured prompt text.</summary>
    public const int MaxPromptBytes = 64 * 1024;

    private const string PromptTruncationMarker = "\n[kyberdash: truncated]";

    /// <summary>All Factory hook event names defined by wire contract v1.</summary>
    public static IReadOnlyList<string> HookEvents { get; } =
    [
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "Stop",
        "SubagentStop",
        "SessionEnd",
        "PreCompact",
    ];

    /// <summary>Returns the canonical span name for <paramref name="kind"/>.</summary>
    public static string SpanNameFor(FactorySpanKind kind) => kind switch
    {
        FactorySpanKind.Session => "factory.session",
        FactorySpanKind.Turn => "factory.turn",
        FactorySpanKind.Tool => "factory.tool",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Derives the deterministic 32-hex trace id for one unit of work (<c>sessionId</c>, <c>anchorId</c>)
    /// using escaping over <c>|</c> and <c>\</c>.
    /// </summary>
    public static string TraceIdFor(string sessionId, string anchorId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(anchorId);
        string input = $"factory|trace|{EscapeField(sessionId)}|{EscapeField(anchorId)}";
        return Sha256Hex(input)[..32];
    }

    /// <summary>
    /// Derives the deterministic 16-hex span id for <paramref name="kind"/>, <paramref name="sessionId"/>,
    /// <paramref name="anchorId"/>, and optional <paramref name="role"/>.
    /// </summary>
    public static string SpanIdFor(
        FactorySpanKind kind,
        string sessionId,
        string anchorId,
        FactoryTurnRole role = FactoryTurnRole.None)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(anchorId);

        string kindText = kind switch
        {
            FactorySpanKind.Session => "session",
            FactorySpanKind.Turn => "turn",
            FactorySpanKind.Tool => "tool",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        string raw = role switch
        {
            FactoryTurnRole.None =>
                $"factory|{EscapeField(kindText)}|{EscapeField(sessionId)}|{EscapeField(anchorId)}",
            FactoryTurnRole.Prompt =>
                $"factory|{EscapeField(kindText)}|prompt|{EscapeField(sessionId)}|{EscapeField(anchorId)}",
            FactoryTurnRole.Completion =>
                $"factory|{EscapeField(kindText)}|completion|{EscapeField(sessionId)}|{EscapeField(anchorId)}",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        return Sha256Hex(raw)[..16];
    }

    /// <summary>
    /// Maps one Factory hook payload JSON object onto zero or one <see cref="FactoryWireSpan"/>
    /// records under wire contract v1.
    /// </summary>
    public static IReadOnlyList<FactoryWireSpan> MapHookEvent(
        JsonElement payload,
        FactoryMapOptions? options = null)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return [];

        string? hookEvent = ReadNonEmptyString(payload, "hook_event_name");
        if (hookEvent is null)
            return [];

        FactoryMapOptions effectiveOptions = options ?? new FactoryMapOptions(ContentAllowed: false);
        return hookEvent switch
        {
            "SessionStart" => [MapSessionSpan(payload)],
            "UserPromptSubmit" => [MapPromptTurnSpan(payload, effectiveOptions)],
            "PreToolUse" => [MapToolSpan(payload, includeOutcome: false)],
            "PostToolUse" => [MapToolSpan(payload, includeOutcome: true)],
            "Stop" => [MapStopTurnSpan(payload)],
            "SubagentStop" => [],
            "SessionEnd" => [MapSessionSpan(payload)],
            "PreCompact" => [],
            _ => [],
        };
    }

    private static FactoryWireSpan MapSessionSpan(JsonElement payload)
    {
        string? session = ReadNonEmptyString(payload, "session_id");
        string sessionId = session ?? string.Empty;
        List<FactoryWireEntry> attributes = [ContractAttribute()];
        if (session is not null)
            attributes.Add(StringAttribute(FactoryKeys.SessionId, session));
        if (ReadNonEmptyString(payload, "cwd") is string cwd)
            attributes.Add(StringAttribute(FactoryKeys.Cwd, cwd));

        return CreateSpan(FactorySpanKind.Session, sessionId, sessionId, attributes);
    }

    private static FactoryWireSpan MapPromptTurnSpan(JsonElement payload, FactoryMapOptions options)
    {
        string? session = ReadNonEmptyString(payload, "session_id");
        string sessionId = session ?? string.Empty;
        string? messageId = ReadNonEmptyString(payload, "message_id");
        List<FactoryWireEntry> attributes = [ContractAttribute()];
        if (session is not null)
            attributes.Add(StringAttribute(FactoryKeys.SessionId, session));
        if (messageId is not null)
            attributes.Add(StringAttribute(FactoryKeys.MessageId, messageId));
        attributes.Add(StringAttribute(FactoryKeys.TurnOperation, "llm.invoke"));
        if (options.ContentAllowed && ReadNonEmptyString(payload, "prompt") is string prompt)
            attributes.Add(StringAttribute(FactoryKeys.PromptText, CapPrompt(prompt)));
        if (ReadNonEmptyString(payload, "model") is string model)
            attributes.Add(StringAttribute(FactoryKeys.ModelId, model));

        return CreateSpan(
            FactorySpanKind.Turn,
            sessionId,
            messageId ?? sessionId,
            attributes,
            FactoryTurnRole.Prompt);
    }

    private static FactoryWireSpan MapStopTurnSpan(JsonElement payload)
    {
        string? session = ReadNonEmptyString(payload, "session_id");
        string sessionId = session ?? string.Empty;
        string? messageId = ReadNonEmptyString(payload, "message_id");
        List<FactoryWireEntry> attributes = [ContractAttribute()];
        if (session is not null)
            attributes.Add(StringAttribute(FactoryKeys.SessionId, session));
        if (messageId is not null)
            attributes.Add(StringAttribute(FactoryKeys.MessageId, messageId));

        return CreateSpan(
            FactorySpanKind.Turn,
            sessionId,
            messageId ?? sessionId,
            attributes,
            FactoryTurnRole.Completion);
    }

    private static FactoryWireSpan MapToolSpan(JsonElement payload, bool includeOutcome)
    {
        string? session = ReadNonEmptyString(payload, "session_id");
        string sessionId = session ?? string.Empty;
        string? callId = ReadNonEmptyString(payload, "tool_call_id");
        List<FactoryWireEntry> attributes = [ContractAttribute()];
        if (session is not null)
            attributes.Add(StringAttribute(FactoryKeys.SessionId, session));
        if (ReadNonEmptyString(payload, "tool_name") is string toolName)
            attributes.Add(StringAttribute(FactoryKeys.ToolName, toolName));
        if (callId is not null)
            attributes.Add(StringAttribute(FactoryKeys.ToolCallId, callId));
        if (JsonUtf8Bytes(payload, "tool_input") is long inputBytes)
            attributes.Add(CountAttribute(FactoryKeys.ToolInputBytes, inputBytes));

        if (includeOutcome)
        {
            if (ReadNonEmptyString(payload, "tool_status") is string status)
                attributes.Add(StringAttribute(FactoryKeys.ToolStatus, status));
            if (ReadNonEmptyString(payload, "tool_error_type") is string errorType)
                attributes.Add(StringAttribute(FactoryKeys.ToolErrorType, errorType));
            if (JsonUtf8Bytes(payload, "tool_response") is long resultBytes)
                attributes.Add(CountAttribute(FactoryKeys.ToolResultBytes, resultBytes));
        }

        return CreateSpan(FactorySpanKind.Tool, sessionId, callId ?? sessionId, attributes);
    }

    private static FactoryWireSpan CreateSpan(
        FactorySpanKind kind,
        string sessionId,
        string anchor,
        List<FactoryWireEntry> attributes,
        FactoryTurnRole role = FactoryTurnRole.None)
    {
        return new FactoryWireSpan(
            TraceId: TraceIdFor(sessionId, anchor),
            SpanId: SpanIdFor(kind, sessionId, anchor, role),
            Name: SpanNameFor(kind),
            Kind: "SPAN_KIND_INTERNAL",
            Attributes: attributes);
    }

    private static FactoryWireEntry ContractAttribute() =>
        StringAttribute(FactoryKeys.ContractVersion, ContractVersion);

    private static FactoryWireEntry StringAttribute(string key, string value) =>
        new(key, FactoryWireValue.FromString(value));

    private static FactoryWireEntry CountAttribute(string key, long value) =>
        new(key, FactoryWireValue.FromInt(value));

    private static string? ReadNonEmptyString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return null;

        string? text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static long? JsonUtf8Bytes(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Undefined
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return JsonSerializer.SerializeToUtf8Bytes(value).LongLength;
    }

    private static string CapPrompt(string text)
    {
        byte[] allBytes = Encoding.UTF8.GetBytes(text);
        if (allBytes.Length <= MaxPromptBytes)
            return text;

        int markerBytes = Encoding.UTF8.GetByteCount(PromptTruncationMarker);
        int budget = MaxPromptBytes - markerBytes;
        int end = Math.Min(budget, allBytes.Length);
        // Step back across UTF-8 continuation bytes (10xxxxxx) to land cleanly on a code-point boundary.
        while (end > 0 && end < allBytes.Length && (allBytes[end] & 0xC0) == 0x80)
            end--;

        // Also avoid splitting a UTF-16 surrogate pair in decoded characters: step back if a 4-byte sequence was truncated,
        // which the continuation-byte loop above already guarantees since only leading bytes start a sequence.
        string prefix = Encoding.UTF8.GetString(allBytes, 0, end);
        return prefix + PromptTruncationMarker;
    }

    private static string EscapeField(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal) && !value.Contains('|', StringComparison.Ordinal))
            return value;

        StringBuilder sb = new(value.Length + 4);
        foreach (char c in value)
        {
            if (c is '\\' or '|')
                sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Sha256Hex(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }
}
