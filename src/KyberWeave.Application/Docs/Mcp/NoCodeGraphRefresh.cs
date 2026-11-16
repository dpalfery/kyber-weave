using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Mcp;

/// <summary>The freshness gate of a caller that asked no tool-availability question: it never refreshes and never blocks.</summary>
internal sealed class NoCodeGraphRefresh : ICodeGraphFreshnessGate
{
    public static NoCodeGraphRefresh Instance { get; } = new();

    /// <inheritdoc />
    public CodeGraphGate Evaluate(Func<ICodeGraphResolver> resolverFactory)
    {
        ArgumentNullException.ThrowIfNull(resolverFactory);
        return CodeGraphGate.Clear;
    }
}
