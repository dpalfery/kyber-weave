using KyberWeave.Core.FileSystem;
using System.Reflection;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Manages deployment lifecycles, configuration redirection, and filesystem link synchronization
///     for concurrent harness instances.
/// </summary>
/// <remarks>
///     Instances allow running isolated agent harness workflows side-by-side using distinct model presets.
///     For <see cref="SquadInstanceKind.HomeReplace" /> targets, settings and history are linked safely
///     from the canonical harness home without overwriting user state.
/// </remarks>
public sealed partial class SquadInstanceService : ISquadInstanceService
{
    private const string ReceiptFileName = "squad.receipt.json";
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex PresetNameRegex();

    private readonly ISquadGlobalRootResolver _globalRootResolver;
    private readonly ISquadLifecycleService _lifecycleService;
    private readonly ISquadLinkFileSystem _linkFileSystem;
    private readonly ISquadModelPresetStore _presetStore;
    private readonly ISquadStateStore _stateStore;
    private readonly ISquadInstanceLinkSync _linkSync;
    private readonly ISquadFileAccess _files;
    private readonly IPathResolver _paths;
    private readonly IProcessEnvironment _environment;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SquadInstanceService" /> class.
    /// </summary>
    /// <param name="lifecycleService">Lifecycle service for installing and updating deployments.</param>
    /// <param name="presetStore">Store for resolving model presets and their digests.</param>
    /// <param name="linkFileSystem">Filesystem abstraction for symbolic link operations.</param>
    /// <param name="globalRootResolver">Resolver for canonical harness global homes.</param>
    /// <param name="linkSync">Synchronizes the instance links with the canonical home.</param>
    /// <param name="files">File probes for the instance directories.</param>
    /// <param name="paths">Resolves the instance root for the containment check.</param>
    /// <param name="environment">Names the working directory a relative home is resolved against.</param>
    /// <param name="stateStore">Optional state store for reading receipts and recording instance metadata.</param>
    public SquadInstanceService(
        ISquadLifecycleService lifecycleService,
        ISquadModelPresetStore presetStore,
        ISquadLinkFileSystem linkFileSystem,
        ISquadGlobalRootResolver globalRootResolver,
        ISquadInstanceLinkSync linkSync,
        ISquadFileAccess files,
        IPathResolver paths,
        IProcessEnvironment environment,
        ISquadStateStore? stateStore = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycleService);
        ArgumentNullException.ThrowIfNull(presetStore);
        ArgumentNullException.ThrowIfNull(linkFileSystem);
        ArgumentNullException.ThrowIfNull(globalRootResolver);
        ArgumentNullException.ThrowIfNull(linkSync);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);

        _lifecycleService = lifecycleService;
        _presetStore = presetStore;
        _linkFileSystem = linkFileSystem;
        _globalRootResolver = globalRootResolver;
        _linkSync = linkSync;
        _files = files;
        _paths = paths;
        _environment = environment;
        _stateStore = stateStore ?? lifecycleService.StateStore;
    }

    /// <summary>
    ///     Ensures that a harness instance for the specified target and preset exists, is up-to-date,
    ///     and has its filesystem links synchronized.
    /// </summary>
    /// <param name="target">The harness target.</param>
    /// <param name="presetName">The model preset name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the instance root directory and any synchronization warnings.</returns>
    public async Task<SquadInstanceEnsureResult> EnsureAsync(
        SquadTarget target,
        string presetName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetName);
        if (!PresetNameRegex().IsMatch(presetName))
            throw new ArgumentException($"Invalid preset name '{presetName}'. Preset names must match '^[a-z0-9][a-z0-9-]{{0,63}}$'.", nameof(presetName));

        ISquadStateStore stateStore = _stateStore;
        string targetToken = SquadTargetCatalog.GetToken(target);
        string home = stateStore.UserPaths.HomeDirectory;

        string instanceRoot = Path.Combine(UserStatePaths.SquadInstancesDirectory(home), targetToken, presetName);
        string instanceStateDir =
            Path.Combine(UserStatePaths.SquadInstanceStateDirectory(home), targetToken, presetName);

        VerifyPathContainment(home, targetToken, instanceRoot);
        string receiptPath = Path.Combine(instanceStateDir, ReceiptFileName);

        IReadOnlyList<string>? lifecycleWarnings;
        if (!_files.FileExists(receiptPath))
        {
            lifecycleWarnings = await InstallInstanceAsync(
                instanceRoot,
                instanceStateDir,
                target,
                presetName,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            lifecycleWarnings = await UpdateInstanceAsync(
                instanceRoot,
                target,
                presetName,
                stateStore,
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> syncWarnings = SynchronizeInstanceLinks(
            target,
            instanceRoot,
            instanceStateDir,
            stateStore);

        List<string> allWarnings = new();
        if (lifecycleWarnings is { Count: > 0 }) allWarnings.AddRange(lifecycleWarnings);

        if (syncWarnings is { Count: > 0 }) allWarnings.AddRange(syncWarnings);

        return new SquadInstanceEnsureResult(instanceRoot, allWarnings);
    }

    private async Task<IReadOnlyList<string>?> InstallInstanceAsync(
        string instanceRoot,
        string instanceStateDir,
        SquadTarget target,
        string presetName,
        CancellationToken cancellationToken)
    {
        _files.CreateDirectory(instanceRoot);
        _files.CreateDirectory(instanceStateDir);

        SquadInstallRequest installRequest = new(
            instanceRoot,
            SquadDeploymentScope.Instance,
            [target],
            ModelPresets: new Dictionary<SquadTarget, string> { [target] = presetName });

        SquadLifecycleResult installResult = await _lifecycleService.InstallAsync(installRequest, cancellationToken)
            .ConfigureAwait(false);

        if (!installResult.Success)
        {
            string errorMessage = installResult.Errors is { Count: > 0 }
                ? string.Join("; ", installResult.Errors)
                : "Instance installation failed.";
            throw new InvalidOperationException(errorMessage);
        }

        return installResult.Warnings;
    }

    private async Task<IReadOnlyList<string>?> UpdateInstanceAsync(
        string instanceRoot,
        SquadTarget target,
        string presetName,
        ISquadStateStore stateStore,
        CancellationToken cancellationToken)
    {
        SquadLock? lockFile = stateStore.ReadLock(instanceRoot, SquadDeploymentScope.Instance);
        SquadModelPreset? currentPreset = _presetStore.TryLoad(presetName);
        string currentCliVersion = GetAssemblyVersion();

        bool versionMismatch = lockFile is null ||
                               !string.Equals(lockFile.CliVersion, currentCliVersion, StringComparison.Ordinal);

        bool presetMismatch = false;
        if (lockFile is not null)
        {
            if (currentPreset is not null)
            {
                if (lockFile.ModelPresets is null ||
                    !lockFile.ModelPresets.TryGetValue(target, out SquadAppliedPreset? applied) ||
                    !string.Equals(applied.Digest, currentPreset.Digest, StringComparison.Ordinal))
                    presetMismatch = true;
            }
            else if (lockFile.ModelPresets is not null && lockFile.ModelPresets.ContainsKey(target))
            {
                presetMismatch = true;
            }
        }

        if (!versionMismatch && !presetMismatch)
        {
            return null;
        }

        SquadUpdateRequest updateRequest = new(
            instanceRoot,
            SquadDeploymentScope.Instance,
            [target],
            ModelPresets: new Dictionary<SquadTarget, string> { [target] = presetName });

        SquadLifecycleResult updateResult = await _lifecycleService
            .UpdateAsync(updateRequest, cancellationToken)
            .ConfigureAwait(false);

        if (!updateResult.Success)
        {
            string errorMessage = updateResult.Errors is { Count: > 0 }
                ? string.Join("; ", updateResult.Errors)
                : "Instance update failed.";
            throw new InvalidOperationException(errorMessage);
        }

        return updateResult.Warnings;
    }

    private IReadOnlyList<string> SynchronizeInstanceLinks(
        SquadTarget target,
        string instanceRoot,
        string instanceStateDir,
        ISquadStateStore stateStore)
    {
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(target);
        if (row.Kind != SquadInstanceKind.HomeReplace)
        {
            return [];
        }

        string mainHome = _globalRootResolver.ResolveGlobalRoot(target);
        SquadReceipt? receipt = stateStore.ReadReceipt(instanceRoot, SquadDeploymentScope.Instance);

        HashSet<string> renderedPaths = receipt is not null
            ? receipt.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        SquadInstanceLinkSyncResult syncResult = _linkSync.Sync(
            instanceRoot,
            mainHome,
            row,
            renderedPaths,
            _linkFileSystem,
            instanceStateDir);

        return syncResult.Warnings;
    }

    /// <summary>
    ///     Removes an instance deployment and its recorded state without modifying main harness files.
    /// </summary>
    /// <param name="target">The harness target.</param>
    /// <param name="presetName">The model preset name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task RemoveAsync(
        SquadTarget target,
        string presetName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetName);
        cancellationToken.ThrowIfCancellationRequested();
        if (!PresetNameRegex().IsMatch(presetName))
            throw new ArgumentException($"Invalid preset name '{presetName}'. Preset names must match '^[a-z0-9][a-z0-9-]{{0,63}}$'.", nameof(presetName));

        ISquadStateStore stateStore = _stateStore;
        string targetToken = SquadTargetCatalog.GetToken(target);
        string home = stateStore.UserPaths.HomeDirectory;

        string instanceRoot = Path.Combine(UserStatePaths.SquadInstancesDirectory(home), targetToken, presetName);
        string instanceStateDir =
            Path.Combine(UserStatePaths.SquadInstanceStateDirectory(home), targetToken, presetName);

        VerifyPathContainment(home, targetToken, instanceRoot);

        if (_files.DirectoryExists(instanceRoot) || _linkFileSystem.IsLink(instanceRoot))
            _linkFileSystem.DeleteTreeNoFollow(instanceRoot);

        if (_files.DirectoryExists(instanceStateDir)) _linkFileSystem.DeleteTreeNoFollow(instanceStateDir);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Discovers all deployed instances that have receipts under the instance state directory
    ///     and ensures each discovered instance is up to date and linked.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        ISquadStateStore stateStore = _stateStore;
        string home = stateStore.UserPaths.HomeDirectory;
        string instanceStateBase = UserStatePaths.SquadInstanceStateDirectory(home);

        if (!_files.DirectoryExists(instanceStateBase)) return;

        foreach (SquadDirectoryEntry targetDir in _files.EnumerateRealDirectories(instanceStateBase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            SquadTarget? matchedTarget = null;
            foreach (SquadTarget candidate in SquadTargetCatalog.All)
                if (string.Equals(SquadTargetCatalog.GetToken(candidate), targetDir.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matchedTarget = candidate;
                    break;
                }

            if (matchedTarget is null) continue;

            foreach (SquadDirectoryEntry presetDir in _files.EnumerateRealDirectories(targetDir.FullPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string receiptFile = Path.Combine(presetDir.FullPath, ReceiptFileName);
                if (_files.FileExists(receiptFile))
                    await EnsureAsync(matchedTarget.Value, presetDir.Name, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void VerifyPathContainment(string home, string targetToken, string instanceRoot)
    {
        string expectedBase = _paths.GetFullPath(
            Path.Combine(UserStatePaths.SquadInstancesDirectory(home), targetToken),
            _environment.CurrentDirectory);
        string fullInstanceRoot = _paths.GetFullPath(instanceRoot, _environment.CurrentDirectory);
        string prefix = expectedBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullInstanceRoot.StartsWith(prefix, StringComparison.Ordinal) || fullInstanceRoot.Length <= prefix.Length)
            throw new ArgumentException($"Instance root '{fullInstanceRoot}' is not contained under '{expectedBase}'.", nameof(instanceRoot));
    }

    private static string GetAssemblyVersion()
    {
        Assembly assembly = typeof(SquadInstanceService).Assembly;
        string? infoVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            int plusIdx = infoVersion.IndexOf('+', StringComparison.Ordinal);
            return plusIdx > 0 ? infoVersion[..plusIdx] : infoVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "0.1.0";
    }
}
