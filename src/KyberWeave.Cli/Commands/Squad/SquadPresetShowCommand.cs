using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Validation;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Shows resolved model profiles for a preset and target harness.
/// </summary>
internal sealed class SquadPresetShowCommand : Command<SquadPresetShowSettings>
{
    private readonly ISquadUserPaths _userPaths;
    private readonly ISquadModelPresetStore _presetStore;
    private readonly ISquadCanonicalProfileResolver _canonicalProfiles;
    private readonly SquadConsole _output;
    private readonly ISquadModelPresetValidator _validator;

    /// <summary>Creates the command over the Squad services the host registers.</summary>
    /// <param name="userPaths">Names the home the presets live under.</param>
    /// <param name="presetStore">Loads the named presets.</param>
    /// <param name="canonicalProfiles">Finds the canonical model profiles to show.</param>
    /// <param name="output">Where the rows and the refusals are written.</param>
    /// <param name="validator">Checks the preset against the canonical model profiles.</param>
    public SquadPresetShowCommand(
        ISquadUserPaths userPaths,
        ISquadModelPresetStore presetStore,
        ISquadCanonicalProfileResolver canonicalProfiles,
        SquadConsole output,
        ISquadModelPresetValidator validator)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        ArgumentNullException.ThrowIfNull(presetStore);
        ArgumentNullException.ThrowIfNull(canonicalProfiles);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(validator);
        _userPaths = userPaths;
        _presetStore = presetStore;
        _canonicalProfiles = canonicalProfiles;
        _output = output;
        _validator = validator;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadPresetShowSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ISquadUserPaths userPaths = _userPaths;
        ISquadModelPresetStore presetStore = _presetStore;

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
            _output.WriteClientInputError($"Unknown preset '{settings.Name}'.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(settings.Target))
        {
            _output.WriteClientInputError(
                "No target specified. Specify a target using --target <target>.");
            return 2;
        }

        SquadTarget target;
        try
        {
            IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse([settings.Target]);
            if (targets.Count != 1)
            {
                _output.WriteClientInputError(
                    $"Specify exactly one target using --target. Received: '{settings.Target}'.");
                return 2;
            }

            target = targets[0];
        }
        catch (ArgumentException ex)
        {
            _output.WriteClientInputError(ex.Message);
            return 2;
        }

        if (target == SquadTarget.Warp)
        {
            _output.WriteLine("Warp renders no model.");
            return 0;
        }

        SquadCanonicalProfiles? canonical = _canonicalProfiles.Resolve(null);
        if (canonical is null)
        {
            // Show has no deployment context to key a version on, so the chain already fell
            // back to the newest snapshot: a null here means no source at all, and the remedy
            // matches the one use and clear print.
            _output.WriteCanonicalProfilesUnresolvedError();
            return 1;
        }

        // Show resolves checkout-first or newest-snapshot while use applies the lock-keyed
        // snapshot, so the rows can preview a different release than an apply would use:
        // name which source they came from. A checkout carries no release version.
        if (string.IsNullOrEmpty(canonical.SquadVersion))
            _output.WriteLine("Canonical model profiles from the current checkout.");
        else
            _output.WriteLine($"Canonical model profiles from Squad release {canonical.SquadVersion}.");

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

        SquadModelProfiles bundledProfiles = canonical.Profiles;
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
                                 (p.Default is not null &&
                                  (!bundledProfiles.Profiles.TryGetValue(category, out SquadModelProfile? bp) ||
                                   !bp.HarnessModels.ContainsKey(targetToken))));

            if (isOverridden)
            {
                _output.WriteLine($"{category}: {resolvedToken ?? "none"}");
            }
            else
            {
                _output.WriteLine($"{category}: {resolvedToken ?? "none"} (bundled)");
            }
        }

        return 0;
    }
}
