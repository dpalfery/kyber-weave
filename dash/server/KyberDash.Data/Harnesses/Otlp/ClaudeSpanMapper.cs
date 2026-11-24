using KyberDash.Data.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Pure <see cref="IHarnessMapper"/> for Claude Code OTLP trace spans, translating decoded
/// <see cref="OtlpSpan"/> payloads via <see cref="OtlpSpanNative"/> into static
/// <see cref="CollectedRecord"/> and <see cref="CollectedBatch"/> DTOs using Claude's
/// cache-exclusive token convention.
/// </summary>
public partial class ClaudeSpanMapper : OtlpSpanMapperBase
{
    /// <inheritdoc />
    protected override string CanonicalHarnessId => TranscriptReaderRepository.ClaudeHarnessId;

    private static readonly OtlpSpanMapperCore.OtlpMapperHooks MapperHooks = new(
        ResolveOp,
        TryBuildTokenUsage,
        EnrichMcpNames: static (native, toolName) => TryParseClaudeMcpToolName(toolName, out string server, out string tool) ? (server, tool) : null);

    /// <summary>Initializes a new instance of the <see cref="ClaudeSpanMapper"/> class.</summary>
    public ClaudeSpanMapper()
        : base(MapperHooks)
    {
    }

    private static RecordOp ResolveOp(OtlpSpanNative native)
    {
        if (string.Equals(native.SpanType, "tool", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(native.ToolName))
        {
            return RecordOp.ToolInvoke;
        }

        return OtlpSpanMapperCore.ResolveGenericOp(native);
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
        long? reasoning = native.ReasoningTokens is long r && r != 0 ? r : null;

        usage = CanonAdapters.ExclusiveConvention(input, cacheRead, cacheCreation, output, reasoning);
        return true;
    }

    private static bool TryParseClaudeMcpToolName(string toolName, out string serverName, out string mcpToolName)
    {
        const string Prefix = "mcp__";
        if (!toolName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            serverName = string.Empty;
            mcpToolName = string.Empty;
            return false;
        }

        string remainder = toolName[Prefix.Length..];
        int split = remainder.IndexOf("__", StringComparison.Ordinal);
        if (split <= 0 || split + 2 >= remainder.Length)
        {
            serverName = string.Empty;
            mcpToolName = string.Empty;
            return false;
        }

        string server = remainder[..split];
        string tool = remainder[(split + 2)..];
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tool))
        {
            serverName = string.Empty;
            mcpToolName = string.Empty;
            return false;
        }

        serverName = server;
        mcpToolName = tool;
        return true;
    }
}
