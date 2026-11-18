using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Validation;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Applies a model preset profile to target harnesses, updating deployed files and recording project selections.
/// </summary>
internal sealed class SquadPresetUseCommand : Command<SquadPresetUseSettings>
{
    private const string LockSchemaV2 = "kyber-squad.lock/v2";

    private readonly ISquadStateStore _stateStore;
    private readonly ISquadGlobalRootResolver _globalRoots;
    private readonly ISquadModelPresetStore _presetStore;
    private readonly ISquadModelPresetSelectionStore _selectionStore;
    private readonly ISquadCanonicalProfileResolver _canonicalProfiles;
    private readonly ISquadDeploymentPlanner _planner;
    private readonly ISquadTransactionFactory _transactions;
    private readonly IFileSystemPathSemantics _pathSemantics;
    private readonly SquadCommandRoots _roots;
    private readonly IFileSystem _files;
    private readonly SquadConsole _output;
    private readonly TimeProvider _time;
    private readonly ISquadModelPresetValidator _validator;

    /// <summary>Creates the command over the Squad services the host registers.</summary>
    /// <param name="stateStore">Reads the deployment state.</param>
    /// <param name="globalRoots">Resolves each target's global root.</param>
    /// <param name="presetStore">Loads the named presets.</param>
    /// <param name="selectionStore">Records the project's preset selection.</param>
    /// <param name="canonicalProfiles">Finds the canonical model profiles to re-render with.</param>
    /// <param name="planner">Preflights the re-render.</param>
    /// <param name="transactions">Applies the re-render.</param>
    /// <param name="pathSemantics">The filesystem's containment and canonical spelling rules.</param>
    /// <param name="roots">Resolves the target root and anchors a global run.</param>
    /// <param name="files">Reads the deployed files the rewrite starts from.</param>
    /// <param name="output">Where the diff rows and the refusals are written.</param>
    /// <param name="time">Stamps the deployment plan.</param>
    /// <param name="validator">Checks the preset against the canonical model profiles.</param>
    public SquadPresetUseCommand(
        ISquadStateStore stateStore,
        ISquadGlobalRootResolver globalRoots,
        ISquadModelPresetStore presetStore,
        ISquadModelPresetSelectionStore selectionStore,
        ISquadCanonicalProfileResolver canonicalProfiles,
        ISquadDeploymentPlanner planner,
        ISquadTransactionFactory transactions,
        IFileSystemPathSemantics pathSemantics,
        SquadCommandRoots roots,
        IFileSystem files,
        SquadConsole output,
        TimeProvider time,
        ISquadModelPresetValidator validator)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(globalRoots);
        ArgumentNullException.ThrowIfNull(presetStore);
        ArgumentNullException.ThrowIfNull(selectionStore);
        ArgumentNullException.ThrowIfNull(canonicalProfiles);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(pathSemantics);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(validator);
        _stateStore = stateStore;
        _globalRoots = globalRoots;
        _presetStore = presetStore;
        _selectionStore = selectionStore;
        _canonicalProfiles = canonicalProfiles;
        _planner = planner;
        _transactions = transactions;
        _pathSemantics = pathSemantics;
        _roots = roots;
        _files = files;
        _output = output;
        _time = time;
        _validator = validator;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadPresetUseSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ISquadStateStore stateStore = _stateStore;
        ISquadModelPresetStore presetStore = _presetStore;
        IReadOnlyList<ISquadModelPresetSelectionStore> selectionStores = [_selectionStore];

        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);
        string? effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        string projectRoot = _roots.ResolveTargetRoot(effectivePath);

        SquadModelPreset? preset = null;
        try
        {
            preset = presetStore.TryLoad(settings.Name);
        }
        catch (ArgumentException)
        {
            // invalid regex format, handled below
        }

        if (preset is null)
        {
            IReadOnlyList<string> existing = presetStore.List();
            string available = existing.Count > 0
                ? string.Join(", ", existing)
                : "none";
            _output.WriteClientInputError(
                $"Unknown preset '{settings.Name}'. Available presets: {available}.");
            return 2;
        }

        IReadOnlyList<SquadTarget> targets;
        try
        {
            if (settings.Targets.Length > 0)
            {
                targets = SquadTargetCatalog.Parse(settings.Targets);
            }
            else
            {
                string checkRoot = scope == SquadDeploymentScope.Global
                    ? _roots.ResolveGlobalAnchorRoot(stateStore, effectivePath, false)
                    : projectRoot;
                SquadReceipt? receipt = stateStore.ReadReceipt(checkRoot, scope);
                if (receipt is not null && receipt.Files.Count > 0)
                {
                    targets = SquadTargetCatalog.Parse(receipt.Files.Select(file => file.Target).Distinct());
                }
                else
                {
                    _output.WriteClientInputError(
                        "No deployment target specified. Specify target(s) using --target <target>.");
                    return 2;
                }
            }
        }
        catch (ArgumentException ex)
        {
            _output.WriteClientInputError(ex.Message);
            return 2;
        }

        if (scope == SquadDeploymentScope.Global)
        {
            string globalAnchor =
                _roots.ResolveGlobalAnchorRoot(stateStore, effectivePath, settings.DryRun);
            SquadReceipt? globalReceipt = stateStore.ReadReceipt(globalAnchor, SquadDeploymentScope.Global);
            SquadLock? globalLock = stateStore.ReadLock(globalAnchor, SquadDeploymentScope.Global);

            HashSet<string> deployedTokens = new(StringComparer.OrdinalIgnoreCase);
            if (globalReceipt is not null)
            {
                foreach (SquadOwnedFile file in globalReceipt.Files)
                {
                    deployedTokens.Add(file.Target);
                }
            }

            if (globalLock is not null)
            {
                foreach (string t in globalLock.Targets)
                {
                    deployedTokens.Add(t);
                }
            }

            foreach (SquadTarget target in targets)
            {
                string token = SquadTargetCatalog.GetToken(target);
                if (!deployedTokens.Contains(token))
                {
                    _output.WriteClientInputError(
                        $"{token} is not deployed globally; run `kyber-weave squad install --global --target {token}` first.");
                    return 2;
                }
            }

            if (globalReceipt is null)
            {
                _output.WriteClientInputError(
                    "Global deployment receipt is missing or unreadable. Run `kyber-weave squad install --global` to repair the deployment.");
                return 1;
            }

            return ApplyPresetToDeployment(
                targets,
                preset,
                SquadDeploymentScope.Global,
                globalAnchor,
                projectRoot,
                globalReceipt,
                globalLock,
                stateStore,
                selectionStores,
                settings);
        }

        SquadReceipt? projectReceipt = stateStore.ReadReceipt(projectRoot, SquadDeploymentScope.Project);
        SquadLock? projectLock = stateStore.ReadLock(projectRoot, SquadDeploymentScope.Project);

        HashSet<string> projectDeployedTokens = new(StringComparer.OrdinalIgnoreCase);
        if (projectReceipt is not null)
        {
            foreach (SquadOwnedFile file in projectReceipt.Files)
            {
                projectDeployedTokens.Add(file.Target);
            }
        }

        if (projectLock is not null)
        {
            foreach (string t in projectLock.Targets)
            {
                projectDeployedTokens.Add(t);
            }
        }

        bool anyDeployed = targets.Any(t => projectDeployedTokens.Contains(SquadTargetCatalog.GetToken(t)));

        if (!anyDeployed)
        {
            if (!settings.DryRun)
            {
                foreach (ISquadModelPresetSelectionStore store in selectionStores)
                {
                    foreach (SquadTarget target in targets)
                    {
                        store.Record(projectRoot, target, preset.Name);
                    }
                }
            }

            foreach (SquadTarget target in targets)
            {
                string token = SquadTargetCatalog.GetToken(target);
                _output.WriteLine(
                    $"Recorded `{preset.Name}` for `{token}` in this project; it applies on install or `squad run`.");
            }

            return 0;
        }

        if (projectReceipt is null)
        {
            _output.WriteClientInputError(
                "Project deployment receipt is missing or unreadable. Run `kyber-weave squad install` to repair the deployment.");
            return 1;
        }

        return ApplyPresetToDeployment(
            targets,
            preset,
            SquadDeploymentScope.Project,
            projectRoot,
            projectRoot,
            projectReceipt,
            projectLock,
            stateStore,
            selectionStores,
            settings);
    }

    private int ApplyPresetToDeployment(
        IReadOnlyList<SquadTarget> targets,
        SquadModelPreset preset,
        SquadDeploymentScope scope,
        string anchorRoot,
        string projectRoot,
        SquadReceipt activeReceipt,
        SquadLock? activeLock,
        ISquadStateStore effectiveStateStore,
        IReadOnlyList<ISquadModelPresetSelectionStore> selectionStores,
        SquadPresetUseSettings settings)
    {
        ISquadGlobalRootResolver effectiveGlobalRoots = _globalRoots;
        HashSet<string> targetTokens =
            targets.Select(SquadTargetCatalog.GetToken).ToHashSet(StringComparer.OrdinalIgnoreCase);

        SquadCanonicalProfiles? canonical =
            _canonicalProfiles.Resolve(activeLock?.SquadVersion);
        if (canonical is null)
        {
            // The deployment cannot be re-rendered without the release it was rendered from:
            // fail before the rewrite loop in both dry-run and apply, never printing a plan
            // built on a silent general fallback.
            _output.WriteCanonicalProfilesUnresolvedError();
            return 1;
        }

        try
        {
            _validator.Validate(preset, canonical.Profiles);
        }
        catch (SquadSourceValidationException ex)
        {
            _output.WriteClientInputError(
                SquadCommandComposition.FormatPresetValidationError(ex));
            return 1;
        }

        SquadModelProfiles mergedProfiles = preset.ApplyTo(canonical.Profiles);

        SquadPresetRewriteResult rewrite = SquadPresetFileRewriter.Rewrite(
            activeReceipt.Files,
            targetTokens,
            mergedProfiles,
            canonical.AgentProfiles,
            file =>
            {
                string physicalPath = scope == SquadDeploymentScope.Global
                    ? SquadDeploymentPlan.ResolveOwnedFilePath(activeReceipt, anchorRoot, effectiveGlobalRoots, file, _pathSemantics)
                    : Path.Combine(anchorRoot, file.RelativePath);
                return _files.FileExists(physicalPath) ? _files.ReadAllBytes(physicalPath) : null;
            },
            new ArbiterModelTargetsPreset(preset.Name, preset.Digest),
            bundled: canonical.Profiles);

        if (settings.DryRun)
        {
            foreach (SquadModelDiffItem diff in rewrite.Diffs)
            {
                _output.WriteLine(
                    $"{diff.Agent}: {diff.OldModel ?? "none"} -> {diff.NewModel ?? "none"}");
            }

            return 0;
        }

        Dictionary<SquadTarget, SquadAppliedPreset> applied = activeLock?.ModelPresets != null
            ? new Dictionary<SquadTarget, SquadAppliedPreset>(activeLock.ModelPresets)
            : new Dictionary<SquadTarget, SquadAppliedPreset>();

        foreach (SquadTarget target in targets)
        {
            applied[target] = new SquadAppliedPreset(preset.Name, preset.Digest);
        }

        SquadLock nextLock = (activeLock ?? new SquadLock(
                LockSchemaV2,
                "1.0.0",
                "1.0.0",
                "1.0.0",
                "full",
                targets.Select(SquadTargetCatalog.GetToken).ToArray(),
                [],
                "best-effort",
                "0".PadRight(64, '0'),
                "0".PadRight(64, '0'),
                SquadApmIdentity.None)) with
        {
            Schema = LockSchemaV2,
            ModelPresets = applied
        };

        SquadDeploymentPlan plan = _planner.CreateUpdate(
            anchorRoot,
            scope,
            nextLock,
            rewrite.Files,
            activeReceipt,
            [],
            true,
            _time,
            effectiveGlobalRoots);

        ISquadTransaction transaction = _transactions.Create(null);
        transaction.Execute(plan);

        foreach (ISquadModelPresetSelectionStore store in selectionStores)
        {
            foreach (SquadTarget target in targets)
            {
                store.Record(projectRoot, target, preset.Name);
            }
        }

        return 0;
    }
}
