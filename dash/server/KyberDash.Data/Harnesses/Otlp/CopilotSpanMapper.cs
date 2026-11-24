using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Pure <see cref="IHarnessMapper"/> for Copilot OTLP trace spans, translating decoded
/// <see cref="OtlpSpan"/> payloads via <see cref="OtlpSpanNative"/> into static
/// <see cref="CollectedRecord"/> and <see cref="CollectedBatch"/> DTOs using Copilot's
/// cache-inclusive token convention.
/// </summary>
/// <remarks>
/// Only the <c>CopilotAdapter</c> evidence reaches the DTOs: usage-bearing spans map to
/// <c>llm.invoke</c> with <see cref="CanonAdapters.ConvertInclusiveCounts"/>,
/// <c>gen_ai.tool.name</c> spans map to <c>tool.invoke</c>, and <c>invoke_agent</c> spans map to
/// <c>agent.invoke</c>, mirroring <c>CanonAdapters.CanonicalOp</c>. No Copilot Chat span shapes
/// beyond those evidenced keys are assumed.
/// </remarks>
public partial class CopilotSpanMapper : OtlpSpanMapperBase
{
    /// <summary>Canonical harness identifier emitted for Copilot OTLP spans.</summary>
    public const string CopilotHarnessId = "copilot";

    /// <inheritdoc />
    protected override string CanonicalHarnessId => CopilotHarnessId;

    private static readonly OtlpSpanMapperCore.OtlpMapperHooks MapperHooks = new(OtlpSpanMapperCore.ResolveGenericOp, TryBuildTokenUsage);

    /// <summary>Initializes a new instance of the <see cref="CopilotSpanMapper"/> class.</summary>
    public CopilotSpanMapper()
        : base(MapperHooks)
    {
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

        usage = CanonAdapters.ConvertInclusiveCounts(input, cacheRead, cacheCreation, output, native.InputTokens.HasValue, reasoning);
        return true;
    }
}
