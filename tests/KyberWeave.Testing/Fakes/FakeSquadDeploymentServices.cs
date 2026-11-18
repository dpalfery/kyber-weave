using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Tests.Fakes;

/// <summary>The Squad lifecycle in a command test: it records each request and answers the result the test scripted.</summary>
public sealed class FakeSquadLifecycleService(ISquadStateStore stateStore) : ISquadLifecycleService
{
    private readonly List<SquadInstallRequest> _installs = [];
    private readonly List<SquadUpdateRequest> _updates = [];
    private readonly List<SquadUninstallRequest> _uninstalls = [];

    /// <summary>Gets the targets <see cref="EffectiveInstallTargets"/> answers; null echoes the request's own.</summary>
    public IReadOnlyList<SquadTarget>? EffectiveTargets { get; set; }

    /// <summary>Gets or sets the result every lifecycle call answers.</summary>
    public SquadLifecycleResult Result { get; set; } = new(true);

    /// <summary>Gets the install requests received, in order.</summary>
    public IReadOnlyList<SquadInstallRequest> Installs => _installs;

    /// <summary>Gets the update requests received, in order.</summary>
    public IReadOnlyList<SquadUpdateRequest> Updates => _updates;

    /// <summary>Gets the uninstall requests received, in order.</summary>
    public IReadOnlyList<SquadUninstallRequest> Uninstalls => _uninstalls;

    /// <inheritdoc />
    public ISquadStateStore StateStore { get; } = stateStore;

    /// <inheritdoc />
    public IReadOnlyList<SquadTarget> EffectiveInstallTargets(SquadInstallRequest request) =>
        EffectiveTargets ?? request.Targets;

    /// <inheritdoc />
    public Task<SquadLifecycleResult> InstallAsync(SquadInstallRequest request, CancellationToken cancellationToken = default)
    {
        _installs.Add(request);
        return Task.FromResult(Result);
    }

    /// <inheritdoc />
    public Task<SquadLifecycleResult> UpdateAsync(SquadUpdateRequest request, CancellationToken cancellationToken = default)
    {
        _updates.Add(request);
        return Task.FromResult(Result);
    }

    /// <inheritdoc />
    public Task<SquadLifecycleResult> UninstallAsync(SquadUninstallRequest request, CancellationToken cancellationToken = default)
    {
        _uninstalls.Add(request);
        return Task.FromResult(Result);
    }
}

/// <summary>The Squad state store in a command test: user paths and receipts the test arranged; everything else is refused.</summary>
public sealed class FakeSquadStateStore(ISquadUserPaths userPaths) : ISquadStateStore
{
    private readonly Dictionary<(string Root, SquadDeploymentScope Scope), SquadReceipt> _receipts = [];

    /// <inheritdoc />
    public ISquadUserPaths UserPaths { get; } = userPaths;

    /// <summary>Arranges the receipt <see cref="ReadReceipt"/> answers for a root and scope.</summary>
    /// <param name="receipt">The receipt, answered for its own target root and scope.</param>
    public void Add(SquadReceipt receipt) => _receipts[(receipt.TargetRoot, receipt.Scope)] = receipt;

    /// <inheritdoc />
    public SquadReceipt? ReadReceipt(string targetRoot, SquadDeploymentScope scope) =>
        _receipts.GetValueOrDefault((targetRoot, scope));

    /// <inheritdoc />
    public string ResolveStateDirectory(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveHomeStateDirectory() => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveHomeOwnedFilesRoot() => throw new NotSupportedException();

    /// <inheritdoc />
    public IReadOnlyList<SquadProjectRegistryEntry> ListProjects() => throw new NotSupportedException();

    /// <inheritdoc />
    public SquadProjectRegistryEntry? FindProject(string root) => throw new NotSupportedException();

    /// <inheritdoc />
    public IReadOnlyList<SquadProjectRegistryEntry> ListProjectsWithoutRebuild() => throw new NotSupportedException();

    /// <inheritdoc />
    public void RegisterProject(string root, IReadOnlyList<string> targets, DateTimeOffset now) => throw new NotSupportedException();

    /// <inheritdoc />
    public bool UnregisterProject(string root) => throw new NotSupportedException();

    /// <inheritdoc />
    public string SerializeLock(SquadLock squadLock) => throw new NotSupportedException();

    /// <inheritdoc />
    public SquadLock DeserializeLock(string yaml) => throw new NotSupportedException();

    /// <inheritdoc />
    public string SerializeReceipt(SquadReceipt receipt) => throw new NotSupportedException();

    /// <inheritdoc />
    public SquadReceipt DeserializeReceipt(string json) => throw new NotSupportedException();

    /// <inheritdoc />
    public SquadLock? ReadLock(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveLockPath(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveReceiptPath(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveTransactionDirectory(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveTransactionWorkDirectory(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveFileWorkDirectory(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveStateAuthorityRoot(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveLeaseIdentity(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveHomeRoot() => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveHomeCreationBoundary() => throw new NotSupportedException();
}

/// <summary>The legacy-state migration in a command test: it records the working directory it was asked about and reports nothing to move.</summary>
public sealed class FakeSquadStateMigration : ISquadStateMigration
{
    private readonly List<string?> _invokingDirectories = [];

    /// <summary>Gets the working directories <see cref="MigrateGlobal"/> was given, in order.</summary>
    public IReadOnlyList<string?> InvokingDirectories => _invokingDirectories;

    /// <summary>Gets the report every call answers.</summary>
    public SquadMigrationReport Report { get; set; } = new([], [], [], []);

    /// <inheritdoc />
    public SquadMigrationReport MigrateGlobal(string? invokingCwd, bool dryRun = false)
    {
        _invokingDirectories.Add(invokingCwd);
        return Report;
    }

    /// <inheritdoc />
    public SquadMigrationReport ImportProject(string root, bool dryRun = false) => Report;
}

/// <summary>The target resolver in a command test: it records the request and answers the decision the test scripted.</summary>
public sealed class FakeSquadTargetResolver : ISquadTargetResolver
{
    private readonly List<SquadTargetResolutionRequest> _requests = [];

    /// <summary>Gets or sets the decision every request answers.</summary>
    public SquadTargetResolutionDecision Decision { get; set; } = new()
    {
        Kind = SquadTargetResolutionKind.Resolved,
        Targets = [SquadTarget.Claude],
        Source = SquadTargetResolutionSource.Explicit
    };

    /// <summary>Gets the requests received, in order.</summary>
    public IReadOnlyList<SquadTargetResolutionRequest> Requests => _requests;

    /// <inheritdoc />
    public SquadTargetResolutionDecision Resolve(SquadTargetResolutionRequest request)
    {
        _requests.Add(request);
        return Decision;
    }
}

/// <summary>The global-root resolver in a command test: every target roots beneath one directory.</summary>
public sealed class FakeSquadGlobalRootResolver(string home) : ISquadGlobalRootResolver
{
    /// <inheritdoc />
    public string ResolveGlobalRoot(SquadTarget target) => home + "/." + SquadTargetCatalog.GetToken(target);

    /// <inheritdoc />
    public string? HomeDirectory => home;
}
