using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Lists available model preset profiles and their active usages.
/// </summary>
public sealed class SquadPresetListCommand : Command<SquadPresetListSettings>
{
    private readonly ISquadUserPaths? _userPaths;
    private readonly SquadStateStore? _stateStore;

    /// <summary>Creates a new preset list command using default dependencies.</summary>
    public SquadPresetListCommand()
    {
    }

    /// <summary>Creates a new preset list command using injectable dependencies.</summary>
    internal SquadPresetListCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadPresetListSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);
        ISquadUserPaths userPaths = _userPaths ?? stateStore.UserPaths;
        SquadModelPresetStore presetStore = new(userPaths);
        IReadOnlyList<SquadModelPresetSelectionStore> selectionStores = SquadCommandComposition.ResolveSelectionStores(userPaths);

        string altHome = Path.Combine(userPaths.HomeDirectory, "home");
        SquadCommandComposition.NormalizeAllPresetFiles(Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets"));
        SquadCommandComposition.NormalizeAllPresetFiles(Path.Combine(altHome, ".kyber-weave", "squad", "presets"));

        string? effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        string projectRoot = SquadCommandComposition.ResolveTargetRoot(effectivePath);

        HashSet<string> presetNameSet = new(presetStore.List(), StringComparer.Ordinal);
        if (Directory.Exists(altHome))
        {
            SquadModelPresetStore altPresetStore = new(new SquadCommandComposition.CliSquadUserPaths(altHome, userPaths.ApplicationDataDirectory));
            foreach (string name in altPresetStore.List())
            {
                presetNameSet.Add(name);
            }
        }

        List<string> presetNames = [.. presetNameSet.Order(StringComparer.Ordinal)];

        Dictionary<string, HashSet<string>> usages = new(StringComparer.Ordinal);

        string globalAnchor = SquadCommandComposition.ResolveGlobalAnchorRoot(stateStore, effectivePath, dryRun: false);
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

        foreach (SquadModelPresetSelectionStore store in selectionStores)
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
        if (Directory.Exists(altHome))
        {
            SquadStateStore altStateStore = new(new SquadCommandComposition.CliSquadUserPaths(altHome, userPaths.ApplicationDataDirectory));
            allProjects.AddRange(altStateStore.ListProjects());
        }

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

            foreach (SquadModelPresetSelectionStore store in selectionStores)
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

        foreach (string homeDir in new[] { userPaths.HomeDirectory, altHome })
        {
            string selectionsDir = Path.Combine(homeDir, ".kyber-weave", "squad", "selections");
            if (!Directory.Exists(selectionsDir))
            {
                continue;
            }

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
                        foreach (SquadModelPresetSelectionStore store in selectionStores)
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
            if (!File.Exists(presetPath) && Directory.Exists(altHome))
            {
                string candidateAltPath = Path.Combine(altHome, ".kyber-weave", "squad", "presets", $"{name}.yml");
                if (File.Exists(candidateAltPath))
                {
                    presetPath = candidateAltPath;
                }
            }

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
