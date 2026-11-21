using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>The worker guard's host configuration seam when a test wants no project configuration: every load fails.</summary>
internal sealed class NoConfig : IHostConfigLoader
{
    public HostConfigLoadResult TryLoad(string repoRoot) => new(false, null, null, "no project configuration");
}

/// <summary>The worker guard's Arbiter state seam when a test reaches no store: every open throws.</summary>
internal sealed class NoState : IArbiterStateFactory
{
    public IArbiterLedger OpenLedger(string arbiterDirectory) => throw new NotSupportedException();

    public IArbiterDecisionLog OpenDecisionLog(string arbiterDirectory) => throw new NotSupportedException();

    public IArbiterRunStore OpenRunStore(string arbiterDirectory, ArbiterStoreOptions? options = null, TimeProvider? clock = null) =>
        throw new NotSupportedException();
}

/// <summary>
/// The worker guard's subagent budget seam when a test keeps no budgets: no budget file exists, so no budget is spent,
/// and every write throws.
/// </summary>
internal sealed class NoBudgets : IWorkerBudgetStore
{
    public void EnsureDirectory(string home) => throw new NotSupportedException();

    public bool Exists(string home, string key) => false;

    public string[] ReadLines(string home, string key) => [];

    public void Delete(string home, string key) => throw new NotSupportedException();

    public void TryCreate(string home, string key, string content) => throw new NotSupportedException();

    public void Append(string home, string key, string content) => throw new NotSupportedException();

    public void Sweep(string home, DateTimeOffset now, TimeSpan retention) => throw new NotSupportedException();
}

/// <summary>
/// The worker guard's Factory session-ledger seam when a test has no ledger: no child is ever recorded, so no call
/// is guarded, and every write throws.
/// </summary>
internal sealed class NoSessions : IWorkerSessionStore
{
    public void EnsureDirectory(string home) => throw new NotSupportedException();

    public bool Exists(string home, string key) => false;

    public WorkerSessionEntry? Read(string home, string key) => null;

    public WorkerSessionEntry? AwaitType(string home, string key, TimeSpan timeout) => null;

    public void AppendParent(string home, string key, string parentSessionId) => throw new NotSupportedException();

    public void AppendGuess(string home, string key, string agentType) => throw new NotSupportedException();

    public void AppendType(string home, string key, string agentType) => throw new NotSupportedException();

    public void Delete(string home, string key) => throw new NotSupportedException();

    public void DeletePending(string home, string key) => throw new NotSupportedException();

    public string[] ReadPending(string home, string key) => [];

    public void AppendPending(string home, string key, string entry) => throw new NotSupportedException();

    public void Sweep(string home, DateTimeOffset now, TimeSpan retention) => throw new NotSupportedException();
}

/// <summary>The worker guard's run checkout seam when a test has no live run: no canonical checkout is found and no write is refused.</summary>
internal sealed class NoCheckouts : IRunCheckout
{
    public string? CanonicalCheckoutOf(string commonDirectory) => null;

    public IReadOnlyList<RunRecord>? LiveSeparateRuns(string homeDirectory, string canonicalCheckout) => [];

    public string? CanonicalCheckoutContaining(string path) => null;

    public string? StrayWriteRefusal(string path, string workingDirectory, string homeDirectory) => null;

    public string? ReadCheckedOutBranch(string worktree) => null;
}
