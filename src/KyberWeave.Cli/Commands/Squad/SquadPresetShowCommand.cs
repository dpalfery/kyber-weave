using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Validation;
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
    internal SquadPresetShowCommand(
        ISquadUserPaths? userPaths = null,
        Func<string?, ISquadUserPaths, SquadCanonicalProfiles?>? canonicalProfileResolver = null)
    {
        _userPaths = userPaths;
        if (canonicalProfileResolver is not null)
            CanonicalProfileResolver = canonicalProfileResolver;
    }

    /// <summary>Resolves canonical model profiles for this command.</summary>
    /// <remarks>
    ///     Injectable seam so tests can force the no-checkout path; the production default is the
    ///     composition static, which prefers a checkout and falls back to the newest home snapshot.
    /// </remarks>
    internal Func<string?, ISquadUserPaths, SquadCanonicalProfiles?> CanonicalProfileResolver { get; } =
        SquadCommandComposition.ResolveCanonicalProfiles;

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadPresetShowSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = SquadCommandComposition.ResolveStateStore(_userPaths);
        ISquadUserPaths userPaths = _userPaths ?? stateStore.UserPaths;
        SquadModelPresetStore presetStore = new(userPaths);

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
            SquadCommandComposition.WriteClientInputError($"Unknown preset '{settings.Name}'.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(settings.Target))
        {
            SquadCommandComposition.WriteClientInputError(
                "No target specified. Specify a target using --target <target>.");
            return 2;
        }

        SquadTarget target;
        try
        {
            IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse([settings.Target]);
            if (targets.Count != 1)
            {
                SquadCommandComposition.WriteClientInputError(
                    $"Specify exactly one target using --target. Received: '{settings.Target}'.");
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

        SquadCanonicalProfiles? canonical = CanonicalProfileResolver(null, userPaths);
        if (canonical is null)
        {
            // Show has no deployment context to key a version on, so the chain already fell
            // back to the newest snapshot: a null here means no source at all, and the remedy
            // matches the one use and clear print.
            SquadCommandComposition.WriteCanonicalProfilesUnresolvedError();
            return 1;
        }

        // Show resolves checkout-first or newest-snapshot while use applies the lock-keyed
        // snapshot, so the rows can preview a different release than an apply would use:
        // name which source they came from. A checkout carries no release version.
        if (string.IsNullOrEmpty(canonical.SquadVersion))
            SquadCommandComposition.WriteLine("Canonical model profiles from the current checkout.");
        else
            SquadCommandComposition.WriteLine($"Canonical model profiles from Squad release {canonical.SquadVersion}.");

        try
        {
            SquadModelPresetValidator.Validate(preset, canonical.Profiles);
        }
        catch (SquadSourceValidationException ex)
        {
            SquadCommandComposition.WriteClientInputError(
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
