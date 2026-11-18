using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>Builds <see cref="HookGitFacts"/> over the ports one hook evaluation reads git, the run records and the snapshot documents through.</summary>
public sealed class HookGitFactsFactory(
    IGitRepositoryFacts git,
    IPlanFileResolver planPaths,
    IHookSnapshotStore snapshots,
    IHookFileWindowReader windows,
    IRunCheckout checkouts,
    IRunStateStore runStates,
    IProcessEnvironment process,
    IPathResolver paths,
    TimeProvider clock) : IHookGitFactsFactory
{
    /// <inheritdoc/>
    public IArbiterGitFacts Create(string workingDirectory, string? homeDirectory = null, string? arbiterDirectory = null) =>
        new HookGitFacts(git, planPaths, snapshots, windows, checkouts, runStates, process, paths, clock, workingDirectory, homeDirectory, arbiterDirectory);
}
