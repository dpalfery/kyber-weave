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
/// Pure <see cref="IHarnessMapper"/> for Antigravity OTLP trace spans, translating decoded
/// <see cref="OtlpSpan"/> payloads via <see cref="OtlpSpanNative"/> into static
/// <see cref="CollectedRecord"/> and <see cref="CollectedBatch"/> DTOs using Antigravity's
/// cache-inclusive token convention.
/// </summary>
/// <remarks>
/// Antigravity emits canonical <c>gen_ai.*</c> attributes with Gemini thought counters:
/// <c>gen_ai.agent.name = "antigravity"</c> carries vendor evidence, usage-bearing spans map to
/// <c>llm.invoke</c> with <see cref="CanonAdapters.ConvertInclusiveCounts"/> forcing
/// <c>cache_creation</c> to zero (unexported, mirroring <c>AntigravityAdapter</c>), nonzero
/// <c>gen_ai.usage.reasoning_tokens</c> wins over <c>gemini.usage.thoughts_tokens</c>,
/// <c>gen_ai.tool.name</c> spans map to <c>tool.invoke</c>, and <c>invoke_agent</c> spans map to
/// <c>agent.invoke</c>, mirroring <c>CanonAdapters.CanonicalOp</c>.
/// </remarks>
public partial class AntigravitySpanMapper : IHarnessMapper
{
    private const string MalformedOtlpCode = "MALFORMED_OTLP_PAYLOAD";

    [GeneratedRegex(@"^invoke_agent(?:[ :]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvokeAgentNameRegex();

    /// <summary>Canonical harness identifier emitted for Antigravity OTLP spans.</summary>
    public const string AntigravityHarnessId = "antigravity";

    /// <inheritdoc />
    public string HarnessId => AntigravityHarnessId;

    private static readonly OtlpSpanMapperCore.OtlpMapperHooks Hooks = new(ResolveOp, TryBuildTokenUsage);

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
    /// OTLP attribute key lists.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Extracted <see cref="OtlpSpanNative"/>.</returns>
    public OtlpSpanNative Parse(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        return OtlpSpanNative.Parse(span);
    }

    /// <summary>
    /// Maps a single decoded Antigravity OTLP <paramref name="span"/> into a normalized
    /// <see cref="CollectedRecord"/>.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Normalized <see cref="CollectedRecord"/>.</returns>
    public CollectedRecord MapSpan(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        OtlpSpanNative native = Parse(span);
        return OtlpSpanMapperCore.MapNative(native, AntigravityHarnessId, Hooks);
    }

    /// <summary>
    /// Maps a batch of decoded Antigravity OTLP <paramref name="spans"/> into sessions, validated
    /// records, and token/ingest problems.
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
            || native.ReasoningTokens.HasValue
            || native.ThoughtTokens.HasValue;

        if (!hasAnyToken && op != RecordOp.LlmInvoke)
        {
            usage = null;
            return false;
        }

        long input = native.InputTokens ?? 0;
        long cacheRead = native.CacheReadTokens ?? 0;
        long output = native.OutputTokens ?? 0;
        bool inputPresent = OtlpSpanNative.HasAnyKey(native.Attributes, OtlpSpanNative.InputTokenKeys);

        long reasoningValue = native.ReasoningTokens ?? 0;
        if (reasoningValue == 0)
        {
            reasoningValue = native.ThoughtTokens ?? 0;
        }

        long? thoughts = reasoningValue != 0 ? reasoningValue : null;

        usage = CanonAdapters.ConvertInclusiveCounts(input, cacheRead, 0, output, inputPresent, thoughts);
        return true;
    }

    private static string ResolveHarnessId(string? unitHarnessId)
    {
        if (string.IsNullOrWhiteSpace(unitHarnessId))
        {
            return AntigravityHarnessId;
        }

        string normalized = HarnessSourceRegistry.NormalizeHarnessId(unitHarnessId);
        return string.Equals(normalized, "antigravity", StringComparison.Ordinal)
            ? AntigravityHarnessId
            : normalized;
    }
}
