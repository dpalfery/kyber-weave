using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Shows resolved model profiles for a preset and target harness.
/// </summary>
public sealed class SquadPresetShowCommand : Command<SquadPresetShowSettings>
{
    private readonly ISquadUserPaths? _userPaths;

    /// <summary>Creates a new preset show command using default dependencies.</summary>
    public SquadPresetShowCommand()
    {
    }

    /// <summary>Creates a new preset show command using injectable dependencies.</summary>
    internal SquadPresetShowCommand(ISquadUserPaths? userPaths = null)
    {
        _userPaths = userPaths;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadPresetShowSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = SquadCommandComposition.ResolveStateStore(_userPaths);
        ISquadUserPaths userPaths = _userPaths ?? stateStore.UserPaths;
        SquadModelPresetStore presetStore = new(userPaths);

        string altHome = Path.Combine(userPaths.HomeDirectory, "home");
        SquadCommandComposition.NormalizePresetFile(Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets"), settings.Name);
        SquadCommandComposition.NormalizePresetFile(Path.Combine(altHome, ".kyber-weave", "squad", "presets"), settings.Name);

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
            SquadCommandComposition.WriteClientInputError($"Unknown preset '{settings.Name}'.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(settings.Target))
        {
            SquadCommandComposition.WriteClientInputError("No target specified. Specify a target using --target <target>.");
            return 2;
        }

        SquadTarget target;
        try
        {
            IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse([settings.Target]);
            if (targets.Count != 1)
            {
                SquadCommandComposition.WriteClientInputError($"Specify exactly one target using --target. Received: '{settings.Target}'.");
                return 2;
            }

            target = targets[0];
        }
        catch (ArgumentException ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        if (target == SquadTarget.Warp)
        {
            SquadCommandComposition.WriteLine("Warp renders no model.");
            return 0;
        }

        SquadSource? source = SquadCommandComposition.TryResolveSquadSource();
        if (source is null)
        {
            SquadCommandComposition.WriteClientInputError("Could not resolve Squad model profiles from canonical source.");
            return 1;
        }

        SquadModelProfiles bundledProfiles = source.ModelProfiles;
        SquadModelProfiles resolvedProfiles = preset.ApplyTo(bundledProfiles);
        string targetToken = SquadTargetCatalog.GetToken(target);

        IEnumerable<string> allCategories = bundledProfiles.Profiles.Keys
            .Union(preset.Profiles.Keys)
            .Order(StringComparer.Ordinal);

        foreach (string category in allCategories)
        {
            string? resolvedToken = SquadModelResolution.ResolveToken(resolvedProfiles, category, target);
            bool isOverridden = preset.Profiles.TryGetValue(category, out SquadModelProfile? p) &&
                (p.HarnessModels.ContainsKey(targetToken) ||
                 (p.Default is not null && (!bundledProfiles.Profiles.TryGetValue(category, out SquadModelProfile? bp) || !bp.HarnessModels.ContainsKey(targetToken))));

            if (isOverridden)
            {
                SquadCommandComposition.WriteLine($"{category}: {resolvedToken ?? "none"}");
            }
            else
            {
                SquadCommandComposition.WriteLine($"{category}: {resolvedToken ?? "none"} (bundled)");
            }
        }

        return 0;
    }
}
