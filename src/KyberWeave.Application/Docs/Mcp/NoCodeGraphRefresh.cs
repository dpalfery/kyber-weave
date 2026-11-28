using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Mcp;

/// <summary>The freshness gate of a caller that asked no tool-availability question: it never refreshes and never blocks.</summary>
internal sealed class NoCodeGraphRefresh : ICodeGraphFreshnessGate
{
    // A constant verdict, not synchronous work dressed as async: the null gate has nothing to run.
    private static readonly Task<CodeGraphGate> ClearResult = Task.FromResult(CodeGraphGate.Clear);

    public static NoCodeGraphRefresh Instance { get; } = new();

    /// <inheritdoc />
    public Task<CodeGraphGate> EvaluateAsync(
        Func<ICodeGraphResolver> resolverFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolverFactory);
        return ClearResult;
    }
}
