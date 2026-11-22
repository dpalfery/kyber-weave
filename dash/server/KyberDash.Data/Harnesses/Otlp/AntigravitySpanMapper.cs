using System.Text.RegularExpressions;
using KyberDash.Data.Collector;
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
public partial class AntigravitySpanMapper : OtlpSpanMapperBase
{
    [GeneratedRegex(@"^invoke_agent(?:[ :]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvokeAgentNameRegex();

    /// <summary>Canonical harness identifier emitted for Antigravity OTLP spans.</summary>
    public const string AntigravityHarnessId = "antigravity";

    /// <inheritdoc />
    protected override string CanonicalHarnessId => AntigravityHarnessId;

    private static readonly OtlpSpanMapperCore.OtlpMapperHooks MapperHooks = new(ResolveOp, TryBuildTokenUsage);

    /// <summary>Initializes a new instance of the <see cref="AntigravitySpanMapper"/> class.</summary>
    public AntigravitySpanMapper()
        : base(MapperHooks)
    {
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
}
