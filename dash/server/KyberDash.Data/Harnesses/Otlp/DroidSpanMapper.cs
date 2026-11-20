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
/// Pure <see cref="IHarnessMapper"/> for Factory Droid OTLP trace spans, translating decoded
/// <see cref="OtlpSpan"/> payloads via <see cref="OtlpSpanNative"/> into static
/// <see cref="CollectedRecord"/> and <see cref="CollectedBatch"/> DTOs using Droid's
/// cache-inclusive token convention.
/// </summary>
/// <remarks>
/// Span names drive the operation mapping exactly like <c>DroidAdapter</c>:
/// <c>factory.session</c> maps to <c>agent.invoke</c>, <c>factory.turn</c> maps to
/// <c>llm.invoke</c>, and <c>factory.tool</c> maps to <c>tool.invoke</c>. Droid session, model,
/// tool, and prompt keys (<c>factory.session.id</c>, <c>factory.model.id</c>,
/// <c>factory.tool.name</c>, <c>factory.turn.prompt.text</c>) win over the canonical
/// <c>gen_ai.*</c> keys. Gemini-attributed spans (<c>gen_ai.system == "gemini"</c> or any
/// <c>gemini.*</c> attribute) are excluded from batches so they yield zero stored records.
/// </remarks>
public partial class DroidSpanMapper : IHarnessMapper
{
    private const string MalformedOtlpCode = "MALFORMED_OTLP_PAYLOAD";

    /// <summary>Canonical harness identifier emitted for Droid OTLP spans.</summary>
    public const string DroidHarnessId = "droid";

    /// <summary>Droid turn prompt text attribute keys.</summary>
    public static readonly IReadOnlyList<string> DroidPromptKeys = ["factory.turn.prompt.text"];

    /// <summary>Droid tool call correlation identifier attribute keys.</summary>
    public static readonly IReadOnlyList<string> DroidToolCallIdKeys = ["factory.tool.call_id"];

    /// <summary>Droid tool status attribute keys.</summary>
    public static readonly IReadOnlyList<string> DroidToolStatusKeys = ["factory.tool.status"];

    /// <summary>Droid tool error type attribute keys.</summary>
    public static readonly IReadOnlyList<string> DroidToolErrorTypeKeys = ["factory.tool.error_type"];

    [GeneratedRegex(@"^invoke_agent(?:[ :]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvokeAgentNameRegex();

    /// <inheritdoc />
    public string HarnessId => DroidHarnessId;

    private static readonly OtlpSpanMapperCore.OtlpMapperHooks Hooks = new(
        ResolveOp,
        TryBuildTokenUsage,
        ResolveSessionKey: ResolveSession,
        ResolveModel: ResolveModel,
        ResolveToolName: static native => OtlpSpanNative.FirstSafeString(native.Attributes, OtlpSpanNative.DroidToolNameKeys) ?? native.ToolName,
        ResolveToolCallId: static native => OtlpSpanNative.FirstSafeString(native.Attributes, DroidToolCallIdKeys) ?? native.ToolCallId,
        ResolveToolErrorCode: static native => OtlpSpanNative.FirstSafeString(native.Attributes, DroidToolErrorTypeKeys) ?? native.ToolErrorCode,
        ResolveExtraToolStatus: static native => OtlpSpanNative.FirstSafeString(native.Attributes, DroidToolStatusKeys),
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
    /// OTLP attribute key lists, appending the Droid turn prompt as a <c>conversation_history</c>
    /// part when <c>factory.turn.prompt.text</c> is present.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Extracted <see cref="OtlpSpanNative"/>.</returns>
    public OtlpSpanNative Parse(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        OtlpSpanNative native = OtlpSpanNative.Parse(span);

        string? prompt = OtlpSpanNative.FirstSafeString(native.Attributes, DroidPromptKeys);
        if (prompt is null)
        {
            return native;
        }

        List<ContentPart> parts = [.. native.Parts, new ContentPart("conversation_history", prompt, Role: "user", Order: native.Parts.Count)];
        return native with { Parts = parts };
    }

    /// <summary>
    /// Maps a single decoded Droid OTLP <paramref name="span"/> into a normalized
    /// <see cref="CollectedRecord"/>.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Normalized <see cref="CollectedRecord"/>.</returns>
    public CollectedRecord MapSpan(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        OtlpSpanNative native = Parse(span);
        return OtlpSpanMapperCore.MapNative(native, DroidHarnessId, Hooks);
    }

    /// <summary>
    /// Maps a batch of decoded Droid OTLP <paramref name="spans"/> into sessions, validated
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
        if (string.Equals(native.Name, "factory.tool", StringComparison.Ordinal))
        {
            return RecordOp.ToolInvoke;
        }

        if (string.Equals(native.Name, "factory.session", StringComparison.Ordinal))
        {
            return RecordOp.AgentInvoke;
        }

        if (string.Equals(native.Name, "factory.turn", StringComparison.Ordinal))
        {
            return RecordOp.LlmInvoke;
        }

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
        long? input = native.InputTokens;
        long? cacheRead = native.CacheReadTokens;
        long? cacheCreation = native.CacheCreationTokens;
        long? output = native.OutputTokens;

        if (!input.HasValue && !output.HasValue && !cacheRead.HasValue && !cacheCreation.HasValue && op != RecordOp.LlmInvoke)
        {
            usage = null;
            return false;
        }

        usage = CanonAdapters.ConvertInclusiveCounts(
            input ?? 0,
            cacheRead ?? 0,
            cacheCreation ?? 0,
            output ?? 0,
            input.HasValue);
        return true;
    }

    private static string? ResolveSession(OtlpSpanNative native)
    {
        return OtlpSpanNative.FirstSafeString(native.Attributes, OtlpSpanNative.DroidSessionKeys)
            ?? native.SessionId;
    }

    private static string? ResolveModel(OtlpSpanNative native)
    {
        return OtlpSpanNative.FirstSafeString(native.Attributes, OtlpSpanNative.DroidModelKeys)
            ?? native.Model;
    }

    private static string ResolveHarnessId(string? unitHarnessId)
    {
        if (string.IsNullOrWhiteSpace(unitHarnessId))
        {
            return DroidHarnessId;
        }

        string normalized = HarnessSourceRegistry.NormalizeHarnessId(unitHarnessId);
        return string.Equals(normalized, "droid", StringComparison.Ordinal)
            ? DroidHarnessId
            : normalized;
    }
}
