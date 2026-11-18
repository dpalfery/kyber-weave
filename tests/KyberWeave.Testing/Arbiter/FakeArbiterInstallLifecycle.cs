using System.Collections.ObjectModel;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Tests.Arbiter;

/// <summary>One install the fake lifecycle answered, in the order the command asked for it.</summary>
/// <param name="Root">The deployment root the command named.</param>
/// <param name="Scope">Project or global.</param>
/// <param name="Target">The harness installed.</param>
/// <param name="ExistingReceipt">The receipt the store already held for the root, or null.</param>
/// <param name="DryRun">Whether the run planned without writing.</param>
/// <param name="Wiring">The Arbiter settings carried into the render.</param>
public sealed record ArbiterInstallCall(
    string Root,
    SquadDeploymentScope Scope,
    SquadTarget Target,
    SquadReceipt? ExistingReceipt,
    bool DryRun,
    SquadArbiterWiring Wiring);

/// <summary>
/// The one fake for <see cref="IArbiterInstallLifecycle"/>: it records each install, writes a deployed hook file and a
/// receipt that keeps every block the root already held, over the in-memory file system and state the test supplies.
/// </summary>
/// <param name="stateStore">The store the receipt is recorded in, so a later run reads it back.</param>
/// <param name="globalRoots">Resolves the harness root a global install writes beneath.</param>
/// <param name="files">The file system the deployed file is written to.</param>
public sealed class FakeArbiterInstallLifecycle(
    FakeSquadStateStore stateStore,
    ISquadGlobalRootResolver globalRoots,
    IWritableFileSystem files) : IArbiterInstallLifecycle
{
    /// <summary>Gets the installs asked for, in call order.</summary>
    public Collection<ArbiterInstallCall> Calls { get; } = [];

    /// <summary>Gets or sets the failure an install raises instead of answering; null answers normally.</summary>
    public Exception? Failure { get; set; }

    /// <inheritdoc />
    public Task<SquadLifecycleResult> InstallAsync(
        string root,
        SquadDeploymentScope scope,
        SquadTarget target,
        SquadReceipt? existingReceipt,
        bool dryRun,
        SquadArbiterWiring wiring,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        Calls.Add(new ArbiterInstallCall(root, scope, target, existingReceipt, dryRun, wiring));

        if (Failure is { } failure)
        {
            throw failure;
        }

        if (dryRun)
        {
            return Task.FromResult(new SquadLifecycleResult(true, DryRun: true));
        }

        string token = SquadTargetCatalog.GetToken(target);
        string deployedRoot = scope == SquadDeploymentScope.Global
            ? globalRoots.ResolveGlobalRoot(target)
            : root;
        files.CreateDirectory(deployedRoot);
        files.WriteAllText(Path.Combine(deployedRoot, "hooks.json"), $"{{\"target\":\"{token}\"}}");

        // A real install keeps every harness the root already recorded, so the receipt accumulates.
        List<SquadOwnedFile> owned = [.. existingReceipt?.Files ?? []];
        owned.RemoveAll(file => string.Equals(file.Target, token, StringComparison.Ordinal));
        owned.Add(new SquadOwnedFile("hooks.json", "sha256-placeholder", token, Adopted: false));

        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            scope,
            root,
            DateTimeOffset.UnixEpoch,
            [],
            owned);
        stateStore.Add(receipt);

        return Task.FromResult(new SquadLifecycleResult(true, Receipt: receipt));
    }
}
