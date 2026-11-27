using System.Globalization;
using System.Text.Json;

namespace KyberDash.Domain;

/// <summary>
/// Span input handed to <see cref="AdapterRegistry"/> and <see cref="IHarnessAdapter"/>
/// before any harness attribution or token conversion has run.
/// </summary>
/// <param name="SpanId">Span identifier.</param>
/// <param name="TraceId">Trace identifier, or <see langword="null"/> when detached.</param>
/// <param name="ParentSpanId">Parent span identifier, or <see langword="null"/> on a trace root.</param>
/// <param name="Source">Telemetry <c>service.name</c>; grouping and inheritance key only, never attribution evidence.</param>
/// <param name="Attributes">Admissible harness-emitted attributes.</param>
/// <param name="Name">Raw span name.</param>
/// <param name="Kind">Normalized OTLP span kind.</param>
/// <param name="Timestamp">Optional ISO start timestamp.</param>
public record RawSpan(
    string SpanId,
    string? TraceId,
    string? ParentSpanId,
    string Source,
    IReadOnlyDictionary<string, JsonElement> Attributes,
    string Name,
    string Kind,
    string? Timestamp = null);

/// <summary>
/// Fingerprint vote result for a <c>(source, trace)</c> group.
/// </summary>
/// <param name="Harness">Winning adapter's harness identifier.</param>
/// <param name="Confidence">Normalized average <c>detect</c> score in <c>[0, 1]</c>.</param>
public record GroupScore(string Harness, double Confidence);

/// <summary>
/// Contract implemented by every harness telemetry adapter in the KyberDash ingest pipeline.
/// </summary>
public interface IHarnessAdapter
{
    /// <summary>Canonical harness identifier stamped on normalized records (for example <c>"pi"</c> or <c>"copilot"</c>).</summary>
    string Name { get; }

    /// <summary>Attribute namespaces recognized by this adapter.</summary>
    IReadOnlyList<string> Namespaces { get; }

    /// <summary>Scores fingerprint evidence for one span in <c>[0, 1]</c>.</summary>
    double Detect(RawSpan span);

    /// <summary>Normalizes a claimed span into a canonical record using this harness's token convention.</summary>
    CanonicalRecord Normalize(RawSpan span);

    /// <summary>Validates a normalized record, returning a blocking <see cref="Problem"/> or <see langword="null"/>.</summary>
    Problem? Validate(CanonicalRecord record);
}

/// <summary>
/// Two-pass fingerprint voter and exact-source inheritance registry
/// (<c>dash/src/canon/adapters/registry.ts</c>).
/// </summary>
public class AdapterRegistry
{
    /// <summary>Minimum normalized group confidence required for Pass-1 attribution.</summary>
    public const double DefaultConfidenceThreshold = 0.6;

    /// <summary>Sentinel group key suffix for spans with a <see langword="null"/> trace ID.</summary>
    public const string UntracedGroup = "(untraced)";

    private readonly List<IHarnessAdapter> adapters = [];
    private readonly double threshold;

    /// <summary>
    /// Creates a registry populated with <paramref name="adapters"/> in tie-break priority order.
    /// </summary>
    public AdapterRegistry(
        IEnumerable<IHarnessAdapter>? adapters = null,
        double threshold = DefaultConfidenceThreshold)
    {
        this.threshold = threshold;
        if (adapters is not null)
        {
            foreach (IHarnessAdapter adapter in adapters)
                Register(adapter);
        }
    }

    /// <summary>Registers a unique harness adapter in tie-break priority order.</summary>
    public void Register(IHarnessAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (adapters.Any(existing => string.Equals(existing.Name, adapter.Name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"harness adapter \"{adapter.Name}\" is already registered");
        }

        adapters.Add(adapter);
    }

    /// <summary>Builds the deterministic grouping key for a <c>(source, traceId)</c> pair.</summary>
    public string GroupKey(string source, string? traceId) =>
        $"{source}::{traceId ?? UntracedGroup}";

    /// <summary>
    /// Scores one <c>(source, traceId)</c> group using strict first-registered tie-breaking.
    /// </summary>
    public GroupScore? ScoreGroup(string source, string? traceId, IReadOnlyList<RawSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(spans);

        string expected = GroupKey(source, traceId);
        foreach (RawSpan span in spans)
        {
            string actual = GroupKey(span.Source, span.TraceId);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"span {span.SpanId} ({actual}) is not part of group {expected}");
            }
        }

        if (spans.Count == 0)
            return null;

        IHarnessAdapter? winner = null;
        double winnerTotal = 0;

        foreach (IHarnessAdapter adapter in adapters)
        {
            double total = 0;
            foreach (RawSpan span in spans)
                total += ClampScore(adapter.Detect(span));

            if (total > winnerTotal)
            {
                winner = adapter;
                winnerTotal = total;
            }
        }

        return winner is null
            ? null
            : new GroupScore(winner.Name, winnerTotal / spans.Count);
    }

    /// <summary>
    /// Runs Pass 1 (fingerprint vote per group) followed by Pass 2 (single-harness exact-source
    /// inheritance) and returns a mapping from attributed <c>spanId</c> to harness name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attribute(IReadOnlyList<RawSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);

        Dictionary<string, SpanGroup> groups = new(StringComparer.Ordinal);
        List<SpanGroup> orderedGroups = [];
        foreach (RawSpan span in spans)
        {
            string key = GroupKey(span.Source, span.TraceId);
            if (!groups.TryGetValue(key, out SpanGroup? group))
            {
                group = new SpanGroup(span.Source, span.TraceId);
                groups.Add(key, group);
                orderedGroups.Add(group);
            }

            group.Spans.Add(span);
        }

        Dictionary<string, string> attributed = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> sourceHarnesses = new(StringComparer.Ordinal);
        List<SpanGroup> undecided = [];

        foreach (SpanGroup group in orderedGroups)
        {
            GroupScore? score = ScoreGroup(group.Source, group.TraceId, group.Spans);
            if (score is not null && score.Confidence >= threshold)
            {
                foreach (RawSpan span in group.Spans)
                    attributed[span.SpanId] = score.Harness;

                if (!sourceHarnesses.TryGetValue(group.Source, out HashSet<string>? harnesses))
                {
                    harnesses = new HashSet<string>(StringComparer.Ordinal);
                    sourceHarnesses.Add(group.Source, harnesses);
                }

                harnesses.Add(score.Harness);
            }
            else
            {
                undecided.Add(group);
            }
        }

        foreach (SpanGroup group in undecided)
        {
            if (!sourceHarnesses.TryGetValue(group.Source, out HashSet<string>? harnesses) || harnesses.Count != 1)
                continue;

            string inherited = harnesses.First();
            foreach (RawSpan span in group.Spans)
                attributed[span.SpanId] = inherited;
        }

        return attributed;
    }

    private static double ClampScore(double score) =>
        double.IsFinite(score) ? Math.Clamp(score, 0.0, 1.0) : 0.0;

    private class SpanGroup(string source, string? traceId)
    {
        public string Source { get; } = source;
        public string? TraceId { get; } = traceId;
        public List<RawSpan> Spans { get; } = [];
    }
}

/// <summary>
/// Built-in primary harness adapters registered in canonical tie-break order.
/// </summary>
public static class CanonAdapters
{
    /// <summary>Supported OpenCode plugin wire contract version.</summary>
    public const string OpencodeContractVersion = "3";

    /// <summary>Attribute key naming the OpenCode plugin wire contract version.</summary>
    public const string OpencodeContractVersionKey = "opencode.contract_version";

    /// <summary>Quarantine reason stamped when an OpenCode envelope names an unsupported or missing contract version.</summary>
    public const string UnknownOpencodeContractVersionReason = "unknown opencode contract version";

    /// <summary>Supported Factory Droid plugin wire contract version.</summary>
    public const string DroidContractVersion = "1";

    /// <summary>Attribute key naming the Factory Droid plugin wire contract version.</summary>
    public const string DroidContractVersionKey = "factory.contract_version";

    /// <summary>Quarantine reason stamped when a Factory Droid envelope names an unsupported or missing contract version.</summary>
    public const string UnknownDroidContractVersionReason = "unknown droid contract version";

    private const double VendorEvidence = 0.6;
    private const double UsageEvidence = 0.4;
    private const string UnknownTimestamp = "1970-01-01T00:00:00.000Z";

    private static readonly string[] InputTokenKeys = ["gen_ai.usage.input_tokens", "input_tokens"];
    private static readonly string[] OutputTokenKeys = ["gen_ai.usage.output_tokens", "output_tokens"];
    private static readonly string[] CacheReadKeys =
    [
        "gen_ai.usage.cache_read.input_tokens",
        "gen_ai.usage.cache_read_input_tokens",
        "gen_ai.usage.cached_tokens",
        "cache_read_tokens",
    ];
    private static readonly string[] CacheCreationKeys =
    [
        "gen_ai.usage.cache_creation.input_tokens",
        "gen_ai.usage.cache_creation_input_tokens",
        "cache_creation_tokens",
    ];
    private static readonly string[] ReasoningKeys = ["gen_ai.usage.reasoning_tokens"];
    private static readonly string[] ThoughtKeys = ["gemini.usage.thoughts_tokens"];
    private static readonly string[] CodexCacheWriteKeys = ["gen_ai.usage.cache_write.input_tokens"];
    private static readonly string[] CodexReasoningKeys = ["codex.usage.reasoning_output_tokens", "gen_ai.usage.reasoning_tokens"];

    private static readonly string[] SessionIdKeys =
    [
        "gen_ai.session.id",
        "session.id",
        "copilot_chat.chat_session_id",
        "copilot_chat.session_id",
        "gen_ai.conversation.id",
    ];

    private static readonly string[] SystemInstructionKeys = ["gen_ai.system_instructions"];
    private static readonly string[] InputMessageKeys = ["gen_ai.input.messages"];
    private static readonly string[] InstructionRuleKeys = ["gen_ai.rules"];
    private static readonly string[] SkillKeys = ["gen_ai.skills"];
    private static readonly string[] ToolDefinitionKeys = ["gen_ai.tool.definitions", "gen_ai.request.tools"];
    private static readonly string[] ToolResultKeys = ["gen_ai.tool.call.result"];
    private static readonly string[] FlatPromptKeys = ["gen_ai.prompt"];
    private static readonly string[] ToolServerFields = ["server", "mcp_server", "mcpServer", "server_name"];

    private static readonly string[] SystemTokenKeys = ["gen_ai.usage.sys_tokens"];
    private static readonly string[] ToolDefinitionTokenKeys = ["gen_ai.usage.tool_tokens"];
    private static readonly string[] SkillTokenKeys = ["gen_ai.usage.skill_tokens"];
    private static readonly string[] RuleTokenKeys = ["gen_ai.usage.rule_tokens"];
    private static readonly string[] MessageTokenKeys = ["gen_ai.usage.msg_tokens"];

    private static readonly string[] OpencodeEnvelopeMarkerPrefixes =
    [
        "opencode.span.",
        "opencode.turn.",
        "opencode.model.",
        "opencode.tool.",
        "opencode.prompt.",
        "opencode.call.",
        "opencode.message.",
        "opencode.hook",
        "opencode.context.",
    ];

    private static readonly string[] CanonicalModelKeys = ["gen_ai.response.model", "gen_ai.request.model", "model"];
    private static readonly string[] DroidSessionKeys = ["factory.session.id", "factory.session_id", "factory.sessionId"];
    private static readonly string[] DroidModelKeys = ["factory.model.id", "factory.model"];
    private static readonly string[] DroidToolNameKeys = ["factory.tool.name", "factory.tool_name"];

    /// <summary>Default ordered adapter list matching TS <c>ADAPTERS</c> tie-break priority.</summary>
    public static IReadOnlyList<IHarnessAdapter> DefaultAdapters { get; } =
    [
        new PiAdapter(),
        new CopilotAdapter(),
        new AntigravityAdapter(),
        new GeminiAdapter(),
        new ClaudeCodeAdapter(),
        new CodexAdapter(),
        new OpencodeAdapter(),
        new DroidAdapter(),
    ];

    /// <summary>Extracts the harness's native session/conversation id when present.</summary>
    public static string? CanonicalSessionId(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        foreach (string key in SessionIdKeys)
        {
            string? value = ReadNonEmptyString(attributes, key);
            if (value is not null)
                return value;
        }

        return null;
    }

    /// <summary>Returns whether a raw span belongs to an OpenCode plugin envelope of any contract version.</summary>
    public static bool IsOpencodeEnvelopeSpan(RawSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        if (span.Attributes.ContainsKey(OpencodeContractVersionKey))
            return true;

        foreach (string key in span.Attributes.Keys)
        {
            foreach (string prefix in OpencodeEnvelopeMarkerPrefixes)
            {
                if (string.Equals(key, prefix, StringComparison.Ordinal))
                    return true;

                string dottedPrefix = prefix.EndsWith('.') ? prefix : prefix + ".";
                if (key.StartsWith(dottedPrefix, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether <paramref name="span"/> carries the supported OpenCode contract version (<c>"3"</c>).</summary>
    public static bool IsSupportedOpencodeEnvelope(RawSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        return IsOpencodeEnvelopeSpan(span)
            && string.Equals(ReadNonEmptyString(span.Attributes, OpencodeContractVersionKey), OpencodeContractVersion, StringComparison.Ordinal);
    }

    /// <summary>Returns whether <paramref name="span"/> belongs to a Factory Droid plugin envelope of any contract version.</summary>
    public static bool IsDroidEnvelopeSpan(RawSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        if (span.Attributes.ContainsKey(DroidContractVersionKey))
            return true;

        foreach (string key in span.Attributes.Keys)
        {
            if (key.StartsWith("factory.", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Returns whether <paramref name="span"/> carries the supported Factory Droid contract version (<c>"1"</c>).</summary>
    public static bool IsSupportedDroidEnvelope(RawSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        return IsDroidEnvelopeSpan(span)
            && string.Equals(ReadNonEmptyString(span.Attributes, DroidContractVersionKey), DroidContractVersion, StringComparison.Ordinal);
    }

    /// <summary>Builds a cache-inclusive disjoint <see cref="TokenUsage"/> without clamping negative results.</summary>
    public static TokenUsage InclusiveConvention(long input, long cacheRead, long cacheCreation, long output, long? reasoning = null)
    {
        long freshInput = input - cacheRead - cacheCreation;
        return new TokenUsage(
            FreshInput: freshInput,
            CacheRead: cacheRead,
            CacheCreation: cacheCreation,
            Output: output,
            ReportedInput: input,
            ReportedOutput: output,
            Reasoning: reasoning);
    }

    /// <summary>Builds a cache-exclusive disjoint <see cref="TokenUsage"/> reassembling total input as the sum of classes.</summary>
    public static TokenUsage ExclusiveConvention(long input, long cacheRead, long cacheCreation, long output, long? reasoning = null)
    {
        return new TokenUsage(
            FreshInput: input,
            CacheRead: cacheRead,
            CacheCreation: cacheCreation,
            Output: output,
            ReportedInput: SaturatingSum(input, cacheRead, cacheCreation),
            ReportedOutput: output,
            Reasoning: reasoning);
    }

    /// <summary>
    /// Sums token classes without wrapping. The classes are untrusted, and a wrapped total would
    /// look like a small measurement; saturating leaves it out of range so the decomposition check
    /// reports a mismatch instead of agreeing.
    /// </summary>
    private static long SaturatingSum(long first, long second, long third)
    {
        decimal total = (decimal)first + second + third;
        if (total > long.MaxValue)
            return long.MaxValue;
        if (total < long.MinValue)
            return long.MinValue;

        return (long)total;
    }

    /// <summary>
    /// Applies cache-inclusive conversion and falls back to cache-exclusive when subtraction goes
    /// negative and an explicit input counter was present.
    /// </summary>
    public static TokenUsage ConvertInclusiveCounts(
        long input,
        long cacheRead,
        long cacheCreation,
        long output,
        bool inputPresent,
        long? reasoning = null)
    {
        TokenUsage inclusive = InclusiveConvention(input, cacheRead, cacheCreation, output, reasoning);
        if (inclusive.FreshInput < 0 && inputPresent)
            return ExclusiveConvention(input, cacheRead, cacheCreation, output, reasoning);

        return inclusive;
    }

    private static bool HasNamespace(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> namespaces)
    {
        foreach (string key in attributes.Keys)
        {
            foreach (string ns in namespaces)
            {
                if (key.StartsWith(ns + ".", StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static bool HasAnyKey(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        foreach (string key in keys)
        {
            if (attributes.ContainsKey(key))
                return true;
        }

        return false;
    }

    private static string? ReadNonEmptyString(IReadOnlyDictionary<string, JsonElement> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out JsonElement element) || element.ValueKind != JsonValueKind.String)
            return null;

        string? value = element.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? FirstNonEmptyString(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        foreach (string key in keys)
        {
            string? value = ReadNonEmptyString(attributes, key);
            if (value is not null)
                return value;
        }

        return null;
    }

    /// <summary>
    /// Truncates a finite double counter to <see langword="long"/>, refusing values outside long's
    /// range: the cast itself wraps on x64, and a wrapped counter then reads as a real measurement.
    /// </summary>
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

    private static long? ReadOptionalCounter(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        foreach (string key in keys)
        {
            if (!attributes.TryGetValue(key, out JsonElement element))
                continue;

            if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt64(out long longValue))
                    return longValue;

                if (element.TryGetDouble(out double doubleValue) && TryTruncateToCounter(doubleValue, out long truncated))
                    return truncated;
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

    private static long ReadCounter(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys) =>
        ReadOptionalCounter(attributes, keys) ?? 0;

    private static double? ReadFiniteDouble(IReadOnlyDictionary<string, JsonElement> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out JsonElement element))
            return null;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double d) && double.IsFinite(d))
            return d;

        return null;
    }

    private static string CanonicalOp(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        if (attributes.ContainsKey("gen_ai.tool.name"))
            return "tool.invoke";

        if (string.Equals(ReadNonEmptyString(attributes, "gen_ai.operation.name"), "invoke_agent", StringComparison.Ordinal))
            return "agent.invoke";

        if (HasAnyKey(attributes, InputTokenKeys) || HasAnyKey(attributes, OutputTokenKeys))
            return "llm.invoke";

        return "unspecified";
    }

    private static JsonElement NotMeasurableElement(string reason) =>
        JsonSerializer.SerializeToElement(new { availability = "not_measurable", reason });

    private static JsonElement MeasuredElement() =>
        JsonSerializer.SerializeToElement("measured");

    /// <summary>
    /// Reads the first present attribute from <paramref name="keys"/> as text: a JSON string's
    /// value when non-empty, otherwise an object's or array's raw JSON text.
    /// </summary>
    /// <param name="attributes">Raw adapter attributes.</param>
    /// <param name="keys">Attribute keys in precedence order.</param>
    /// <returns>The attribute text, or <see langword="null"/> when no listed key carries text.</returns>
    public static string? ReadText(IReadOnlyDictionary<string, JsonElement> attributes, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(keys);

        foreach (string key in keys)
        {
            if (!attributes.TryGetValue(key, out JsonElement element))
                continue;

            if (element.ValueKind == JsonValueKind.String)
            {
                string? value = element.GetString();
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
            else if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return element.GetRawText();
            }
        }

        return null;
    }

    private static JsonElement? TryParseStructured(string raw)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? SystemPromptText(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        string? raw = ReadText(attributes, SystemInstructionKeys);
        if (raw is null)
            return null;

        JsonElement? parsed = TryParseStructured(raw);
        if (parsed is null || parsed.Value.ValueKind != JsonValueKind.Array || parsed.Value.GetArrayLength() == 0)
            return raw;

        List<string> texts = [];
        foreach (JsonElement piece in parsed.Value.EnumerateArray())
        {
            if (piece.ValueKind != JsonValueKind.Object)
                return raw;

            string? value = null;
            if (piece.TryGetProperty("text", out JsonElement textProp) && textProp.ValueKind == JsonValueKind.String)
                value = textProp.GetString();
            else if (piece.TryGetProperty("content", out JsonElement contentProp) && contentProp.ValueKind == JsonValueKind.String)
                value = contentProp.GetString();

            if (value is null)
                return raw;

            if (value.Length > 0)
                texts.Add(value);
        }

        return texts.Count > 0 ? string.Join("\n\n", texts) : raw;
    }

    private static List<ContentPart> CanonicalParts(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        int order = 0;
        List<ContentPart> parts = [];

        string? system = SystemPromptText(attributes);
        if (system is not null)
        {
            long? reported = ReadOptionalCounter(attributes, SystemTokenKeys);
            parts.Add(new ContentPart("system_prompt", system, Tokens: reported, Order: order++));
        }

        string? rules = ReadText(attributes, InstructionRuleKeys);
        if (rules is not null)
        {
            long? reported = ReadOptionalCounter(attributes, RuleTokenKeys);
            parts.Add(new ContentPart("instruction_context", rules, Tokens: reported, Order: order++));
        }

        string? skills = ReadText(attributes, SkillKeys);
        if (skills is not null)
        {
            long? reported = ReadOptionalCounter(attributes, SkillTokenKeys);
            parts.Add(new ContentPart("instruction_context", skills, Tokens: reported, Order: order++));
        }

        AppendToolDefinitionParts(attributes, parts, ref order);
        AppendMessageParts(attributes, parts, system is not null, ref order);

        string? toolResult = ReadText(attributes, ToolResultKeys);
        if (toolResult is not null)
            parts.Add(new ContentPart("tool_result_content", toolResult, Order: order++));

        if (!HasAnyKey(attributes, InputMessageKeys))
        {
            string? flat = ReadText(attributes, FlatPromptKeys);
            if (flat is not null)
                parts.Add(new ContentPart("conversation_history", flat, Order: order++));
        }

        return parts;
    }

    private static void AppendToolDefinitionParts(
        IReadOnlyDictionary<string, JsonElement> attributes,
        List<ContentPart> parts,
        ref int order)
    {
        string? raw = ReadText(attributes, ToolDefinitionKeys);
        if (raw is null)
            return;

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
                    anyServer = true;
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
            return null;

        foreach (string field in ToolServerFields)
        {
            if (tool.TryGetProperty(field, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? value = prop.GetString();
                if (!string.IsNullOrEmpty(value))
                    return value;
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
        string? raw = ReadText(attributes, InputMessageKeys);
        if (raw is null)
            return;

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
                continue;

            string? role = message.TryGetProperty("role", out JsonElement roleProp) && roleProp.ValueKind == JsonValueKind.String
                ? roleProp.GetString()
                : null;
            if (hasSystemInstructions && string.Equals(role, "system", StringComparison.Ordinal))
                continue;

            if (!message.TryGetProperty("parts", out JsonElement innerParts) || innerParts.ValueKind != JsonValueKind.Array)
            {
                messageParts.Add(new ContentPart("conversation_history", message.GetRawText(), Order: order++));
                continue;
            }

            foreach (JsonElement piece in innerParts.EnumerateArray())
            {
                if (piece.ValueKind != JsonValueKind.Object)
                    continue;

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

    private static CanonicalRecord BaseRecord(
        IHarnessAdapter adapter,
        IReadOnlyList<string> unexportedMetrics,
        RawSpan raw)
    {
        List<ContentPart> parts = CanonicalParts(raw.Attributes);
        Dictionary<string, JsonElement> measurability = new(unexportedMetrics.Count, StringComparer.Ordinal);
        foreach (string metric in unexportedMetrics)
            measurability[metric] = NotMeasurableElement($"{adapter.Name} does not export {metric}.");

        Dictionary<string, JsonElement> rawCopy = new(raw.Attributes.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> entry in raw.Attributes)
            rawCopy[entry.Key] = entry.Value.Clone();

        return new CanonicalRecord
        {
            SpanId = raw.SpanId,
            TraceId = raw.TraceId,
            ParentSpanId = raw.ParentSpanId,
            Source = raw.Source,
            Harness = adapter.Name,
            SessionId = CanonicalSessionId(raw.Attributes),
            Name = raw.Name,
            Op = CanonicalOp(raw.Attributes),
            Kind = raw.Kind,
            Timestamp = raw.Timestamp ?? UnknownTimestamp,
            DurationMs = 0,
            Status = "unspecified",
            Tokens = InclusiveConvention(0, 0, 0, 0),
            Content = CanonValidation.ContentFromParts(parts),
            Parts = parts,
            Cost = new CostBlock(CostBasis.Unknown, CostStatus.NoRate),
            Measurability = measurability,
            Raw = rawCopy,
        };
    }

    private static Problem? ValidateRecordTokens(CanonicalRecord record) =>
        CanonValidation.ValidateTokens(record.Tokens, record.SpanId, record.Measurability);

    private static UsageCounters ReadUsageCounters(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        long? input = ReadOptionalCounter(attributes, InputTokenKeys);
        return new UsageCounters(
            Input: input ?? 0,
            InputPresent: input.HasValue,
            Output: ReadCounter(attributes, OutputTokenKeys),
            CacheRead: ReadCounter(attributes, CacheReadKeys),
            CacheCreation: ReadCounter(attributes, CacheCreationKeys),
            Reasoning: ReadCounter(attributes, ReasoningKeys));
    }

    private readonly record struct UsageCounters(
        long Input,
        bool InputPresent,
        long Output,
        long CacheRead,
        long CacheCreation,
        long Reasoning);

    private class PiAdapter : IHarnessAdapter
    {
        private static readonly string[] VendorNamespaces = ["pi"];
        private static readonly string[] ToolKeys = ["pi.tools.selected", "pi.tools.snippets"];
        private static readonly string[] InstructionKeys = ["pi.skills", "pi.context_files"];

        public string Name => "pi";
        public IReadOnlyList<string> Namespaces { get; } = ["gen_ai", "pi"];

        public double Detect(RawSpan span)
        {
            double score = 0;
            if (HasNamespace(span.Attributes, VendorNamespaces))
                score += VendorEvidence;
            if (HasAnyKey(span.Attributes, InputTokenKeys))
                score += UsageEvidence;
            return Math.Min(1.0, score);
        }

        public CanonicalRecord Normalize(RawSpan raw)
        {
            CanonicalRecord record = BaseRecord(this, [], raw);
            List<ContentPart> mergedParts = [.. record.Parts ?? []];
            int order = mergedParts.Count;

            foreach (string key in ToolKeys)
            {
                string? text = ReadText(raw.Attributes, [key]);
                if (text is not null)
                    mergedParts.Add(new ContentPart("tool_definitions", text, Order: order++));
            }

            foreach (string key in InstructionKeys)
            {
                string? text = ReadText(raw.Attributes, [key]);
                if (text is not null)
                    mergedParts.Add(new ContentPart("instruction_context", text, Order: order++));
            }

            UsageCounters counters = ReadUsageCounters(raw.Attributes);
            TokenUsage tokens = ExclusiveConvention(
                counters.Input,
                counters.CacheRead,
                counters.CacheCreation,
                counters.Output,
                counters.Reasoning != 0 ? counters.Reasoning : null);

            string? sessionId = string.IsNullOrWhiteSpace(record.SessionId) ? null : record.SessionId.Trim();
            if (sessionId is null)
            {
                string? piSession = ReadNonEmptyString(raw.Attributes, "pi.session.id");
                if (!string.IsNullOrWhiteSpace(piSession))
                    sessionId = piSession.Trim();
            }

            return record with
            {
                SessionId = sessionId,
                Parts = mergedParts,
                Content = CanonValidation.ContentFromParts(mergedParts),
                Tokens = tokens,
            };
        }

        public Problem? Validate(CanonicalRecord record) => ValidateRecordTokens(record);
    }

    private class CopilotAdapter : IHarnessAdapter
    {
        private static readonly string[] VendorNamespaces = ["github.copilot", "copilot", "copilot_chat", "codeburn"];
        private static readonly string[] StrongCopilotNamespaces = ["copilot_chat", "github.copilot", "copilot"];
        private static readonly string[] Unexported = ["reasoning"];

        public string Name => "copilot";
        public IReadOnlyList<string> Namespaces { get; } = ["gen_ai", "github.copilot", "copilot", "copilot_chat", "codeburn"];

        public double Detect(RawSpan span)
        {
            bool isCopilotEvidence = HasNamespace(span.Attributes, StrongCopilotNamespaces);
            if (string.Equals(ReadNonEmptyString(span.Attributes, "gen_ai.system"), "gemini", StringComparison.Ordinal))
            {
                if (string.Equals(ReadNonEmptyString(span.Attributes, "gen_ai.agent.name"), "antigravity", StringComparison.Ordinal)
                    || !isCopilotEvidence)
                {
                    return 0.0;
                }
            }

            double score = 0;
            if (HasNamespace(span.Attributes, VendorNamespaces))
                score += VendorEvidence;
            if (HasAnyKey(span.Attributes, InputTokenKeys))
                score += UsageEvidence;
            return Math.Min(1.0, score);
        }

        public CanonicalRecord Normalize(RawSpan raw)
        {
            CanonicalRecord record = BaseRecord(this, Unexported, raw);
            Dictionary<string, JsonElement> measurability = record.Measurability is not null
                ? new Dictionary<string, JsonElement>(record.Measurability, StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            if (record.Parts is null || record.Parts.Count == 0)
            {
                foreach (string bucket in CanonValidation.CanonicalContentKeys)
                    measurability[bucket] = NotMeasurableElement("Copilot did not capture this content bucket.");
            }

            UsageCounters counters = ReadUsageCounters(raw.Attributes);
            TokenUsage tokens = ConvertInclusiveCounts(
                counters.Input,
                counters.CacheRead,
                counters.CacheCreation,
                counters.Output,
                counters.InputPresent,
                counters.Reasoning != 0 ? counters.Reasoning : null);

            return record with
            {
                Measurability = measurability,
                Tokens = tokens,
            };
        }

        public Problem? Validate(CanonicalRecord record) => ValidateRecordTokens(record);
    }

    private class AntigravityAdapter : IHarnessAdapter
    {
        private static readonly string[] Unexported = ["cache_creation"];

        public string Name => "antigravity";
        public IReadOnlyList<string> Namespaces { get; } = ["gen_ai", "gemini", "antigravity"];

        public double Detect(RawSpan span)
        {
            double score = 0;
            if (string.Equals(ReadNonEmptyString(span.Attributes, "gen_ai.agent.name"), "antigravity", StringComparison.Ordinal))
                score += VendorEvidence;
            if (HasAnyKey(span.Attributes, InputTokenKeys))
                score += UsageEvidence;
            return Math.Min(1.0, score);
        }

        public CanonicalRecord Normalize(RawSpan raw)
        {
            CanonicalRecord record = BaseRecord(this, Unexported, raw);
            UsageCounters counters = ReadUsageCounters(raw.Attributes);
            long thoughts = counters.Reasoning != 0
                ? counters.Reasoning
                : ReadCounter(raw.Attributes, ThoughtKeys);

            TokenUsage tokens = ConvertInclusiveCounts(
                counters.Input,
                counters.CacheRead,
                cacheCreation: 0,
                counters.Output,
                counters.InputPresent,
                thoughts != 0 ? thoughts : null);

            return record with { Tokens = tokens };
        }

        public Problem? Validate(CanonicalRecord record) => ValidateRecordTokens(record);
    }

    private class GeminiAdapter : IHarnessAdapter
    {
        private static readonly string[] VendorNamespaces = ["gemini"];
        private static readonly string[] CopilotNamespaces = ["copilot_chat", "github.copilot", "copilot"];
        private static readonly string[] Unexported = ["cache_creation"];

        public string Name => "gemini";
        public IReadOnlyList<string> Namespaces { get; } = ["gen_ai", "gemini"];

        public double Detect(RawSpan span)
        {
            if (HasNamespace(span.Attributes, CopilotNamespaces)
                || span.Name.StartsWith("copilot_chat", StringComparison.Ordinal)
                || span.Name.StartsWith("copilot", StringComparison.Ordinal))
            {
                return 0.0;
            }

            if (string.Equals(ReadNonEmptyString(span.Attributes, "gen_ai.agent.name"), "antigravity", StringComparison.Ordinal))
                return 0.0;

            double score = 0;
            if (HasNamespace(span.Attributes, VendorNamespaces)
                || string.Equals(ReadNonEmptyString(span.Attributes, "gen_ai.system"), "gemini", StringComparison.Ordinal))
            {
                score += VendorEvidence;
            }

            if (HasAnyKey(span.Attributes, InputTokenKeys))
                score += UsageEvidence;

            return Math.Min(1.0, score);
        }

        public CanonicalRecord Normalize(RawSpan raw)
        {
            CanonicalRecord record = BaseRecord(this, Unexported, raw);
            UsageCounters counters = ReadUsageCounters(raw.Attributes);
            long thoughts = counters.Reasoning != 0
                ? counters.Reasoning
                : ReadCounter(raw.Attributes, ThoughtKeys);

            TokenUsage tokens = ConvertInclusiveCounts(
                counters.Input,
                counters.CacheRead,
                cacheCreation: 0,
                counters.Output,
                counters.InputPresent,
                thoughts != 0 ? thoughts : null);

            return record with { Tokens = tokens };
        }

        public Problem? Validate(CanonicalRecord record) => ValidateRecordTokens(record);
    }

    private class ClaudeCodeAdapter : IHarnessAdapter
    {
        private static readonly string[] VendorNamespaces = ["claude", "claude_code"];
        private static readonly string[] Unexported =
        [
            "system_prompt",
            "tool_definitions",
            "instruction_context",
            "conversation_history",
            "tool_result_content",
            "schema_cost",
        ];

        public string Name => "claude-code";
        public IReadOnlyList<string> Namespaces { get; } = ["gen_ai", "claude", "claude_code"];

        public double Detect(RawSpan span)
        {
            double score = 0;
            if (HasNamespace(span.Attributes, VendorNamespaces))
                score += VendorEvidence;
            if (HasAnyKey(span.Attributes, InputTokenKeys))
                score += UsageEvidence;
            return Math.Min(1.0, score);
        }

        public CanonicalRecord Normalize(RawSpan raw)
        {
            CanonicalRecord record = BaseRecord(this, Unexported, raw);
            string op = record.Op;
            if (string.Equals(op, "unspecified", StringComparison.Ordinal)
                && string.Equals(ReadNonEmptyString(raw.Attributes, "span.type"), "tool", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(ReadNonEmptyString(raw.Attributes, "tool_name")))
            {
                op = "tool.invoke";
            }

            UsageCounters counters = ReadUsageCounters(raw.Attributes);
            TokenUsage tokens = ExclusiveConvention(
                counters.Input,
                counters.CacheRead,
                counters.CacheCreation,
                counters.Output,
                counters.Reasoning != 0 ? counters.Reasoning : null);

            return record with
            {
                Op = op,
                Tokens = tokens,
            };
        }

        public Problem? Validate(CanonicalRecord record) => ValidateRecordTokens(record);
    }

    private class CodexAdapter : IHarnessAdapter
    {
        private static readonly string[] VendorNamespaces = ["codex"];
        private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
        {
            "model",
            "slug",
            "conversation.id",
            "gen_ai.session.id",
            "session.id",
            "event.timestamp",
        };
        private static readonly string[] AllowedPrefixes = ["gen_ai.usage.", "codex.usage."];
        private static readonly string[] Unexported = ["tool_definitions"];

        public string Name => "codex";
        public IReadOnlyList<string> Namespaces { get; } = ["gen_ai", "codex"];

        public double Detect(RawSpan span)
        {
            double score = 0;
            if (HasNamespace(span.Attributes, VendorNamespaces))
                score += VendorEvidence;
            if (HasAnyKey(span.Attributes, InputTokenKeys))
                score += UsageEvidence;
            return Math.Min(1.0, score);
        }

        public CanonicalRecord Normalize(RawSpan raw)
        {
            Dictionary<string, JsonElement> safeAttributes = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonElement> entry in raw.Attributes)
            {
                if (AllowedKeys.Contains(entry.Key)
                    || AllowedPrefixes.Any(prefix => entry.Key.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    safeAttributes[entry.Key] = entry.Value;
                }
            }

            RawSpan safe = raw with { Attributes = safeAttributes };
            CanonicalRecord record = BaseRecord(this, Unexported, safe);
            string? sessionId = ReadNonEmptyString(safe.Attributes, "conversation.id") ?? record.SessionId;
            UsageCounters counters = ReadUsageCounters(safe.Attributes);
            long cacheCreation = ReadCounter(safe.Attributes, CodexCacheWriteKeys);
            long reasoning = ReadCounter(safe.Attributes, CodexReasoningKeys);

            TokenUsage tokens = InclusiveConvention(
                counters.Input,
                counters.CacheRead,
                cacheCreation,
                counters.Output,
                reasoning != 0 ? reasoning : null);

            return record with
            {
                SessionId = sessionId,
                Tokens = tokens,
            };
        }

        public Problem? Validate(CanonicalRecord record) => ValidateRecordTokens(record);
    }

    private class OpencodeAdapter : IHarnessAdapter
    {
        public string Name => "opencode";
        public IReadOnlyList<string> Namespaces { get; } = ["opencode"];

        public double Detect(RawSpan span) => IsSupportedOpencodeEnvelope(span) ? 1.0 : 0.0;

        public CanonicalRecord Normalize(RawSpan raw)
        {
            CanonicalRecord record = BaseRecord(this, [], raw);
            if (!IsSupportedOpencodeEnvelope(raw))
                return record;

            string? kind = ReadNonEmptyString(raw.Attributes, "opencode.span.kind");
            string op = kind switch
            {
                "request" => "agent.invoke",
                "step" => "llm.invoke",
                "tool" => "tool.invoke",
                _ => record.Op,
            };

            string name = string.Equals(kind, "tool", StringComparison.Ordinal)
                ? ReadNonEmptyString(raw.Attributes, "opencode.tool.name") ?? record.Name
                : record.Name;

            string? sessionId = ReadNonEmptyString(raw.Attributes, "opencode.session.id") ?? record.SessionId;

            Dictionary<string, JsonElement> measurability = new(StringComparer.Ordinal);
            TokenUsage tokens = record.Tokens;
            if (string.Equals(kind, "step", StringComparison.Ordinal))
            {
                long? input = ReadOptionalCounter(raw.Attributes, ["opencode.usage.input"]);
                long? cacheRead = ReadOptionalCounter(raw.Attributes, ["opencode.usage.cache_read"]);
                long? cacheCreation = ReadOptionalCounter(raw.Attributes, ["opencode.usage.cache_write"]);
                long? output = ReadOptionalCounter(raw.Attributes, ["opencode.usage.output"]);
                long? reasoning = ReadOptionalCounter(raw.Attributes, ["opencode.usage.reasoning"]);

                // Sum in decimal: it cannot wrap, and a negative reasoning count must not read as an
                // overflow. The comparisons keep the cast inside long's range.
                decimal adjustedSum = (decimal)(output ?? 0) + (reasoning ?? 0);
                bool outputOverflowed = output.HasValue
                    && (adjustedSum > long.MaxValue || adjustedSum < long.MinValue);
                long adjustedOutput = output.HasValue && !outputOverflowed ? (long)adjustedSum : 0;
                long? adjustedReasoning = output.HasValue && reasoning.HasValue && !outputOverflowed
                    ? reasoning.Value
                    : null;

                tokens = ExclusiveConvention(
                    input ?? 0,
                    cacheRead ?? 0,
                    cacheCreation ?? 0,
                    adjustedOutput,
                    adjustedReasoning);

                bool inputReported = input.HasValue || cacheRead.HasValue || cacheCreation.HasValue;
                measurability["token_usage"] = inputReported
                    ? MeasuredElement()
                    : NotMeasurableElement("OpenCode telemetry did not report input token usage for this span.");
                if (!output.HasValue)
                {
                    measurability["output"] = NotMeasurableElement(
                        "OpenCode telemetry did not report output token usage for this span.");
                }
                else if (outputOverflowed)
                {
                    measurability["output"] = NotMeasurableElement(
                        "OpenCode reported an output count whose sum with its reasoning count overflows a 64-bit counter.");
                }
                else
                {
                    measurability["output"] = MeasuredElement();
                }
                measurability["cache_read"] = cacheRead.HasValue
                    ? MeasuredElement()
                    : NotMeasurableElement("OpenCode did not export a cache read counter.");
                measurability["cache_creation"] = cacheCreation.HasValue
                    ? MeasuredElement()
                    : NotMeasurableElement("OpenCode did not export a cache creation counter.");
            }
            else
            {
                const string stepOnlyReason = "OpenCode reports token usage on step spans, not on request or tool spans.";
                measurability["token_usage"] = NotMeasurableElement(stepOnlyReason);
                measurability["output"] = NotMeasurableElement(stepOnlyReason);
                measurability["cache_read"] = NotMeasurableElement(stepOnlyReason);
                measurability["cache_creation"] = NotMeasurableElement(stepOnlyReason);
            }

            List<ContentPart> parts = [.. record.Parts ?? []];
            int order = parts.Count;
            string? prompt = ReadNonEmptyString(raw.Attributes, "opencode.prompt.text");
            if (prompt is not null)
                parts.Add(new ContentPart("conversation_history", prompt, Order: order++));
            string? system = ReadNonEmptyString(raw.Attributes, "opencode.context.system");
            if (system is not null)
                parts.Add(new ContentPart("system_prompt", system, Order: order++));
            string? tools = ReadNonEmptyString(raw.Attributes, "opencode.context.tools");
            if (tools is not null)
                parts.Add(new ContentPart("tool_definitions", tools, Order: order++));

            if (string.Equals(kind, "request", StringComparison.Ordinal))
            {
                measurability["user_prompt"] = prompt is not null
                    ? MeasuredElement()
                    : NotMeasurableElement("OpenCode content capture is off, so this span did not capture user_prompt; turn OpenCode capture on in KyberDash Settings to record it.");
            }
            else if (string.Equals(kind, "step", StringComparison.Ordinal))
            {
                string? skills = ReadNonEmptyString(raw.Attributes, "opencode.context.skills");
                measurability["system_prompt"] = system is not null
                    ? MeasuredElement()
                    : NotMeasurableElement("OpenCode content capture is off, so this span did not capture system_prompt; turn OpenCode capture on in KyberDash Settings to record it.");
                measurability["tool_definitions"] = tools is not null
                    ? MeasuredElement()
                    : NotMeasurableElement("OpenCode content capture is off, so this span did not capture tool_definitions; turn OpenCode capture on in KyberDash Settings to record it.");
                measurability["skills_listed"] = skills is not null || system is not null
                    ? MeasuredElement()
                    : NotMeasurableElement("OpenCode content capture is off, so this span did not capture skills_listed; turn OpenCode capture on in KyberDash Settings to record it.");
            }

            double? reportedCost = ReadFiniteDouble(raw.Attributes, "opencode.cost");
            CostBlock cost = reportedCost is >= 0
                ? new CostBlock(CostBasis.Harness, CostStatus.Priced, Value: reportedCost, Currency: "USD")
                : new CostBlock(CostBasis.Unknown, CostStatus.NoRate);

            Dictionary<string, JsonElement> strippedRaw = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonElement> entry in raw.Attributes)
            {
                if (!string.Equals(entry.Key, "opencode.prompt.text", StringComparison.Ordinal)
                    && !entry.Key.StartsWith("opencode.context.", StringComparison.Ordinal))
                {
                    strippedRaw[entry.Key] = entry.Value.Clone();
                }
            }

            string? modelId = FirstNonEmptyString(raw.Attributes, CanonicalModelKeys) is null
                ? ReadNonEmptyString(raw.Attributes, "opencode.model.id")
                : null;
            if (modelId is not null)
                strippedRaw["gen_ai.request.model"] = JsonSerializer.SerializeToElement(modelId);

            return record with
            {
                SessionId = sessionId,
                Op = op,
                Name = name,
                Tokens = tokens,
                Cost = cost,
                Parts = parts,
                Content = CanonValidation.ContentFromParts(parts),
                Measurability = measurability,
                Raw = strippedRaw,
            };
        }

        public Problem? Validate(CanonicalRecord record) => ValidateRecordTokens(record);
    }

    private class DroidAdapter : IHarnessAdapter
    {
        public string Name => "droid";
        public IReadOnlyList<string> Namespaces { get; } = ["factory"];

        public double Detect(RawSpan span) => IsSupportedDroidEnvelope(span) ? 1.0 : 0.0;

        public CanonicalRecord Normalize(RawSpan raw)
        {
            CanonicalRecord record = BaseRecord(this, [], raw);
            if (!IsDroidEnvelopeSpan(raw))
                return record;

            string op = raw.Name switch
            {
                "factory.session" => "agent.invoke",
                "factory.turn" => "llm.invoke",
                "factory.tool" => "tool.invoke",
                _ => record.Op,
            };

            string? sessionId = FirstNonEmptyString(raw.Attributes, DroidSessionKeys) ?? record.SessionId;
            Dictionary<string, JsonElement> bridgedRaw = record.Raw is not null
                ? new Dictionary<string, JsonElement>(record.Raw, StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            if (FirstNonEmptyString(raw.Attributes, CanonicalModelKeys) is null)
            {
                string? droidModel = FirstNonEmptyString(raw.Attributes, DroidModelKeys);
                if (droidModel is not null)
                    bridgedRaw["gen_ai.request.model"] = JsonSerializer.SerializeToElement(droidModel);
            }

            string? toolName = FirstNonEmptyString(raw.Attributes, DroidToolNameKeys);
            if (toolName is not null)
                bridgedRaw["gen_ai.tool.name"] = JsonSerializer.SerializeToElement(toolName);

            List<ContentPart> parts = [.. record.Parts ?? []];
            Dictionary<string, JsonElement> measurability = record.Measurability is not null
                ? new Dictionary<string, JsonElement>(record.Measurability, StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            string? prompt = ReadNonEmptyString(raw.Attributes, "factory.turn.prompt.text");
            if (prompt is not null)
            {
                parts.Add(new ContentPart(
                    "conversation_history",
                    CanonValidation.TruncateContentPartUtf8(prompt),
                    Role: "user",
                    Order: parts.Count));
                measurability["user_prompt"] = MeasuredElement();
            }
            else if (string.Equals(raw.Name, "factory.turn", StringComparison.Ordinal))
            {
                measurability["user_prompt"] = NotMeasurableElement(
                    "Factory's content consent is off, so this turn did not capture the prompt text; turn the Factory capture on in KyberDash Settings to record it.");
            }

            long? input = ReadOptionalCounter(raw.Attributes, InputTokenKeys);
            long? output = ReadOptionalCounter(raw.Attributes, OutputTokenKeys);
            long? cacheRead = ReadOptionalCounter(raw.Attributes, CacheReadKeys);
            long? cacheCreation = ReadOptionalCounter(raw.Attributes, CacheCreationKeys);

            TokenUsage tokens = record.Tokens;
            if (!input.HasValue && !output.HasValue && !cacheRead.HasValue && !cacheCreation.HasValue)
            {
                measurability["token_usage"] = NotMeasurableElement(
                    "Droid telemetry did not report token usage for this span; the Factory session file's companion settings supply it when this session's id matches a file-sourced session.");
                measurability["output"] = NotMeasurableElement(
                    "Droid telemetry did not report output token usage for this span; the Factory session file's companion settings supply it when this session's id matches a file-sourced session.");
                measurability["cache_read"] = NotMeasurableElement(
                    "Droid did not export a cache read counter; the Factory session file's companion settings supply it when this session's id matches a file-sourced session.");
                measurability["cache_creation"] = NotMeasurableElement(
                    "Droid did not export a cache creation counter; the Factory session file's companion settings supply it when this session's id matches a file-sourced session.");
            }
            else
            {
                tokens = ConvertInclusiveCounts(
                    input ?? 0,
                    cacheRead ?? 0,
                    cacheCreation ?? 0,
                    output ?? 0,
                    input.HasValue);
            }

            return record with
            {
                Op = op,
                SessionId = sessionId,
                Parts = parts,
                Content = CanonValidation.ContentFromParts(parts),
                Measurability = measurability,
                Tokens = tokens,
                Raw = bridgedRaw,
            };
        }

        public Problem? Validate(CanonicalRecord record) => ValidateRecordTokens(record);
    }
}
