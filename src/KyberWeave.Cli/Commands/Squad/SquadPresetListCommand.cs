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

        string? effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        string projectRoot = SquadCommandComposition.ResolveTargetRoot(effectivePath);

        List<string> presetNames = [.. presetStore.List().Order(StringComparer.Ordinal)];

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
