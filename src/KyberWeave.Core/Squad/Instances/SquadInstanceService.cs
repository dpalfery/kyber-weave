using System.Reflection;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     The result of ensuring a harness instance is installed and synchronized.
/// </summary>
/// <param name="InstanceRoot">The absolute filesystem path of the instance root directory.</param>
/// <param name="Warnings">Any warnings generated during link synchronization or lifecycle operations.</param>
public sealed record SquadInstanceEnsureResult(
    string InstanceRoot,
    IReadOnlyList<string> Warnings);

/// <summary>
///     Manages deployment lifecycles, configuration redirection, and filesystem link synchronization
///     for concurrent harness instances.
/// </summary>
/// <remarks>
///     Instances allow running isolated agent harness workflows side-by-side using distinct model presets.
///     For <see cref="SquadInstanceKind.HomeReplace" /> targets, settings and history are linked safely
///     from the canonical harness home without overwriting user state.
/// </remarks>
public sealed class SquadInstanceService
{
    private const string ReceiptFileName = "squad.receipt.json";
    private readonly ISquadGlobalRootResolver _globalRootResolver;

    private readonly SquadLifecycleService _lifecycleService;
    private readonly ISquadLinkFileSystem _linkFileSystem;
    private readonly SquadModelPresetStore _presetStore;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SquadInstanceService" /> class.
    /// </summary>
    /// <param name="lifecycleService">Lifecycle service for installing and updating deployments.</param>
    /// <param name="presetStore">Store for resolving model presets and their digests.</param>
    /// <param name="linkFileSystem">Filesystem abstraction for symbolic link operations.</param>
    /// <param name="globalRootResolver">Resolver for canonical harness global homes.</param>
    public SquadInstanceService(
        SquadLifecycleService lifecycleService,
        SquadModelPresetStore presetStore,
        ISquadLinkFileSystem linkFileSystem,
        ISquadGlobalRootResolver globalRootResolver)
    {
        ArgumentNullException.ThrowIfNull(lifecycleService);
        ArgumentNullException.ThrowIfNull(presetStore);
        ArgumentNullException.ThrowIfNull(linkFileSystem);
        ArgumentNullException.ThrowIfNull(globalRootResolver);

        _lifecycleService = lifecycleService;
        _presetStore = presetStore;
        _linkFileSystem = linkFileSystem;
        _globalRootResolver = globalRootResolver;
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

        SquadStateStore stateStore = ResolveStateStore();
        string targetToken = SquadTargetCatalog.GetToken(target);
        string home = stateStore.UserPaths.HomeDirectory;

        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", targetToken, presetName);
        string instanceStateDir =
            Path.Combine(home, ".kyber-weave", "squad", "instance-state", targetToken, presetName);
        string receiptPath = Path.Combine(instanceStateDir, ReceiptFileName);

        IReadOnlyList<string>? lifecycleWarnings = null;

        if (!File.Exists(receiptPath))
        {
            Directory.CreateDirectory(instanceRoot);
            Directory.CreateDirectory(instanceStateDir);

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

            lifecycleWarnings = installResult.Warnings;
        }
        else
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

            if (versionMismatch || presetMismatch)
            {
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

                lifecycleWarnings = updateResult.Warnings;
            }
        }

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(target);
        IReadOnlyList<string> syncWarnings = [];

        if (row.Kind == SquadInstanceKind.HomeReplace)
        {
            string mainHome = _globalRootResolver.ResolveGlobalRoot(target);
            SquadReceipt? receipt = stateStore.ReadReceipt(instanceRoot, SquadDeploymentScope.Instance);

            HashSet<string> renderedPaths = receipt is not null
                ? receipt.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            SquadInstanceLinkSyncResult syncResult = SquadInstanceLinkSync.Sync(
                instanceRoot,
                mainHome,
                row,
                renderedPaths,
                _linkFileSystem,
                instanceStateDir);

            syncWarnings = syncResult.Warnings;
        }

        List<string> allWarnings = new();
        if (lifecycleWarnings is { Count: > 0 }) allWarnings.AddRange(lifecycleWarnings);

        if (syncWarnings is { Count: > 0 }) allWarnings.AddRange(syncWarnings);

        return new SquadInstanceEnsureResult(instanceRoot, allWarnings);
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

        SquadStateStore stateStore = ResolveStateStore();
        string targetToken = SquadTargetCatalog.GetToken(target);
        string home = stateStore.UserPaths.HomeDirectory;

        string instanceRoot = Path.Combine(home, ".kyber-weave", "squad", "instances", targetToken, presetName);
        string instanceStateDir =
            Path.Combine(home, ".kyber-weave", "squad", "instance-state", targetToken, presetName);

        if (Directory.Exists(instanceRoot) || _linkFileSystem.IsLink(instanceRoot))
            _linkFileSystem.DeleteTreeNoFollow(instanceRoot);

        if (Directory.Exists(instanceStateDir)) Directory.Delete(instanceStateDir, true);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Discovers all deployed instances that have receipts under the instance state directory
    ///     and ensures each discovered instance is up to date and linked.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        SquadStateStore stateStore = ResolveStateStore();
        string home = stateStore.UserPaths.HomeDirectory;
        string instanceStateBase = Path.Combine(home, ".kyber-weave", "squad", "instance-state");

        if (!Directory.Exists(instanceStateBase)) return;

        DirectoryInfo baseDir = new(instanceStateBase);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        foreach (FileSystemInfo targetEntry in baseDir.EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (targetEntry is not DirectoryInfo targetDir || targetEntry.LinkTarget is not null) continue;

            SquadTarget? matchedTarget = null;
            foreach (SquadTarget candidate in SquadTargetCatalog.All)
                if (string.Equals(SquadTargetCatalog.GetToken(candidate), targetDir.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matchedTarget = candidate;
                    break;
                }

            if (matchedTarget is null) continue;

            foreach (FileSystemInfo presetEntry in targetDir.EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (presetEntry is not DirectoryInfo presetDir || presetEntry.LinkTarget is not null) continue;

                string receiptFile = Path.Combine(presetDir.FullName, ReceiptFileName);
                if (File.Exists(receiptFile))
                    await EnsureAsync(matchedTarget.Value, presetDir.Name, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private SquadStateStore ResolveStateStore()
    {
        PropertyInfo? property = typeof(SquadLifecycleService).GetProperty(
            "StateStore",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(_lifecycleService) is SquadStateStore storeFromProperty) return storeFromProperty;

        FieldInfo? field = typeof(SquadLifecycleService).GetField(
            "_stateStore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(_lifecycleService) is SquadStateStore storeFromField) return storeFromField;

        throw new InvalidOperationException("Could not resolve SquadStateStore from SquadLifecycleService.");
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
