using System.Text;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Applies a model preset profile to target harnesses, updating deployed files and recording project selections.
/// </summary>
public sealed class SquadPresetUseCommand : Command<SquadPresetUseSettings>
{
    private const string LockSchemaV2 = "kyber-squad.lock/v2";

    private readonly ISquadUserPaths? _userPaths;
    private readonly SquadStateStore? _stateStore;
    private readonly ISquadGlobalRootResolver? _globalRoots;

    /// <summary>Creates a new preset use command using default dependencies.</summary>
    public SquadPresetUseCommand()
    {
    }

    /// <summary>Creates a new preset use command using injectable dependencies.</summary>
    internal SquadPresetUseCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadGlobalRootResolver? globalRoots = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
        _globalRoots = globalRoots;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadPresetUseSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);
        ISquadUserPaths userPaths = _userPaths ?? stateStore.UserPaths;
        SquadModelPresetStore presetStore = new(userPaths);
        IReadOnlyList<SquadModelPresetSelectionStore> selectionStores = SquadCommandComposition.ResolveSelectionStores(userPaths);

        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);
        string? effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        string projectRoot = SquadCommandComposition.ResolveTargetRoot(effectivePath);

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
            SquadCommandComposition.WriteClientInputError(
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
                    ? SquadCommandComposition.ResolveGlobalAnchorRoot(stateStore, effectivePath, dryRun: false)
                    : projectRoot;
                SquadReceipt? receipt = stateStore.ReadReceipt(checkRoot, scope);
                if (receipt is not null && receipt.Files.Count > 0)
                {
                    targets = SquadTargetCatalog.Parse(receipt.Files.Select(file => file.Target).Distinct());
                }
                else
                {
                    SquadCommandComposition.WriteClientInputError(
                        "No deployment target specified. Specify target(s) using --target <target>.");
                    return 2;
                }
            }
        }
        catch (ArgumentException ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        if (scope == SquadDeploymentScope.Global)
        {
            string globalAnchor = SquadCommandComposition.ResolveGlobalAnchorRoot(stateStore, effectivePath, dryRun: settings.DryRun);
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
                    SquadCommandComposition.WriteClientInputError(
                        $"{token} is not deployed globally; run `kyber-weave squad install --global --target {token}` first.");
                    return 2;
                }
            }

            if (globalReceipt is null)
            {
                SquadCommandComposition.WriteClientInputError(
                    "Global deployment receipt is missing or unreadable. Run `kyber-weave squad install --global` to repair the deployment.");
                return 1;
            }

            return ApplyPresetToDeployment(
                targets: targets,
                preset: preset,
                scope: SquadDeploymentScope.Global,
                anchorRoot: globalAnchor,
                projectRoot: projectRoot,
                activeReceipt: globalReceipt,
                activeLock: globalLock,
                effectiveStateStore: stateStore,
                selectionStores: selectionStores,
                settings: settings);
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
                foreach (SquadModelPresetSelectionStore store in selectionStores)
                {
                    foreach (SquadTarget target in targets)
                    {
                        store.Set(projectRoot, target, preset.Name);
                    }
                }
            }

            foreach (SquadTarget target in targets)
            {
                string token = SquadTargetCatalog.GetToken(target);
                SquadCommandComposition.WriteLine(
                    $"Recorded `{preset.Name}` for `{token}` in this project; it applies on install or `squad run`.");
            }

            return 0;
        }

        if (projectReceipt is null)
        {
            SquadCommandComposition.WriteClientInputError(
                "Project deployment receipt is missing or unreadable. Run `kyber-weave squad install` to repair the deployment.");
            return 1;
        }

        return ApplyPresetToDeployment(
            targets: targets,
            preset: preset,
            scope: SquadDeploymentScope.Project,
            anchorRoot: projectRoot,
            projectRoot: projectRoot,
            activeReceipt: projectReceipt,
            activeLock: projectLock,
            effectiveStateStore: stateStore,
            selectionStores: selectionStores,
            settings: settings);
    }

    private int ApplyPresetToDeployment(
        IReadOnlyList<SquadTarget> targets,
        SquadModelPreset preset,
        SquadDeploymentScope scope,
        string anchorRoot,
        string projectRoot,
        SquadReceipt activeReceipt,
        SquadLock? activeLock,
        SquadStateStore effectiveStateStore,
        IReadOnlyList<SquadModelPresetSelectionStore> selectionStores,
        SquadPresetUseSettings settings)
    {
        ISquadGlobalRootResolver effectiveGlobalRoots = _globalRoots ?? SquadCommandComposition.ResolveGlobalRoots();
        HashSet<string> targetTokens = targets.Select(SquadTargetCatalog.GetToken).ToHashSet(StringComparer.OrdinalIgnoreCase);

        SquadSource? source = SquadCommandComposition.TryResolveSquadSource();
        Dictionary<string, string> agentProfiles = source?.Agents.ToDictionary(a => a.Name, a => a.ModelProfile, StringComparer.OrdinalIgnoreCase)
            ?? new(StringComparer.OrdinalIgnoreCase);
        SquadModelProfiles? mergedProfiles = source is not null
            ? preset.ApplyTo(source.ModelProfiles)
            : new SquadModelProfiles("kyber-squad.model-profiles/v1", preset.Profiles, string.Empty);

        List<SquadModelDiffItem> diffs = [];
        List<SquadDeploymentFile> renderedFiles = [];

        foreach (SquadOwnedFile file in activeReceipt.Files)
        {
            string physicalPath = scope == SquadDeploymentScope.Global
                ? SquadDeploymentPlan.ResolveOwnedFilePath(activeReceipt, anchorRoot, effectiveGlobalRoots, file)
                : Path.Combine(anchorRoot, file.RelativePath);

            if (!File.Exists(physicalPath))
            {
                continue;
            }

            byte[] rawBytes = File.ReadAllBytes(physicalPath);
            if (!targetTokens.Contains(file.Target) || !file.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                renderedFiles.Add(new SquadDeploymentFile(file.RelativePath, rawBytes, file.Target));
                continue;
            }

            string fileText = Encoding.UTF8.GetString(rawBytes);
            if (!SquadFrontmatterHelper.TryParseFrontmatter(fileText, out string frontmatter, out string body, out string newline))
            {
                renderedFiles.Add(new SquadDeploymentFile(file.RelativePath, rawBytes, file.Target));
                continue;
            }

            string agentName = SquadFrontmatterHelper.ExtractField(frontmatter, "name") ?? SquadFrontmatterHelper.ResolveIdentity(file.RelativePath);
            string? currentModel = SquadFrontmatterHelper.ExtractField(frontmatter, "model");
            string profileCategory = SquadFrontmatterHelper.ExtractField(frontmatter, "model-profile")
                ?? (agentProfiles.TryGetValue(agentName, out string? p) ? p : "general");

            string? newModel = SquadFrontmatterHelper.ResolveModel(mergedProfiles, profileCategory, file.Target);

            if (!string.Equals(currentModel, newModel, StringComparison.Ordinal))
            {
                diffs.Add(new SquadModelDiffItem(agentName, currentModel, newModel));
            }

            string updatedFrontmatter = SquadFrontmatterHelper.UpdateFrontmatterModel(frontmatter, newModel, newline);
            string updatedContent = $"---{newline}{updatedFrontmatter}{newline}---{body}";
            byte[] updatedBytes = Encoding.UTF8.GetBytes(updatedContent);

            renderedFiles.Add(new SquadDeploymentFile(file.RelativePath, updatedBytes, file.Target));
        }

        if (settings.DryRun)
        {
            diffs.Sort((a, b) => StringComparer.Ordinal.Compare(a.Agent, b.Agent));
            foreach (SquadModelDiffItem diff in diffs)
            {
                SquadCommandComposition.WriteLine($"{diff.Agent}: {diff.OldModel ?? "none"} -> {diff.NewModel ?? "none"}");
            }

            return 0;
        }

        Dictionary<SquadTarget, SquadAppliedPreset> applied = activeLock?.ModelPresets != null
            ? new(activeLock.ModelPresets)
            : new();

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

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            targetRoot: anchorRoot,
            scope: scope,
            squadLock: nextLock,
            renderedFiles: renderedFiles,
            previousReceipt: activeReceipt,
            degradations: [],
            replaceManaged: true,
            timeProvider: TimeProvider.System,
            globalRoots: effectiveGlobalRoots);

        SquadTransaction transaction = new(effectiveStateStore);
        transaction.Execute(plan);

        foreach (SquadModelPresetSelectionStore store in selectionStores)
        {
            foreach (SquadTarget target in targets)
            {
                store.Set(projectRoot, target, preset.Name);
            }
        }

        return 0;
    }
}
