using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
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
public partial class PiSpanMapper : IHarnessMapper
{
    private const string MalformedOtlpCode = "MALFORMED_OTLP_PAYLOAD";

    /// <summary>Canonical harness identifier emitted for Pi OTLP spans.</summary>
    public const string PiHarnessId = "pi";

    /// <summary>Pi tool inventory attribute keys mapped to <c>tool_definitions</c> parts.</summary>
    public static readonly IReadOnlyList<string> PiToolKeys = ["pi.tools.selected", "pi.tools.snippets"];

    /// <summary>Pi skill and context attribute keys mapped to <c>instruction_context</c> parts.</summary>
    public static readonly IReadOnlyList<string> PiInstructionKeys = ["pi.skills", "pi.context_files"];

    [GeneratedRegex(@"^invoke_agent(?:[ :]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvokeAgentNameRegex();

    /// <inheritdoc />
    public string HarnessId => PiHarnessId;

    private static readonly OtlpSpanMapperCore.OtlpMapperHooks Hooks = new(
        ResolveOp,
        TryBuildTokenUsage,
        ShouldSkipSpan: IsGeminiAttributed);

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        string harnessId = ResolveHarnessId(unit.Unit.HarnessId);
        CollectedSource source = new(harnessId, unit.Unit.Kind, unit.Unit.SourceKey);

        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        IReadOnlyList<OtlpSpan> spans;
        try
        {
            spans = OtlpCollector.DecodeTracesJson(Encoding.UTF8.GetBytes(unit.RawText));
        }
        catch (Exception ex) when (ex is OtlpDecodeException or OtlpPayloadTooLargeException or JsonException)
        {
            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(unit.Unit.SourceKey)))[..16];
            string message = OtlpSpanMapperCore.SafeOptionalText(ex.Message) ?? "Failed to decode OTLP trace payload.";
            CollectedProblem problem = new(
                ProblemKey: $"otlp:{digest}:{MalformedOtlpCode}",
                Severity: OtlpSpanMapperCore.FormatProblemSeverity(ProblemSeverity.Error),
                Code: MalformedOtlpCode,
                Message: message,
                Location: OtlpSpanMapperCore.SafeOptionalText(unit.Unit.SourceKey),
                HarnessId: harnessId,
                TimestampUtc: importedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            return new CollectedBatch(source, [], [], [problem]);
        }

        return OtlpSpanMapperCore.MapSpans(spans, Parse, unit.Unit.SourceKey, harnessId, Hooks);
    }

    /// <summary>
    /// Parses <paramref name="span"/> into an <see cref="OtlpSpanNative"/> using the canonical
    /// OTLP attribute key lists, appending the Pi tool and instruction keys as
    /// <c>tool_definitions</c> and <c>instruction_context</c> parts.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Extracted <see cref="OtlpSpanNative"/>.</returns>
    public OtlpSpanNative Parse(OtlpSpan span)
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
    /// Maps a single decoded Pi OTLP <paramref name="span"/> into a normalized
    /// <see cref="CollectedRecord"/>.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Normalized <see cref="CollectedRecord"/>.</returns>
    public CollectedRecord MapSpan(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        OtlpSpanNative native = Parse(span);
        return OtlpSpanMapperCore.MapNative(native, PiHarnessId, Hooks);
    }

    /// <summary>
    /// Maps a batch of decoded Pi OTLP <paramref name="spans"/> into sessions, validated
    /// records, and token/ingest problems. Gemini-attributed spans are skipped so they yield
    /// zero stored records.
    /// </summary>
    /// <param name="spans">Decoded OTLP spans.</param>
    /// <param name="sourceLocator">Optional source locator override; defaults to the first span's <c>service.name</c> or <c>"otlp"</c>.</param>
    /// <param name="harnessIdOverride">Optional harness identifier override; defaults to <see cref="HarnessId"/>.</param>
    /// <returns>Static <see cref="CollectedBatch"/> for persistence.</returns>
    public CollectedBatch MapSpans(
        IReadOnlyList<OtlpSpan> spans,
        string? sourceLocator = null,
        string? harnessIdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(spans);

        string harnessId = ResolveHarnessId(harnessIdOverride);
        return OtlpSpanMapperCore.MapSpans(spans, Parse, sourceLocator, harnessId, Hooks);
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

    private static string ResolveHarnessId(string? unitHarnessId)
    {
        if (string.IsNullOrWhiteSpace(unitHarnessId))
        {
            return PiHarnessId;
        }

        string normalized = HarnessSourceRegistry.NormalizeHarnessId(unitHarnessId);
        return string.Equals(normalized, "pi", StringComparison.Ordinal)
            ? PiHarnessId
            : normalized;
    }
}
