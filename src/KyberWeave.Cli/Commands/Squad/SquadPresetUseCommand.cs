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

        string altHome = Path.Combine(userPaths.HomeDirectory, "home");
        SquadCommandComposition.SyncPresetFiles(userPaths);

        SquadStateStore effectiveStateStore = stateStore;
        ISquadUserPaths effectiveUserPaths = userPaths;
        if (Directory.Exists(altHome))
        {
            SquadStateStore altStateStore = new(new SquadCommandComposition.CliSquadUserPaths(altHome, userPaths.ApplicationDataDirectory));
            if (scope == SquadDeploymentScope.Global)
            {
                string globalAnchor = SquadCommandComposition.ResolveGlobalAnchorRoot(stateStore, effectivePath, dryRun: settings.DryRun);
                if (stateStore.ReadReceipt(globalAnchor, SquadDeploymentScope.Global) is null &&
                    stateStore.ReadLock(globalAnchor, SquadDeploymentScope.Global) is null &&
                    (altStateStore.ReadReceipt(globalAnchor, SquadDeploymentScope.Global) is not null ||
                     altStateStore.ReadLock(globalAnchor, SquadDeploymentScope.Global) is not null))
                {
                    effectiveStateStore = altStateStore;
                    effectiveUserPaths = altStateStore.UserPaths;
                }
            }
            else
            {
                if (stateStore.ReadReceipt(projectRoot, SquadDeploymentScope.Project) is null &&
                    stateStore.ReadLock(projectRoot, SquadDeploymentScope.Project) is null &&
                    (altStateStore.ReadReceipt(projectRoot, SquadDeploymentScope.Project) is not null ||
                     altStateStore.ReadLock(projectRoot, SquadDeploymentScope.Project) is not null))
                {
                    effectiveStateStore = altStateStore;
                    effectiveUserPaths = altStateStore.UserPaths;
                }
            }
        }

        SquadModelPreset? preset = null;
        try
        {
            preset = presetStore.TryLoad(settings.Name);
            if (preset is null && Directory.Exists(altHome))
            {
                SquadModelPresetStore altPresetStore = new(new SquadCommandComposition.CliSquadUserPaths(altHome, userPaths.ApplicationDataDirectory));
                preset = altPresetStore.TryLoad(settings.Name);
            }
        }
        catch (ArgumentException)
        {
            // invalid regex format, handled below
        }

        if (preset is null)
        {
            IReadOnlyList<string> existing = presetStore.List();
            if (Directory.Exists(altHome))
            {
                SquadModelPresetStore altPresetStore = new(new SquadCommandComposition.CliSquadUserPaths(altHome, userPaths.ApplicationDataDirectory));
                HashSet<string> combined = new(existing, StringComparer.Ordinal);
                foreach (string name in altPresetStore.List())
                {
                    combined.Add(name);
                }

                existing = [.. combined.Order(StringComparer.Ordinal)];
            }

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
                    ? SquadCommandComposition.ResolveGlobalAnchorRoot(effectiveStateStore, effectivePath, dryRun: false)
                    : projectRoot;
                SquadReceipt? receipt = effectiveStateStore.ReadReceipt(checkRoot, scope);
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
            string globalAnchor = SquadCommandComposition.ResolveGlobalAnchorRoot(effectiveStateStore, effectivePath, dryRun: settings.DryRun);
            SquadReceipt? globalReceipt = effectiveStateStore.ReadReceipt(globalAnchor, SquadDeploymentScope.Global);
            SquadLock? globalLock = effectiveStateStore.ReadLock(globalAnchor, SquadDeploymentScope.Global);

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

            return ApplyPresetToDeployment(
                targets: targets,
                preset: preset,
                scope: SquadDeploymentScope.Global,
                anchorRoot: globalAnchor,
                projectRoot: projectRoot,
                activeReceipt: globalReceipt!,
                activeLock: globalLock,
                effectiveStateStore: effectiveStateStore,
                selectionStores: selectionStores,
                settings: settings);
        }

        SquadReceipt? projectReceipt = effectiveStateStore.ReadReceipt(projectRoot, SquadDeploymentScope.Project);
        SquadLock? projectLock = effectiveStateStore.ReadLock(projectRoot, SquadDeploymentScope.Project);

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

        return ApplyPresetToDeployment(
            targets: targets,
            preset: preset,
            scope: SquadDeploymentScope.Project,
            anchorRoot: projectRoot,
            projectRoot: projectRoot,
            activeReceipt: projectReceipt!,
            activeLock: projectLock,
            effectiveStateStore: effectiveStateStore,
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
            if (!TryParseFrontmatter(fileText, out string frontmatter, out string body, out string newline))
            {
                renderedFiles.Add(new SquadDeploymentFile(file.RelativePath, rawBytes, file.Target));
                continue;
            }

            string agentName = ExtractField(frontmatter, "name") ?? ResolveIdentity(file.RelativePath);
            string? currentModel = ExtractField(frontmatter, "model");
            string profileCategory = ExtractField(frontmatter, "model-profile") ?? "general";

            string? newModel = null;
            if (preset.Profiles.TryGetValue(profileCategory, out SquadModelProfile? presetProfile))
            {
                if (presetProfile.HarnessModels.TryGetValue(file.Target, out string? hm))
                {
                    newModel = hm;
                }
                else if (presetProfile.Default is not null)
                {
                    newModel = presetProfile.Default;
                }
            }
            else if (preset.Profiles.TryGetValue("general", out SquadModelProfile? genProfile))
            {
                if (genProfile.HarnessModels.TryGetValue(file.Target, out string? hm))
                {
                    newModel = hm;
                }
                else if (genProfile.Default is not null)
                {
                    newModel = genProfile.Default;
                }
            }

            if (string.Equals(newModel, "inherit", StringComparison.OrdinalIgnoreCase))
            {
                newModel = null;
            }

            if (!string.Equals(currentModel, newModel, StringComparison.Ordinal))
            {
                diffs.Add(new SquadModelDiffItem(agentName, currentModel, newModel));
            }

            string updatedFrontmatter = UpdateFrontmatterModel(frontmatter, newModel, newline);
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

    private static string ResolveIdentity(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);
        if (fileName.Equals("agent.md", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(relativePath);
            return string.IsNullOrEmpty(parent) ? fileName : Path.GetFileName(parent);
        }

        if (fileName.EndsWith(".agent.md", StringComparison.Ordinal))
        {
            return fileName[..^".agent.md".Length];
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static bool TryParseFrontmatter(
        string content,
        out string frontmatter,
        out string body,
        out string newline)
    {
        frontmatter = string.Empty;
        body = string.Empty;
        newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        string trimmedContent = content.StartsWith('\uFEFF') ? content[1..] : content;

        if (!trimmedContent.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        int firstNewline = trimmedContent.IndexOf('\n', StringComparison.Ordinal);
        if (firstNewline < 0)
        {
            return false;
        }

        int secondDashes = trimmedContent.IndexOf($"{newline}---", firstNewline, StringComparison.Ordinal);
        if (secondDashes < 0)
        {
            secondDashes = trimmedContent.IndexOf("\n---", firstNewline, StringComparison.Ordinal);
            if (secondDashes < 0)
            {
                return false;
            }
        }

        frontmatter = trimmedContent[(firstNewline + 1)..secondDashes].Trim('\r', '\n');
        int relIdx = trimmedContent.AsSpan(secondDashes + 4).IndexOf('\n');
        int endOfSecondDashes = relIdx >= 0 ? secondDashes + 4 + relIdx : -1;
        body = endOfSecondDashes >= 0 ? trimmedContent[endOfSecondDashes..] : string.Empty;
        return true;
    }

    private static string? ExtractField(string frontmatter, string fieldName)
    {
        string[] lines = frontmatter.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string prefix = $"{fieldName}:";
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string val = trimmed[prefix.Length..].Trim().Trim('\'', '"');
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }
        }

        return null;
    }

    private static string UpdateFrontmatterModel(string frontmatter, string? newModel, string newline)
    {
        string[] lines = frontmatter.Split(['\r', '\n'], StringSplitOptions.None);
        List<string> result = [];
        bool modelFound = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
            {
                modelFound = true;
                if (!string.IsNullOrWhiteSpace(newModel))
                {
                    result.Add($"model: {newModel}");
                }
            }
            else
            {
                result.Add(line);
            }
        }

        if (!modelFound && !string.IsNullOrWhiteSpace(newModel))
        {
            int insertIndex = result.FindIndex(l => l.Trim().StartsWith("name:", StringComparison.OrdinalIgnoreCase));
            if (insertIndex >= 0)
            {
                result.Insert(insertIndex + 1, $"model: {newModel}");
            }
            else
            {
                result.Insert(0, $"model: {newModel}");
            }
        }

        return string.Join(newline, result);
    }
}
