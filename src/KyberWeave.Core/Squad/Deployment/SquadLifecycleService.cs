using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Orchestrates install, update, and uninstall lifecycle operations for Kyber-Squad.
/// </summary>
public sealed class SquadLifecycleService
{
    private readonly ISquadGlobalRootResolver? _globalRoots;
    private readonly ISquadTransactionObserver? _observer;
    private readonly SquadModelPresetStore _presetStore;

    private readonly ISquadReleaseSource _releaseSource;
    private readonly ISquadRenderer _renderer;
    private readonly SquadModelPresetSelectionStore _selectionStore;
    private readonly SquadStateStore _stateStore;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    ///     Gets the state store associated with this lifecycle service.
    /// </summary>
    public SquadStateStore StateStore => _stateStore;

    public SquadLifecycleService(
        ISquadReleaseSource releaseSource,
        ISquadRenderer renderer,
        SquadStateStore stateStore,
        TimeProvider? timeProvider = null,
        ISquadTransactionObserver? observer = null,
        ISquadGlobalRootResolver? globalRoots = null,
        SquadModelPresetStore? presetStore = null,
        SquadModelPresetSelectionStore? selectionStore = null)
    {
        ArgumentNullException.ThrowIfNull(releaseSource);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(stateStore);
        _releaseSource = releaseSource;
        _renderer = renderer;
        _stateStore = stateStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observer = observer;
        _globalRoots = globalRoots;
        _presetStore = presetStore ?? new SquadModelPresetStore(stateStore.UserPaths);
        _selectionStore = selectionStore ?? new SquadModelPresetSelectionStore(stateStore.UserPaths);
    }

    /// <summary>
    ///     Performs an installation of Kyber-Squad into the target root.
    /// </summary>
    public async Task<SquadLifecycleResult> InstallAsync(
        SquadInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetRoot);

        if (request.Targets is null || request.Targets.Count == 0)
            throw new ArgumentException("At least one deployment target must be specified.", nameof(request));

        if (request.Scope == SquadDeploymentScope.Instance && request.Targets.Count != 1)
            throw new ArgumentException("SquadDeploymentScope.Instance requires exactly one target.", nameof(request));

        RequireRenderableTargets(request.Targets);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(request.TargetRoot);
        string targetRoot = identity.PhysicalPath;

        SquadReceipt? existingReceipt = _stateStore.ReadReceipt(targetRoot, request.Scope);
        if (existingReceipt is not null)
        {
            HashSet<string> existingTargets = existingReceipt.Files
                .Select(f => f.Target)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> requestedTargets = request.Targets
                .Select(SquadTargetCatalog.GetToken)
                .ToHashSet(StringComparer.Ordinal);

            if (!existingTargets.SetEquals(requestedTargets))
                throw new SquadDeploymentConflictException(
                    $"Kyber-Squad is already installed at '{targetRoot}' with targets ({string.Join(", ", existingTargets)}). " +
                    $"Requested targets ({string.Join(", ", requestedTargets)}) do not match. Use 'update' to modify targets.");

            // Same-target install routes through CreateUpdate below; refuse a legacy
            // single-root receipt here, before any network round trip, rather than letting
            // CreateUpdate's own refusal fire only after the release has already downloaded.
            // Only meaningful once a per-target resolver is in play (see CreateUpdate's own
            // guard): with none, legacy and modern resolution already agree.
            if (_globalRoots is not null && SquadDeploymentPlan.IsLegacySingleRootReceipt(existingReceipt))
                throw SquadDeploymentPlan.LegacySingleRootLayoutConflict();
        }

        SquadLock? previousLock = _stateStore.ReadLock(targetRoot, request.Scope);
        IReadOnlyDictionary<SquadTarget, string>? requestPresets = request.ModelPresets;
        if (request.Scope == SquadDeploymentScope.Project &&
            requestPresets is null &&
            (previousLock?.ModelPresets is null || previousLock.ModelPresets.Count == 0))
        {
            SquadModelPresetSelection? projectSelection = _selectionStore.Read(request.TargetRoot);
            if (projectSelection?.ModelPresets is { Count: > 0 }) requestPresets = projectSelection.ModelPresets;
        }

        Dictionary<SquadTarget, string> targetPresetNames =
            ResolveTargetPresetNames(requestPresets, previousLock);
        Dictionary<SquadTarget, SquadModelPreset> loadedPresets =
            LoadAndVerifyPresets(targetPresetNames, request.Targets);

        string version = request.Version ?? ResolveDefaultVersion();
        string tempExtractDir = Path.Combine(
            Path.GetTempPath(),
            "kyber-squad-extract-" + Guid.NewGuid().ToString("N"));

        try
        {
            SquadReleaseResult releaseResult = await _releaseSource.DownloadAndExtractAsync(
                new SquadReleaseRequest(request.Repository, version, tempExtractDir),
                cancellationToken).ConfigureAwait(false);

            List<SquadModelDiffItem>? modelChanges = null;
            SquadSource? source = TryResolveSquadSource(releaseResult.ExtractionRoot);
            if (source is not null)
            {
                foreach (KeyValuePair<SquadTarget, SquadModelPreset> kvp in loadedPresets)
                    SquadModelPresetValidator.Validate(kvp.Value, source.ModelProfiles);

                if (request.DryRun) modelChanges = ComputeModelChanges(source, request.Targets, loadedPresets);
            }
            else if (loadedPresets.Count > 0)
            {
                throw new InvalidOperationException(
                    "Cannot validate model presets because Squad source could not be resolved.");
            }

            string? userScopeDir = request.Scope is SquadDeploymentScope.Global or SquadDeploymentScope.Instance
                ? _stateStore.UserPaths.ApplicationDataDirectory
                : null;

            SquadDeploymentScope renderScope = request.Scope == SquadDeploymentScope.Instance
                ? SquadDeploymentScope.Global
                : request.Scope;

            ISquadGlobalRootResolver? effectiveGlobalRoots = _globalRoots;
            if (request.Scope == SquadDeploymentScope.Instance && request.Targets.Count == 1)
                effectiveGlobalRoots = new SquadInstanceRootResolver(targetRoot, request.Targets[0]);

            SquadRenderRequest renderRequest = new(
                releaseResult.ExtractionRoot,
                request.Targets,
                renderScope,
                userScopeDir,
                request.Translation,
                effectiveGlobalRoots,
                loadedPresets.Count > 0 ? loadedPresets : null);

            SquadRenderResult renderResult =
                await _renderer.RenderAsync(renderRequest, cancellationToken).ConfigureAwait(false);
            if (!renderResult.Success || renderResult.Errors.Count > 0)
            {
                string error = renderResult.Errors.Count > 0
                    ? string.Join(Environment.NewLine, renderResult.Errors)
                    : "Squad render failed.";
                throw new SquadRenderValidationException(error);
            }

            SquadLock squadLock = BuildLock(
                releaseResult.Version,
                request.Bundle,
                request.Targets,
                request.Exclusions ?? Array.Empty<string>(),
                request.Translation,
                releaseResult.Checksum.Sha256,
                releaseResult.ExtractionRoot,
                loadedPresets.Count > 0 ? loadedPresets : null);

            IReadOnlyList<SquadDegradation> degradations = renderResult.Degradations
                .Select(d => new SquadDegradation(d.Target, d.CanonicalIdentity, d.Code))
                .ToArray();

            ISquadGlobalRootResolver? planGlobalRoots = request.Scope == SquadDeploymentScope.Instance
                ? effectiveGlobalRoots
                : _globalRoots;

            SquadDeploymentPlan plan;
            if (existingReceipt is null)
                plan = SquadDeploymentPlan.CreateInstall(
                    targetRoot,
                    request.Scope,
                    squadLock,
                    renderResult.Files,
                    degradations,
                    request.Adopt,
                    _timeProvider,
                    planGlobalRoots,
                    []);
            else
                plan = SquadDeploymentPlan.CreateUpdate(
                    targetRoot,
                    request.Scope,
                    squadLock,
                    renderResult.Files,
                    existingReceipt,
                    degradations,
                    false,
                    _timeProvider,
                    planGlobalRoots,
                    []);

            if (request.DryRun)
                return new SquadLifecycleResult(
                    true,
                    plan,
                    plan.Receipt,
                    plan.Lock,
                    plan.Receipt.Degradations,
                    DryRun: true,
                    ModelChanges: modelChanges,
                    Warnings: RenderWarningMessages(renderResult.Warnings));

            SquadTransaction transaction = new(_stateStore, _observer);
            transaction.Execute(plan);

            List<string> warnings = [.. RenderWarningMessages(renderResult.Warnings), .. transaction.Warnings];
            WriteCanonicalSnapshot(releaseResult.Version, source, warnings);

            return new SquadLifecycleResult(
                true,
                plan,
                plan.Receipt,
                plan.Lock,
                plan.Receipt.Degradations,
                DryRun: false,
                Warnings: warnings);
        }
        finally
        {
            CleanupTempDirectory(tempExtractDir);
        }
    }

    /// <summary>
    ///     Carries the renderer's warnings onto the lifecycle result, prefixed with their code, so the CLI
    ///     shows them. Without this a render warning, such as the operator channel's missing-server notice,
    ///     reaches no operator.
    /// </summary>
    private static List<string> RenderWarningMessages(IReadOnlyList<SquadRenderWarning> warnings)
    {
        // Only the operator channel's warnings reach the CLI. A default install prints exactly what it printed
        // before. The one exception is agent-body-too-long, which reaches the CLI only when the operator channel
        // overlay was inlined, because then the overlay is what pushed the conductor over the cap.
        bool overlayApplied = warnings.Any(warning => warning.Code == SquadOperatorChannel.OverlayAppliedCode);
        return warnings
            .Where(warning => SquadOperatorChannel.IsOperatorChannelWarning(warning.Code)
                              || (overlayApplied && warning.Code == CopilotRenderer.AgentBodyTooLongCode))
            .Select(warning => $"{warning.Code}: {warning.Message}")
            .ToList();
    }

    /// <summary>
    ///     Performs an update of an existing Kyber-Squad deployment in the target root.
    /// </summary>
    public async Task<SquadLifecycleResult> UpdateAsync(
        SquadUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetRoot);

        if (request.Scope == SquadDeploymentScope.Instance && (request.Targets is null || request.Targets.Count != 1))
            throw new ArgumentException("SquadDeploymentScope.Instance requires exactly one target.", nameof(request));

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(request.TargetRoot);
        string targetRoot = identity.PhysicalPath;

        SquadReceipt previousReceipt = _stateStore.ReadReceipt(targetRoot, request.Scope)
                                       ?? throw new SquadDeploymentConflictException(
                                           $"No Kyber-Squad deployment found at '{targetRoot}'. Run 'squad install' first.");

        // Refused before any network round trip (real or dry-run alike, since the download
        // happens unconditionally below) rather than letting CreateUpdate's own refusal fire
        // only after the release has already downloaded. Only meaningful once a per-target
        // resolver is in play (see CreateUpdate's own guard): with none, legacy and modern
        // resolution already agree.
        if (_globalRoots is not null && SquadDeploymentPlan.IsLegacySingleRootReceipt(previousReceipt))
            throw SquadDeploymentPlan.LegacySingleRootLayoutConflict();

        IReadOnlyList<SquadTarget> targets;
        if (request.Targets is { Count: > 0 })
        {
            targets = request.Targets;
        }
        else
        {
            HashSet<string> targetTokens = previousReceipt.Files
                .Select(f => f.Target)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            targets = SquadTargetCatalog.Parse(targetTokens);
            if (targets.Count == 0)
                throw new SquadDeploymentConflictException("No targets found in previous deployment receipt.");
        }

        RequireRenderableTargets(targets);

        SquadLock? previousLock = _stateStore.ReadLock(targetRoot, request.Scope);
        Dictionary<SquadTarget, string> targetPresetNames =
            ResolveTargetPresetNames(request.ModelPresets, previousLock);
        Dictionary<SquadTarget, SquadModelPreset> loadedPresets =
            LoadAndVerifyPresets(targetPresetNames, targets);

        string version = request.Version ?? ResolveDefaultVersion();
        string tempExtractDir = Path.Combine(
            Path.GetTempPath(),
            "kyber-squad-extract-" + Guid.NewGuid().ToString("N"));

        try
        {
            SquadReleaseResult releaseResult = await _releaseSource.DownloadAndExtractAsync(
                new SquadReleaseRequest(request.Repository, version, tempExtractDir),
                cancellationToken).ConfigureAwait(false);

            List<SquadModelDiffItem>? modelChanges = null;
            SquadSource? source = TryResolveSquadSource(releaseResult.ExtractionRoot);
            if (source is not null)
            {
                foreach (KeyValuePair<SquadTarget, SquadModelPreset> kvp in loadedPresets)
                    SquadModelPresetValidator.Validate(kvp.Value, source.ModelProfiles);

                if (request.DryRun) modelChanges = ComputeModelChanges(source, targets, loadedPresets);
            }
            else if (loadedPresets.Count > 0)
            {
                throw new InvalidOperationException(
                    "Cannot validate model presets because Squad source could not be resolved.");
            }

            string? userScopeDir = request.Scope is SquadDeploymentScope.Global or SquadDeploymentScope.Instance
                ? _stateStore.UserPaths.ApplicationDataDirectory
                : null;

            SquadDeploymentScope renderScope = request.Scope == SquadDeploymentScope.Instance
                ? SquadDeploymentScope.Global
                : request.Scope;

            ISquadGlobalRootResolver? effectiveGlobalRoots = _globalRoots;
            if (request.Scope == SquadDeploymentScope.Instance && targets.Count == 1)
                effectiveGlobalRoots = new SquadInstanceRootResolver(targetRoot, targets[0]);

            SquadRenderRequest renderRequest = new(
                releaseResult.ExtractionRoot,
                targets,
                renderScope,
                userScopeDir,
                request.Translation,
                effectiveGlobalRoots,
                loadedPresets.Count > 0 ? loadedPresets : null);

            SquadRenderResult renderResult =
                await _renderer.RenderAsync(renderRequest, cancellationToken).ConfigureAwait(false);
            if (!renderResult.Success || renderResult.Errors.Count > 0)
            {
                string error = renderResult.Errors.Count > 0
                    ? string.Join(Environment.NewLine, renderResult.Errors)
                    : "Squad render failed.";
                throw new SquadRenderValidationException(error);
            }

            SquadLock squadLock = BuildLock(
                releaseResult.Version,
                request.Bundle,
                targets,
                request.Exclusions ?? Array.Empty<string>(),
                request.Translation,
                releaseResult.Checksum.Sha256,
                releaseResult.ExtractionRoot,
                loadedPresets.Count > 0 ? loadedPresets : null);

            IReadOnlyList<SquadDegradation> degradations = renderResult.Degradations
                .Select(d => new SquadDegradation(d.Target, d.CanonicalIdentity, d.Code))
                .ToArray();

            ISquadGlobalRootResolver? planGlobalRoots = request.Scope == SquadDeploymentScope.Instance
                ? effectiveGlobalRoots
                : _globalRoots;

            // Written before the plan so the digest the receipt records is one this run has already
            // read back from the deployed path; recording it any earlier would claim ownership of a
            // file the run never wrote, and every later run would read the operator's untouched file
            // as an edit. The cost of the ordering is the mirror case: a deployment that fails after
            // this write leaves the guidance updated and the run failing loudly, which the next run
            // repairs by adopting bytes that already equal the shipped default.
            List<string> warnings = [.. RenderWarningMessages(renderResult.Warnings)];
            SquadResponseGuidanceDeployer? guidanceDeployer =
                request is { Scope: SquadDeploymentScope.Global, DryRun: false }
                    ? new SquadResponseGuidanceDeployer(_stateStore.ResolveHomeOwnedFilesRoot())
                    : null;
            SquadHomeFile? guidance = guidanceDeployer?.Deploy(previousReceipt.HomeFiles, warnings);

            SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
                targetRoot,
                request.Scope,
                squadLock,
                renderResult.Files,
                previousReceipt,
                degradations,
                request.ReplaceManaged,
                _timeProvider,
                planGlobalRoots,
                [],
                guidance is null ? null : ReplaceHomeFile(previousReceipt.HomeFiles, guidance));

            if (request.DryRun)
                return new SquadLifecycleResult(
                    true,
                    plan,
                    plan.Receipt,
                    plan.Lock,
                    plan.Receipt.Degradations,
                    DryRun: true,
                    ModelChanges: modelChanges,
                    Warnings: RenderWarningMessages(renderResult.Warnings));

            SquadTransaction transaction = new(_stateStore, _observer);
            transaction.Execute(plan);

            warnings.AddRange(transaction.Warnings);
            WriteCanonicalSnapshot(releaseResult.Version, source, warnings);

            return new SquadLifecycleResult(
                true,
                plan,
                plan.Receipt,
                plan.Lock,
                plan.Receipt.Degradations,
                DryRun: false,
                Warnings: warnings);
        }
        finally
        {
            CleanupTempDirectory(tempExtractDir);
        }
    }

    /// <summary>
    ///     Performs an uninstallation of Kyber-Squad from the target root.
    /// </summary>
    public Task<SquadLifecycleResult> UninstallAsync(
        SquadUninstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetRoot);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(request.TargetRoot);
        string targetRoot = identity.PhysicalPath;

        SquadReceipt? receipt = _stateStore.ReadReceipt(targetRoot, request.Scope);
        if (receipt is null)
            return Task.FromResult(new SquadLifecycleResult(
                true,
                null,
                null,
                null,
                Array.Empty<SquadDegradation>(),
                DryRun: request.DryRun));

        ISquadGlobalRootResolver? planGlobalRoots = _globalRoots;
        if (planGlobalRoots is null && request.Scope == SquadDeploymentScope.Instance)
        {
            HashSet<string> targetTokens = receipt.Files
                .Select(f => f.Target)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<SquadTarget> parsedTargets = SquadTargetCatalog.Parse(targetTokens);
            if (parsedTargets.Count == 1) planGlobalRoots = new SquadInstanceRootResolver(targetRoot, parsedTargets[0]);
        }

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUninstall(
            targetRoot,
            request.Scope,
            receipt,
            planGlobalRoots,
            []);

        if (request.DryRun)
            return Task.FromResult(new SquadLifecycleResult(
                true,
                plan,
                plan.Receipt,
                plan.Lock,
                plan.Receipt.Degradations,
                DryRun: true));

        SquadTransaction transaction = new(_stateStore, _observer);
        transaction.Execute(plan);

        return Task.FromResult(new SquadLifecycleResult(
            true,
            plan,
            plan.ReceiptMutation == SquadStateMutation.Write ? plan.Receipt : null,
            plan.LockMutation == SquadStateMutation.Write ? plan.Lock : null,
            plan.Receipt.Degradations,
            DryRun: false,
            Warnings: transaction.Warnings));
    }

    /// <summary>
    ///     Resolves target-to-preset name mapping from request presets and previous lock state.
    /// </summary>
    private static Dictionary<SquadTarget, string> ResolveTargetPresetNames(
        IReadOnlyDictionary<SquadTarget, string>? requestPresets,
        SquadLock? previousLock)
    {
        Dictionary<SquadTarget, string> targetPresetNames = new();

        if (requestPresets == null)
        {
            if (previousLock?.ModelPresets is not null)
                foreach (KeyValuePair<SquadTarget, SquadAppliedPreset> kvp in previousLock.ModelPresets)
                    targetPresetNames[kvp.Key] = kvp.Value.Name;
        }
        else if (requestPresets.Count == 0)
        {
            targetPresetNames.Clear();
        }
        else
        {
            if (previousLock?.ModelPresets is not null)
                foreach (KeyValuePair<SquadTarget, SquadAppliedPreset> kvp in previousLock.ModelPresets)
                    targetPresetNames[kvp.Key] = kvp.Value.Name;

            foreach (KeyValuePair<SquadTarget, string> kvp in requestPresets) targetPresetNames[kvp.Key] = kvp.Value;
        }

        return targetPresetNames;
    }

    /// <summary>
    ///     Loads and verifies preset presence before any release archive is downloaded.
    /// </summary>
    private Dictionary<SquadTarget, SquadModelPreset> LoadAndVerifyPresets(
        Dictionary<SquadTarget, string> targetPresetNames,
        IReadOnlyList<SquadTarget> targets)
    {
        Dictionary<SquadTarget, SquadModelPreset> loaded = new();
        foreach (SquadTarget target in targets)
            if (targetPresetNames.TryGetValue(target, out string? presetName) && !string.IsNullOrWhiteSpace(presetName))
            {
                SquadModelPreset? preset = null;
                try
                {
                    preset = _presetStore.TryLoad(presetName);
                }
                catch (ArgumentException)
                {
                    // Invalid name regex treated as missing/invalid
                }

                if (preset is null)
                {
                    string targetToken = SquadTargetCatalog.GetToken(target);
                    string presetPath = Path.Combine(
                        _stateStore.UserPaths.HomeDirectory,
                        ".kyber-weave",
                        "squad",
                        "presets",
                        $"{presetName}.yml");
                    throw new SquadDeploymentConflictException(
                        $"Model preset '{presetName}' for target '{targetToken}' was not found at '{presetPath}'. " +
                        $"To clear this preset, run: kyber-weave squad preset clear --target {targetToken}");
                }

                loaded[target] = preset;
            }

        return loaded;
    }

    /// <summary>
    ///     Resolves canonical Squad source for preset validation and model diffing, falling back
    ///     to local checkout canonical source when the extraction directory is minimal (such as in tests).
    /// </summary>
    private static SquadSource? TryResolveSquadSource(string extractionRoot)
    {
        string squadPath = Path.Combine(extractionRoot, "squad.yml");
        if (File.Exists(squadPath))
        {
            string text = File.ReadAllText(squadPath);
            if (text.Contains("name:", StringComparison.Ordinal)) return SquadSourceLoader.Load(extractionRoot);
        }

        try
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "products", "kyber-squad");
                if (File.Exists(Path.Combine(candidate, "squad.yml"))) return SquadSourceLoader.Load(candidate);

                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore filesystem errors when searching parent directories
        }

        return null;
    }

    /// <summary>
    ///     Returns the home-owned files to record, with <paramref name="replacement" /> taking the place
    ///     of any record for the same path. A null replacement means this run changed nothing, so the
    ///     previous records stand as they are.
    /// </summary>
    /// <remarks>
    ///     Matched on the relative path rather than appended, because the receipt's validation requires
    ///     one record per home path: a second entry for the guidance would be an ownership claim the
    ///     store rejects on the next read.
    /// </remarks>
    private static IReadOnlyList<SquadHomeFile> ReplaceHomeFile(
        IReadOnlyList<SquadHomeFile> previous,
        SquadHomeFile? replacement)
    {
        if (replacement is null)
            return previous;

        return
        [
            .. previous.Where(file =>
                !string.Equals(file.RelativePath, replacement.RelativePath, StringComparison.Ordinal)),
            replacement
        ];
    }

    /// <summary>
    ///     Persists the canonical profile snapshot for a committed deployment, keyed by release version.
    /// </summary>
    /// <remarks>
    ///     A snapshot failure never fails the deployment: the transaction is already durable, and the
    ///     next update rebuilds the snapshot, so the warning names that repair. This mirrors the
    ///     project-registry write-failure pattern in <c>SquadTransaction.RecordProjectRegistration</c>.
    ///     A null source means there is nothing to snapshot (possible only when no checkout backs the
    ///     extraction root and no presets forced resolution); the preset verbs then fail closed with
    ///     the update remedy, the same migration path pre-snapshot deployments take. Dry-run callers
    ///     never reach here: they return before the transaction executes.
    /// </remarks>
    private void WriteCanonicalSnapshot(string version, SquadSource? source, List<string> warnings)
    {
        if (source is null)
            return;

        try
        {
            Dictionary<string, string> agentProfiles = new(StringComparer.Ordinal);
            foreach (SquadAgent agent in source.Agents)
                agentProfiles[agent.Name] = agent.ModelProfile;

            new SquadCanonicalProfileStore(_stateStore.UserPaths).Write(
                new SquadCanonicalProfiles(version, source.ModelProfiles, agentProfiles));
        }
        catch (Exception exception) when (exception is IOException or
                                              UnauthorizedAccessException or
                                              InvalidDataException or
                                              ArgumentException)
        {
            warnings.Add(
                $"The Squad canonical profile snapshot for version '{version}' was not written " +
                $"({exception.Message}). The deployment itself is committed. " +
                "Rerun `squad update` to rebuild the snapshot.");
        }
    }

    /// <summary>
    ///     Computes model diff items across affected targets for a dry-run invocation.
    /// </summary>
    private static List<SquadModelDiffItem> ComputeModelChanges(
        SquadSource source,
        IReadOnlyList<SquadTarget> targets,
        IReadOnlyDictionary<SquadTarget, SquadModelPreset> loadedPresets)
    {
        List<SquadModelDiffItem> changes = new();
        foreach (SquadTarget target in targets)
            if (loadedPresets.TryGetValue(target, out SquadModelPreset? preset))
            {
                SquadModelProfiles after = preset.ApplyTo(source.ModelProfiles);
                IReadOnlyList<SquadModelDiffItem> diffs =
                    SquadModelDiff.Compute(source, target, source.ModelProfiles, after);
                changes.AddRange(diffs);
            }

        changes.Sort((a, b) => StringComparer.Ordinal.Compare(a.Agent, b.Agent));
        return changes;
    }

    /// <summary>
    ///     Rejects any requested target with no registered renderer before the release is
    ///     downloaded — a target with a renderer already existing per-target on
    ///     <see cref="_renderer" /> is a cheap property read, so there is no reason to spend a
    ///     network round trip on a request that is going to fail regardless.
    /// </summary>
    private void RequireRenderableTargets(IReadOnlyList<SquadTarget> targets)
    {
        IReadOnlyList<SquadTarget> unsupported = targets
            .Where(target => !_renderer.SupportedTargets.Contains(target))
            .ToArray();
        if (unsupported.Count == 0) return;

        string names = string.Join(", ", unsupported.Select(SquadTargetCatalog.GetToken).Order(StringComparer.Ordinal));
        string supported = string.Join(
            ", ",
            _renderer.SupportedTargets.Select(SquadTargetCatalog.GetToken).Order(StringComparer.Ordinal));
        throw new SquadRenderValidationException(
            $"No renderer is implemented yet for target(s): {names}. " +
            $"See docs/todo/<target>.md for what is needed to add support. " +
            $"Targets available today: {supported}.");
    }

    private static SquadLock BuildLock(
        string version,
        string bundle,
        IReadOnlyList<SquadTarget> targets,
        IReadOnlyList<string> exclusions,
        string translation,
        string assetDigest,
        string extractionRoot,
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? modelPresets = null)
    {
        string cliVersion = GetAssemblyVersion();
        string bundleDigest = ComputeBundleDigest(extractionRoot, bundle);
        SquadApmIdentity apmIdentity = ReadApmIdentity(extractionRoot);

        Dictionary<SquadTarget, SquadAppliedPreset>? appliedPresets = null;
        if (modelPresets is { Count: > 0 })
            foreach (KeyValuePair<SquadTarget, SquadModelPreset> kvp in modelPresets)
                if (targets.Contains(kvp.Key))
                {
                    appliedPresets ??= new Dictionary<SquadTarget, SquadAppliedPreset>();
                    appliedPresets[kvp.Key] = new SquadAppliedPreset(kvp.Value.Name, kvp.Value.Digest);
                }

        string schema = appliedPresets is { Count: > 0 } ? SquadStateStore.LockSchemaV2 : SquadStateStore.LockSchemaV1;

        return new SquadLock(
            schema,
            version,
            cliVersion,
            cliVersion,
            bundle,
            targets.Select(SquadTargetCatalog.GetToken).ToArray(),
            exclusions.ToArray(),
            translation,
            bundleDigest,
            assetDigest,
            apmIdentity,
            appliedPresets);
    }

    private static string GetAssemblyVersion()
    {
        Assembly assembly = typeof(SquadLifecycleService).Assembly;
        string? infoVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            int plusIdx = infoVersion.IndexOf('+', StringComparison.Ordinal);
            return plusIdx > 0 ? infoVersion[..plusIdx] : infoVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "0.1.0";
    }

    private static string ResolveDefaultVersion()
    {
        return GetAssemblyVersion();
    }

    private static string ComputeBundleDigest(string extractionRoot, string bundle)
    {
        string bundlePath = Path.Combine(extractionRoot, "bundles", $"{bundle}.yml");
        if (File.Exists(bundlePath)) return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(bundlePath)));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bundle)));
    }

    /// <summary>
    ///     Reads the pinned upstream-toolchain identity recorded in a Squad release's
    ///     <c>toolchain.yml</c>, if any.
    /// </summary>
    /// <remarks>
    ///     Vestigial since rendering stopped shelling out to an external toolchain: canonical
    ///     <c>toolchain.yml</c> no longer declares a <c>validated-release</c>, so this reads
    ///     "unverified" on every install today. The lock field and this reader stay rather
    ///     than forcing a lock schema bump (and the golden-file churn across
    ///     <c>SquadDeploymentStateTests</c> that implies) for a field that was already
    ///     optional and whose absence this code already handled correctly.
    /// </remarks>
    private static SquadApmIdentity ReadApmIdentity(string extractionRoot)
    {
        string toolchainPath = Path.Combine(extractionRoot, "toolchain.yml");
        if (!File.Exists(toolchainPath)) return SquadApmIdentity.None;

        try
        {
            string yamlText = File.ReadAllText(toolchainPath);
            YamlStream yaml = new();
            yaml.Load(new StringReader(yamlText));
            if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode root)
                if (root.Children.TryGetValue(new YamlScalarNode("validated-release"), out YamlNode? valRelNode))
                {
                    if (valRelNode is YamlMappingNode valRel)
                    {
                        string apmVersion = string.Empty;
                        string apmTagCommit = string.Empty;
                        string apmDigest = string.Empty;

                        if (valRel.Children.TryGetValue(new YamlScalarNode("version"), out YamlNode? vNode) &&
                            vNode is YamlScalarNode vScalar && !string.IsNullOrWhiteSpace(vScalar.Value))
                            apmVersion = vScalar.Value;

                        if (valRel.Children.TryGetValue(new YamlScalarNode("tag-commit"), out YamlNode? tcNode) &&
                            tcNode is YamlScalarNode tcScalar && !string.IsNullOrWhiteSpace(tcScalar.Value))
                            apmTagCommit = tcScalar.Value;

                        if (valRel.Children.TryGetValue(new YamlScalarNode("asset-sha256"), out YamlNode? shaNode) &&
                            shaNode is YamlScalarNode shaScalar && !string.IsNullOrWhiteSpace(shaScalar.Value))
                            apmDigest = shaScalar.Value;

                        if (string.IsNullOrWhiteSpace(apmVersion) ||
                            string.IsNullOrWhiteSpace(apmTagCommit) ||
                            apmDigest.Length != 64 ||
                            !apmDigest.All(char.IsAsciiHexDigit))
                            throw new SquadRenderValidationException(
                                $"'{toolchainPath}' declares an invalid APM validated-release. A 64-character hex asset-sha256, version, and tag-commit are required.");

#pragma warning disable CA1308
                        // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
                        return new SquadApmIdentity(apmVersion, apmTagCommit, apmDigest.ToLowerInvariant());
#pragma warning restore CA1308
                    }

                    if (valRelNode is not YamlScalarNode scalar || (scalar.Value is not null &&
                                                                    scalar.Value != "null" && scalar.Value != "~" &&
                                                                    scalar.Value.Length > 0))
                        throw new SquadRenderValidationException(
                            $"'{toolchainPath}' declares an invalid APM validated-release shape. A mapping with version, tag-commit, and asset-sha256 (or null) is required.");
                }
        }
        catch (SquadRenderValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SquadRenderValidationException(
                $"Failed to parse toolchain file at '{toolchainPath}': {ex.Message}", ex);
        }

        return SquadApmIdentity.None;
    }

    private static void CleanupTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // Suppress cleanup failure in temporary directory
        }
    }
}
