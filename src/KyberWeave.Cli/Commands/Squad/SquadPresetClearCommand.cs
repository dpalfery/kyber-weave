using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Clears model preset overrides for target harnesses and restores bundled model configuration.
/// </summary>
internal sealed class SquadPresetClearCommand : Command<SquadPresetClearSettings>
{
    private const string LockSchemaV1 = "kyber-squad.lock/v1";
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
    public SquadPresetClearCommand(
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
        TimeProvider time)
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
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadPresetClearSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ISquadStateStore stateStore = _stateStore;
        IReadOnlyList<ISquadModelPresetSelectionStore> selectionStores = [_selectionStore];

        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);
        string? effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        string projectRoot = _roots.ResolveTargetRoot(effectivePath);

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

            return ClearPresetFromDeployment(
                targets,
                SquadDeploymentScope.Global,
                globalAnchor,
                projectRoot,
                globalReceipt
                    ?? throw new InvalidOperationException("A global preset clear needs the global deployment receipt."),
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
                        store.Clear(projectRoot, target);
                    }
                }
            }

            return 0;
        }

        return ClearPresetFromDeployment(
            targets,
            SquadDeploymentScope.Project,
            projectRoot,
            projectRoot,
            projectReceipt
                ?? throw new InvalidOperationException("A project preset clear needs the project deployment receipt."),
            projectLock,
            stateStore,
            selectionStores,
            settings);
    }

    private int ClearPresetFromDeployment(
        IReadOnlyList<SquadTarget> targets,
        SquadDeploymentScope scope,
        string anchorRoot,
        string projectRoot,
        SquadReceipt activeReceipt,
        SquadLock? activeLock,
        ISquadStateStore effectiveStateStore,
        IReadOnlyList<ISquadModelPresetSelectionStore> selectionStores,
        SquadPresetClearSettings settings)
    {
        ISquadGlobalRootResolver effectiveGlobalRoots = _globalRoots;
        HashSet<string> targetTokens =
            targets.Select(SquadTargetCatalog.GetToken).ToHashSet(StringComparer.OrdinalIgnoreCase);

        SquadCanonicalProfiles? canonical =
            _canonicalProfiles.Resolve(activeLock?.SquadVersion);
        if (canonical is null)
        {
            // Clearing restores bundled models, which are meaningless without the release the
            // deployment was rendered from: fail before the rewrite loop in both dry-run and
            // apply, exactly as use does.
            _output.WriteCanonicalProfilesUnresolvedError();
            return 1;
        }

        SquadPresetRewriteResult rewrite = BuildRewrite(
            activeReceipt,
            anchorRoot,
            scope,
            targetTokens,
            canonical,
            effectiveGlobalRoots);

        if (settings.DryRun)
        {
            WriteDiffs(rewrite);
            return 0;
        }

        Dictionary<SquadTarget, SquadAppliedPreset> applied = activeLock?.ModelPresets != null
            ? new Dictionary<SquadTarget, SquadAppliedPreset>(activeLock.ModelPresets)
            : new Dictionary<SquadTarget, SquadAppliedPreset>();

        foreach (SquadTarget target in targets)
        {
            applied.Remove(target);
        }

        SquadLock nextLock = BuildNextLock(activeLock, targets, applied);

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

        return ApplyPlan(plan, selectionStores, targets, projectRoot);
    }

    private SquadPresetRewriteResult BuildRewrite(
        SquadReceipt activeReceipt,
        string anchorRoot,
        SquadDeploymentScope scope,
        HashSet<string> targetTokens,
        SquadCanonicalProfiles canonical,
        ISquadGlobalRootResolver effectiveGlobalRoots)
    {
        return SquadPresetFileRewriter.Rewrite(
            activeReceipt.Files,
            targetTokens,
            canonical.Profiles,
            canonical.AgentProfiles,
            file => ReadOwnedFileBytes(activeReceipt, anchorRoot, scope, effectiveGlobalRoots, file),
            new ArbiterModelTargetsPreset(ArbiterModelTargetsFile.BundledPresetName, string.Empty));
    }

    private byte[]? ReadOwnedFileBytes(
        SquadReceipt activeReceipt,
        string anchorRoot,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver effectiveGlobalRoots,
        SquadOwnedFile file)
    {
        string physicalPath = scope == SquadDeploymentScope.Global
            ? SquadDeploymentPlanRules.ResolveOwnedFilePath(activeReceipt, anchorRoot, effectiveGlobalRoots, file, _pathSemantics)
            : Path.Combine(anchorRoot, file.RelativePath);
        return _files.FileExists(physicalPath) ? _files.ReadAllBytes(physicalPath) : null;
    }

    private void WriteDiffs(SquadPresetRewriteResult rewrite)
    {
        foreach (SquadModelDiffItem diff in rewrite.Diffs)
        {
            _output.WriteLine(
                $"{diff.Agent}: {diff.OldModel ?? "none"} -> {diff.NewModel ?? "none"}");
        }
    }

    private static SquadLock BuildNextLock(
        SquadLock? activeLock,
        IReadOnlyList<SquadTarget> targets,
        Dictionary<SquadTarget, SquadAppliedPreset> applied)
    {
        return (activeLock ?? new SquadLock(
                LockSchemaV1,
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
            Schema = applied.Count > 0 ? LockSchemaV2 : LockSchemaV1,
            ModelPresets = applied.Count > 0 ? applied : null
        };
    }

    private int ApplyPlan(
        SquadDeploymentPlan plan,
        IReadOnlyList<ISquadModelPresetSelectionStore> selectionStores,
        IReadOnlyList<SquadTarget> targets,
        string projectRoot)
    {
        ISquadTransaction transaction = _transactions.Create(null);
        try
        {
            transaction.Execute(plan);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException && failure is not OperationCanceledException)
        {
            // A recovery under this deployment's lease can refuse the stale plan, or fail and name the journal. Both
            // are this command's ordinary error output, not an unhandled exception.
            _output.WriteTransactionWarnings(transaction.Warnings);
            _output.WriteFailure(failure);
            return 1;
        }

        _output.WriteTransactionWarnings(transaction.Warnings);

        foreach (ISquadModelPresetSelectionStore store in selectionStores)
        {
            foreach (SquadTarget target in targets)
            {
                store.Clear(projectRoot, target);
            }
        }

        return 0;
    }
}
