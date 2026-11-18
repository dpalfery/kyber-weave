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
    private readonly Dictionary<(string Root, SquadDeploymentScope Scope), SquadLock> _locks = [];
    private readonly Dictionary<string, SquadLock> _lockTexts = [];
    private readonly List<SquadProjectRegistryEntry> _projects = [];
    private readonly Dictionary<(string Root, SquadDeploymentScope Scope), string> _stateDirectories = [];

    /// <inheritdoc />
    public ISquadUserPaths UserPaths { get; } = userPaths;

    /// <summary>Registers a project <see cref="ListProjects"/> and <see cref="FindProject"/> answer from.</summary>
    /// <param name="project">The registry entry.</param>
    public void AddProject(SquadProjectRegistryEntry project) => _projects.Add(project);

    /// <summary>Gets or sets a failure <see cref="ListProjects"/> raises instead of answering, or null for none.</summary>
    public Exception? ListProjectsFailure { get; set; }

    /// <summary>Arranges the lock <see cref="ReadLock"/> answers for a root and scope.</summary>
    /// <param name="root">The target root.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="squadLock">The lock to answer.</param>
    public void AddLock(string root, SquadDeploymentScope scope, SquadLock squadLock) => _locks[(root, scope)] = squadLock;

    /// <summary>Arranges the lock <see cref="DeserializeLock"/> answers for a lock file's exact text.</summary>
    /// <param name="yaml">The text of the lock file.</param>
    /// <param name="squadLock">The lock the text parses to.</param>
    public void AddLockText(string yaml, SquadLock squadLock) => _lockTexts[yaml] = squadLock;

    /// <summary>Arranges the receipt <see cref="ReadReceipt"/> answers for a root and scope.</summary>
    /// <param name="receipt">The receipt, answered for its own target root and scope.</param>
    /// <param name="readAt">The root the receipt is read at when that differs from its recorded target root, as a migrated legacy receipt does; null for the recorded root.</param>
    public void Add(SquadReceipt receipt, string? readAt = null) => _receipts[(readAt ?? receipt.TargetRoot, receipt.Scope)] = receipt;

    /// <inheritdoc />
    public SquadReceipt? ReadReceipt(string targetRoot, SquadDeploymentScope scope) =>
        _receipts.GetValueOrDefault((targetRoot, scope));

    /// <summary>Arranges the state directory <see cref="ResolveStateDirectory"/> answers for a root and scope.</summary>
    /// <param name="root">The target root.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="directory">The state directory the store reports.</param>
    public void AddStateDirectory(string root, SquadDeploymentScope scope, string directory) =>
        _stateDirectories[(root, scope)] = directory;

    /// <inheritdoc />
    public string ResolveStateDirectory(string targetRoot, SquadDeploymentScope scope) =>
        _stateDirectories.TryGetValue((targetRoot, scope), out string? directory)
            ? directory
            : throw new NotSupportedException($"No state directory is arranged for '{targetRoot}' ({scope}).");

    /// <inheritdoc />
    public string ResolveHomeStateDirectory() => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveHomeOwnedFilesRoot() => throw new NotSupportedException();

    /// <inheritdoc />
    public IReadOnlyList<SquadProjectRegistryEntry> ListProjects() =>
        ListProjectsFailure is null ? _projects : throw ListProjectsFailure;

    /// <inheritdoc />
    public SquadProjectRegistryEntry? FindProject(string root) =>
        _projects.Find(project => string.Equals(project.Path, root, StringComparison.Ordinal));

    /// <inheritdoc />
    public bool IsRegistered(string root) => FindProject(root) is not null;

    /// <inheritdoc />
    public IReadOnlyList<SquadProjectRegistryEntry> ListProjectsWithoutRebuild() => throw new NotSupportedException();

    /// <inheritdoc />
    public void RegisterProject(string root, IReadOnlyList<string> targets, DateTimeOffset now) => throw new NotSupportedException();

    /// <inheritdoc />
    public bool UnregisterProject(string root) => throw new NotSupportedException();

    /// <inheritdoc />
    public string SerializeLock(SquadLock squadLock) => throw new NotSupportedException();

    /// <inheritdoc />
    public SquadLock DeserializeLock(string yaml) =>
        _lockTexts.TryGetValue(yaml, out SquadLock? squadLock) ? squadLock : throw new InvalidDataException("unreadable lock");

    /// <inheritdoc />
    public string SerializeReceipt(SquadReceipt receipt) => throw new NotSupportedException();

    /// <inheritdoc />
    public SquadReceipt DeserializeReceipt(string json) => throw new NotSupportedException();

    /// <inheritdoc />
    public SquadLock? ReadLock(string targetRoot, SquadDeploymentScope scope) =>
        _locks.GetValueOrDefault((targetRoot, scope));

    /// <inheritdoc />
    public string ResolveLockPath(string targetRoot, SquadDeploymentScope scope) => throw new NotSupportedException();

    /// <inheritdoc />
    public string ResolveReceiptPath(string targetRoot, SquadDeploymentScope scope) =>
        Path.Combine(ResolveStateDirectory(targetRoot, scope), "receipt.json");

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
