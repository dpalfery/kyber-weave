using System.Text.RegularExpressions;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Pure <see cref="IHarnessMapper"/> for OpenCode OTLP trace spans, translating decoded
/// <see cref="OtlpSpan"/> payloads via <see cref="OtlpSpanNative"/> into static
/// <see cref="CollectedRecord"/> and <see cref="CollectedBatch"/> DTOs using OpenCode's
/// step-scoped cache-exclusive token convention.
/// </summary>
/// <remarks>
/// Only contract-<c>3</c> plugin envelopes (<c>opencode.contract_version == "3"</c>) resolve ops
/// from <c>opencode.span.kind</c>: <c>step</c> maps to <c>llm.invoke</c>, <c>tool</c> maps to
/// <c>tool.invoke</c> (named by <c>opencode.tool.name</c>), and <c>request</c> maps to
/// <c>agent.invoke</c>. Usage counters (<c>opencode.usage.*</c>) exist only on <c>step</c> spans
/// and fold reasoning into output with the same overflow fold as <c>OpencodeAdapter</c>;
/// unsupported envelopes fall back to generic op resolution with no usage tokens.
/// <c>opencode.prompt.text</c>, <c>opencode.context.system</c>, and <c>opencode.context.tools</c>
/// map to conversation-history, system-prompt, and tool-definition parts measured through
/// <see cref="ToolContentMeasurer"/>, so JSON payloads are withheld, never stored raw.
/// </remarks>
public partial class OpenCodeSpanMapper : OtlpSpanMapperBase
{
    [GeneratedRegex(@"^invoke_agent(?:[ :]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvokeAgentNameRegex();

    /// <summary>Canonical harness identifier emitted for OpenCode OTLP spans.</summary>
    public const string OpenCodeHarnessId = "opencode";

    /// <summary>Supported OpenCode plugin wire contract version.</summary>
    public const string SupportedContractVersion = CanonAdapters.OpencodeContractVersion;

    /// <summary>OpenCode span kind attribute keys (<c>step</c>, <c>tool</c>, <c>request</c>).</summary>
    public static readonly IReadOnlyList<string> SpanKindKeys = ["opencode.span.kind"];

    /// <summary>OpenCode native session identifier attribute keys.</summary>
    public static readonly IReadOnlyList<string> SessionKeys = ["opencode.session.id"];

    /// <summary>OpenCode model identifier attribute keys.</summary>
    public static readonly IReadOnlyList<string> ModelKeys = ["opencode.model.id"];

    /// <summary>OpenCode tool name attribute keys.</summary>
    public static readonly IReadOnlyList<string> ToolNameKeys = ["opencode.tool.name"];

    /// <summary>OpenCode harness-reported cost attribute keys.</summary>
    public static readonly IReadOnlyList<string> CostKeys = ["opencode.cost"];

    /// <summary>OpenCode step input token attribute keys.</summary>
    public static readonly IReadOnlyList<string> InputKeys = ["opencode.usage.input"];

    /// <summary>OpenCode step cache-read token attribute keys.</summary>
    public static readonly IReadOnlyList<string> CacheReadKeys = ["opencode.usage.cache_read"];

    /// <summary>OpenCode step cache-write token attribute keys.</summary>
    public static readonly IReadOnlyList<string> CacheWriteKeys = ["opencode.usage.cache_write"];

    /// <summary>OpenCode step output token attribute keys.</summary>
    public static readonly IReadOnlyList<string> OutputKeys = ["opencode.usage.output"];

    /// <summary>OpenCode step reasoning token attribute keys.</summary>
    public static readonly IReadOnlyList<string> ReasoningKeys = ["opencode.usage.reasoning"];

    /// <summary>OpenCode user prompt capture attribute keys.</summary>
    public static readonly IReadOnlyList<string> PromptTextKeys = ["opencode.prompt.text"];

    /// <summary>OpenCode system context capture attribute keys.</summary>
    public static readonly IReadOnlyList<string> ContextSystemKeys = ["opencode.context.system"];

    /// <summary>OpenCode tool context capture attribute keys.</summary>
    public static readonly IReadOnlyList<string> ContextToolsKeys = ["opencode.context.tools"];

    /// <inheritdoc />
    protected override string CanonicalHarnessId => OpenCodeHarnessId;

    private static readonly OtlpSpanMapperCore.OtlpMapperHooks MapperHooks = new(
        ResolveOp,
        TryBuildTokenUsage,
        ResolveSessionKey: ResolveSession,
        ResolveModel: ResolveModel,
        ResolveToolName: ResolveToolName,
        ResolveCostUsd: static native => OtlpSpanNative.ReadFiniteDouble(native.Attributes, CostKeys),
        AppendParts: AppendOpenCodeParts);

    /// <summary>Initializes a new instance of the <see cref="OpenCodeSpanMapper"/> class.</summary>
    public OpenCodeSpanMapper()
        : base(MapperHooks)
    {
    }

    private static RecordOp ResolveOp(OtlpSpanNative native)
    {
        if (IsSupportedContract(native))
        {
            string? kind = OtlpSpanNative.FirstNonEmptyString(native.Attributes, SpanKindKeys);
            RecordOp kindOp = kind switch
            {
                "request" => RecordOp.AgentInvoke,
                "step" => RecordOp.LlmInvoke,
                "tool" => RecordOp.ToolInvoke,
                _ => RecordOp.Unspecified,
            };

            if (kindOp != RecordOp.Unspecified)
            {
                return kindOp;
            }
        }

        return ResolveGenericOp(native);
    }

    private static RecordOp ResolveGenericOp(OtlpSpanNative native)
    {
        if (OtlpSpanNative.HasAnyKey(native.Attributes, ["gen_ai.tool.name"]))
        {
            return RecordOp.ToolInvoke;
        }

        if (string.Equals(native.OperationName, "invoke_agent", StringComparison.Ordinal)
            || InvokeAgentNameRegex().IsMatch(native.Name))
        {
            return RecordOp.AgentInvoke;
        }

        if (OtlpSpanNative.HasAnyKey(native.Attributes, OtlpSpanNative.InputTokenKeys)
            || OtlpSpanNative.HasAnyKey(native.Attributes, OtlpSpanNative.OutputTokenKeys))
        {
            return RecordOp.LlmInvoke;
        }

        return RecordOp.Unspecified;
    }

    private static bool IsSupportedContract(OtlpSpanNative native) =>
        string.Equals(
            OtlpSpanNative.ReadNonEmptyString(native.Attributes, OtlpSpanNative.OpencodeContractVersionKey),
            SupportedContractVersion,
            StringComparison.Ordinal);

    private static string? ResolveSession(OtlpSpanNative native) =>
        OtlpSpanNative.FirstSafeString(native.Attributes, SessionKeys) ?? native.SessionId;

    private static string? ResolveModel(OtlpSpanNative native) =>
        OtlpSpanMapperCore.SafeOptionalText(native.Model) ?? OtlpSpanNative.FirstSafeString(native.Attributes, ModelKeys);

    private static string? ResolveToolName(OtlpSpanNative native) =>
        OtlpSpanNative.FirstSafeString(native.Attributes, ToolNameKeys) ?? native.ToolName;

    private static bool TryBuildTokenUsage(OtlpSpanNative native, RecordOp op, out TokenUsage? usage)
    {
        usage = null;
        if (!IsSupportedContract(native)
            || !string.Equals(
                OtlpSpanNative.FirstNonEmptyString(native.Attributes, SpanKindKeys),
                "step",
                StringComparison.Ordinal))
        {
            return false;
        }

        long? input = OtlpSpanNative.ReadOptionalCounter(native.Attributes, InputKeys);
        long? cacheRead = OtlpSpanNative.ReadOptionalCounter(native.Attributes, CacheReadKeys);
        long? cacheCreation = OtlpSpanNative.ReadOptionalCounter(native.Attributes, CacheWriteKeys);
        long? output = OtlpSpanNative.ReadOptionalCounter(native.Attributes, OutputKeys);
        long? reasoning = OtlpSpanNative.ReadOptionalCounter(native.Attributes, ReasoningKeys);

        bool hasAnyToken = input.HasValue
            || cacheRead.HasValue
            || cacheCreation.HasValue
            || output.HasValue
            || reasoning.HasValue;

        if (!hasAnyToken && op != RecordOp.LlmInvoke)
        {
            return false;
        }

        decimal adjustedSum = (decimal)(output ?? 0) + (reasoning ?? 0);
        bool outputOverflowed = output.HasValue
            && (adjustedSum > long.MaxValue || adjustedSum < long.MinValue);
        long adjustedOutput = output.HasValue && !outputOverflowed ? (long)adjustedSum : 0;
        long? adjustedReasoning = output.HasValue && reasoning.HasValue && !outputOverflowed
            ? reasoning.Value
            : null;

        usage = CanonAdapters.ExclusiveConvention(
            input ?? 0,
            cacheRead ?? 0,
            cacheCreation ?? 0,
            adjustedOutput,
            adjustedReasoning);
        return true;
    }

    private static void AppendOpenCodeParts(OtlpSpanNative native, List<CollectedContentPart> parts)
    {
        (IReadOnlyList<string> Keys, ContentBucket Bucket)[] sources =
        [
            (PromptTextKeys, ContentBucket.ConversationHistory),
            (ContextSystemKeys, ContentBucket.SystemPrompt),
            (ContextToolsKeys, ContentBucket.ToolDefinitions),
        ];

        HashSet<int> usedPositions = [.. parts.Select(static p => p.Position)];
        int nextPosition = parts.Count == 0 ? 0 : parts.Max(static p => p.Position) + 1;

        foreach ((IReadOnlyList<string> keys, ContentBucket bucket) in sources)
        {
            string? text = OtlpSpanNative.FirstNonEmptyString(native.Attributes, keys);
            if (text is null)
            {
                continue;
            }

            int position = nextPosition;
            while (!usedPositions.Add(position))
            {
                position++;
            }

            nextPosition = Math.Max(nextPosition, position + 1);

            ToolContentMeasurement measurement = ToolContentMeasurer.Measure(text);
            string? plainText = measurement.IsDocument ? null : measurement.PlainText;
            CollectedWithheldText? withheld = measurement.IsDocument ? measurement.ToWithheldText() : null;
            long? originalBytes = measurement.IsDocument ? null : measurement.OriginalUtf8BytesWhenTruncated;

            parts.Add(new CollectedContentPart(
                Position: position,
                Bucket: bucket,
                PlainText: plainText,
                Withheld: withheld,
                ReportedTokens: null,
                Server: null,
                Role: null,
                Resident: null,
                OriginalUtf8BytesWhenTruncated: originalBytes));
        }
    }
}
