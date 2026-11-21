using System.Text.RegularExpressions;
using KyberDash.Data.Collector;
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
public partial class DroidSpanMapper : OtlpSpanMapperBase
{
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
    protected override string CanonicalHarnessId => DroidHarnessId;

    private static readonly OtlpSpanMapperCore.OtlpMapperHooks MapperHooks = new(
        ResolveOp,
        TryBuildTokenUsage,
        ResolveSessionKey: ResolveSession,
        ResolveModel: ResolveModel,
        ResolveToolName: static native => OtlpSpanNative.FirstSafeString(native.Attributes, OtlpSpanNative.DroidToolNameKeys) ?? native.ToolName,
        ResolveToolCallId: static native => OtlpSpanNative.FirstSafeString(native.Attributes, DroidToolCallIdKeys) ?? native.ToolCallId,
        ResolveToolErrorCode: static native => OtlpSpanNative.FirstSafeString(native.Attributes, DroidToolErrorTypeKeys) ?? native.ToolErrorCode,
        ResolveExtraToolStatus: static native => OtlpSpanNative.FirstSafeString(native.Attributes, DroidToolStatusKeys),
        ShouldSkipSpan: IsGeminiAttributed);

    /// <summary>Initializes a new instance of the <see cref="DroidSpanMapper"/> class.</summary>
    public DroidSpanMapper()
        : base(MapperHooks)
    {
    }

    /// <summary>
    /// Parses <paramref name="span"/> into an <see cref="OtlpSpanNative"/> using the canonical
    /// OTLP attribute key lists, appending the Droid turn prompt as a <c>conversation_history</c>
    /// part when <c>factory.turn.prompt.text</c> is present.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Extracted <see cref="OtlpSpanNative"/>.</returns>
    public override OtlpSpanNative Parse(OtlpSpan span)
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
    /// Returns whether <paramref name="native"/> carries Gemini attribution, either via
    /// <c>gen_ai.system == "gemini"</c> or any <c>gemini.*</c> attribute key.
    /// </summary>
    /// <param name="native">Parsed OTLP span.</param>
    /// <returns><see langword="true"/> when the span is Gemini-attributed and must be excluded.</returns>
    public static bool IsGeminiAttributed(OtlpSpanNative native) =>
        OtlpSpanNative.IsGeminiAttributed(native);

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
}
