using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Mcp;

/// <summary>The freshness gate of a caller that asked no tool-availability question: it never refreshes and never blocks.</summary>
internal sealed class NoCodeGraphRefresh : ICodeGraphFreshnessGate
{
    private static readonly Task<CodeGraphGate> ClearResult = CompletedClear();

    public static NoCodeGraphRefresh Instance { get; } = new();

    /// <inheritdoc />
    public Task<CodeGraphGate> EvaluateAsync(
        Func<ICodeGraphResolver> resolverFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolverFactory);
        return ClearResult;
    }

    private static Task<CodeGraphGate> CompletedClear()
    {
        TaskCompletionSource<CodeGraphGate> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(CodeGraphGate.Clear);
        return source.Task;
    }
}
