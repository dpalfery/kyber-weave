using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Lists available model preset profiles and their active usages.
/// </summary>
internal sealed class SquadPresetListCommand : Command<SquadPresetListSettings>
{
    private readonly ISquadUserPaths _userPaths;
    private readonly ISquadStateStore _stateStore;
    private readonly ISquadModelPresetStore _presetStore;
    private readonly ISquadModelPresetSelectionStore _selectionStore;
    private readonly SquadCommandRoots _roots;

    /// <summary>Creates the command over the Squad services the host registers.</summary>
    /// <param name="userPaths">Names the home the presets and selections live under.</param>
    /// <param name="stateStore">Reads the deployment state the selections apply to.</param>
    /// <param name="presetStore">Lists the presets.</param>
    /// <param name="selectionStore">Reads the project's selection.</param>
    /// <param name="roots">Resolves the target root and anchors a global run.</param>
    public SquadPresetListCommand(
        ISquadUserPaths userPaths,
        ISquadStateStore stateStore,
        ISquadModelPresetStore presetStore,
        ISquadModelPresetSelectionStore selectionStore,
        SquadCommandRoots roots)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(presetStore);
        ArgumentNullException.ThrowIfNull(selectionStore);
        ArgumentNullException.ThrowIfNull(roots);
        _userPaths = userPaths;
        _stateStore = stateStore;
        _presetStore = presetStore;
        _selectionStore = selectionStore;
        _roots = roots;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadPresetListSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ISquadStateStore stateStore = _stateStore;
        ISquadUserPaths userPaths = _userPaths;
        ISquadModelPresetStore presetStore = _presetStore;
        IReadOnlyList<ISquadModelPresetSelectionStore> selectionStores = [_selectionStore];

        string? effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        string projectRoot = _roots.ResolveTargetRoot(effectivePath);

        List<string> presetNames = [.. presetStore.List().Order(StringComparer.Ordinal)];

        Dictionary<string, HashSet<string>> usages = new(StringComparer.Ordinal);

        string globalAnchor = _roots.ResolveGlobalAnchorRoot(stateStore, effectivePath, dryRun: false);
        SquadLock? globalLock = stateStore.ReadLock(globalAnchor, SquadDeploymentScope.Global);
        if (globalLock?.ModelPresets is not null)
        {
            foreach (KeyValuePair<SquadTarget, SquadAppliedPreset> kvp in globalLock.ModelPresets)
            {
                string token = $"global:{SquadTargetCatalog.GetToken(kvp.Key)}";
                AddUsage(usages, kvp.Value.Name, token);
            }
        }

        SquadLock? projectLock = stateStore.ReadLock(projectRoot, SquadDeploymentScope.Project);
        if (projectLock?.ModelPresets is not null)
        {
            foreach (KeyValuePair<SquadTarget, SquadAppliedPreset> kvp in projectLock.ModelPresets)
            {
                string token = $"project:{SquadTargetCatalog.GetToken(kvp.Key)}";
                AddUsage(usages, kvp.Value.Name, token);
            }
        }

        foreach (ISquadModelPresetSelectionStore store in selectionStores)
        {
            SquadModelPresetSelection? currentSelection = store.Read(projectRoot);
            if (currentSelection?.ModelPresets is not null)
            {
                foreach (KeyValuePair<SquadTarget, string> kvp in currentSelection.ModelPresets)
                {
                    string token = $"project:{SquadTargetCatalog.GetToken(kvp.Key)}";
                    AddUsage(usages, kvp.Value, token);
                }
            }
        }

        List<SquadProjectRegistryEntry> allProjects = [.. stateStore.ListProjects()];

        foreach (SquadProjectRegistryEntry project in allProjects)
        {
            if (string.Equals(project.Path, projectRoot, StringComparison.Ordinal))
            {
                continue;
            }

            SquadLock? regLock = stateStore.ReadLock(project.Path, SquadDeploymentScope.Project);
            if (regLock?.ModelPresets is not null)
            {
                foreach (KeyValuePair<SquadTarget, SquadAppliedPreset> kvp in regLock.ModelPresets)
                {
                    string token = $"project:{SquadTargetCatalog.GetToken(kvp.Key)}";
                    AddUsage(usages, kvp.Value.Name, token);
                }
            }

            foreach (ISquadModelPresetSelectionStore store in selectionStores)
            {
                SquadModelPresetSelection? regSel = store.Read(project.Path);
                if (regSel?.ModelPresets is not null)
                {
                    foreach (KeyValuePair<SquadTarget, string> kvp in regSel.ModelPresets)
                    {
                        string token = $"project:{SquadTargetCatalog.GetToken(kvp.Key)}";
                        AddUsage(usages, kvp.Value, token);
                    }
                }
            }
        }

        string selectionsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "selections");
        if (Directory.Exists(selectionsDir))
        {
            foreach (string file in Directory.EnumerateFiles(selectionsDir, "*.yml"))
            {
                try
                {
                    string[] lines = File.ReadAllLines(file);
                    string? root = null;
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("root: ", StringComparison.Ordinal))
                        {
                            root = line["root: ".Length..].Trim();
                            break;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        foreach (ISquadModelPresetSelectionStore store in selectionStores)
                        {
                            SquadModelPresetSelection? sel = store.Read(root);
                            if (sel?.ModelPresets is not null)
                            {
                                foreach (KeyValuePair<SquadTarget, string> kvp in sel.ModelPresets)
                                {
                                    string token = $"project:{SquadTargetCatalog.GetToken(kvp.Key)}";
                                    AddUsage(usages, kvp.Value, token);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                }
            }
        }

        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        foreach (string name in presetNames)
        {
            string presetPath = Path.Combine(presetsDir, $"{name}.yml");

            string usedByText = "none";
            if (usages.TryGetValue(name, out HashSet<string>? tokens) && tokens.Count > 0)
            {
                usedByText = string.Join(", ", tokens.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
            }

            SquadCommandComposition.WriteLine($"{name}  {presetPath}  used by: {usedByText}");
        }

        return 0;
    }

    private static void AddUsage(Dictionary<string, HashSet<string>> usages, string presetName, string token)
    {
        if (!usages.TryGetValue(presetName, out HashSet<string>? tokens))
        {
            tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            usages[presetName] = tokens;
        }

        tokens.Add(token);
    }
}
