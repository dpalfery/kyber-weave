using System.Text.Json;
using System.Text.RegularExpressions;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Pure <see cref="IHarnessMapper"/> for Pi OTLP trace spans, translating decoded
/// <see cref="OtlpSpan"/> payloads via <see cref="OtlpSpanNative"/> into static
/// <see cref="CollectedRecord"/> and <see cref="CollectedBatch"/> DTOs using Pi's
/// cache-exclusive token convention.
/// </summary>
/// <remarks>
/// Only the <c>PiAdapter</c> evidence reaches the DTOs: usage-bearing spans map to
/// <c>llm.invoke</c> with <see cref="CanonAdapters.ExclusiveConvention"/>,
/// <c>gen_ai.tool.name</c> spans map to <c>tool.invoke</c>, and <c>invoke_agent</c> spans map to
/// <c>agent.invoke</c>, mirroring <c>CanonAdapters.CanonicalOp</c>. Pi content keys
/// (<c>pi.tools.selected</c>, <c>pi.tools.snippets</c>, <c>pi.skills</c>,
/// <c>pi.context_files</c>) are appended as <c>tool_definitions</c> and
/// <c>instruction_context</c> parts. Gemini-attributed spans (<c>gen_ai.system == "gemini"</c>
/// or any <c>gemini.*</c> attribute) are excluded from batches so they yield zero stored records.
/// </remarks>
public partial class PiSpanMapper : OtlpSpanMapperBase
{
    /// <summary>Canonical harness identifier emitted for Pi OTLP spans.</summary>
    public const string PiHarnessId = "pi";

    /// <summary>Pi tool inventory attribute keys mapped to <c>tool_definitions</c> parts.</summary>
    public static readonly IReadOnlyList<string> PiToolKeys = ["pi.tools.selected", "pi.tools.snippets"];

    /// <summary>Pi skill and context attribute keys mapped to <c>instruction_context</c> parts.</summary>
    public static readonly IReadOnlyList<string> PiInstructionKeys = ["pi.skills", "pi.context_files"];

    [GeneratedRegex(@"^invoke_agent(?:[ :]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvokeAgentNameRegex();

    /// <inheritdoc />
    protected override string CanonicalHarnessId => PiHarnessId;

    private static readonly OtlpSpanMapperCore.OtlpMapperHooks MapperHooks = new(
        ResolveOp,
        TryBuildTokenUsage,
        ShouldSkipSpan: IsGeminiAttributed);

    /// <summary>Initializes a new instance of the <see cref="PiSpanMapper"/> class.</summary>
    public PiSpanMapper()
        : base(MapperHooks)
    {
    }

    /// <summary>
    /// Parses <paramref name="span"/> into an <see cref="OtlpSpanNative"/> using the canonical
    /// OTLP attribute key lists, appending the Pi tool and instruction keys as
    /// <c>tool_definitions</c> and <c>instruction_context</c> parts.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Extracted <see cref="OtlpSpanNative"/>.</returns>
    public override OtlpSpanNative Parse(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        OtlpSpanNative native = OtlpSpanNative.Parse(span);

        List<ContentPart> parts = [.. native.Parts];
        foreach (string key in PiToolKeys)
        {
            string? text = ReadRawPartText(native.Attributes, key);
            if (text is not null)
            {
                parts.Add(new ContentPart("tool_definitions", text, Order: parts.Count));
            }
        }

        foreach (string key in PiInstructionKeys)
        {
            string? text = ReadRawPartText(native.Attributes, key);
            if (text is not null)
            {
                parts.Add(new ContentPart("instruction_context", text, Order: parts.Count));
            }
        }

        return parts.Count == native.Parts.Count ? native : native with { Parts = parts };
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

        string? system = OtlpSpanNative.FirstSafeString(native.Attributes, ["gen_ai.system"]);
        if (string.Equals(system, "gemini", StringComparison.Ordinal))
        {
            return true;
        }

        return OtlpSpanNative.HasNamespace(native.Attributes, ["gemini"]);
    }

    private static RecordOp ResolveOp(OtlpSpanNative native)
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

    private static bool TryBuildTokenUsage(OtlpSpanNative native, RecordOp op, out TokenUsage? usage)
    {
        bool hasAnyToken = native.InputTokens.HasValue
            || native.OutputTokens.HasValue
            || native.CacheReadTokens.HasValue
            || native.CacheCreationTokens.HasValue
            || native.ReasoningTokens.HasValue;

        if (!hasAnyToken && op != RecordOp.LlmInvoke)
        {
            usage = null;
            return false;
        }

        long input = native.InputTokens ?? 0;
        long cacheRead = native.CacheReadTokens ?? 0;
        long cacheCreation = native.CacheCreationTokens ?? 0;
        long output = native.OutputTokens ?? 0;
        long? reasoning = native.ReasoningTokens is long raw && raw != 0 ? raw : null;

        usage = CanonAdapters.ExclusiveConvention(input, cacheRead, cacheCreation, output, reasoning);
        return true;
    }

    private static string? ReadRawPartText(IReadOnlyDictionary<string, JsonElement> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return element.GetRawText();
        }

        return null;
    }
}
