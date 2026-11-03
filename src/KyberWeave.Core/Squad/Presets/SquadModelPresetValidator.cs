using KyberWeave.Core.Squad.Validation;

namespace KyberWeave.Core.Squad.Model;

/// <summary>
///     Cross-validates a Squad model preset against bundled model profiles.
/// </summary>
public static class SquadModelPresetValidator
{
    /// <summary>
    ///     Validates that all profile categories referenced by <paramref name="preset" /> exist in <paramref name="bundled" />
    ///     .
    /// </summary>
    /// <param name="preset">The model preset to validate.</param>
    /// <param name="bundled">The bundled model profiles to validate against.</param>
    /// <exception cref="SquadSourceValidationException">
    ///     Thrown when <paramref name="preset" /> references a profile category not declared in <paramref name="bundled" />.
    /// </exception>
    public static void Validate(SquadModelPreset preset, SquadModelProfiles bundled)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(bundled);

        foreach (string category in preset.Profiles.Keys)
            if (!bundled.Profiles.ContainsKey(category))
            {
                int startLine = 1;
                if (File.Exists(preset.SourcePath))
                {
                    string[] lines = File.ReadAllLines(preset.SourcePath);
                    for (int i = 0; i < lines.Length; i++)
                        if (lines[i].Contains(category, StringComparison.Ordinal))
                        {
                            startLine = i + 1;
                            break;
                        }
                }

                SquadSourceValidator.Throw(
                    $"Preset '{preset.Name}' references unknown model profile category 'profiles.{category}'.",
                    $"profiles.{category}",
                    preset.SourcePath,
                    "Use a profile category declared in profiles/models.yml.",
                    startLine);
            }
    }

    /// <summary>
    ///     Validates that all profile categories referenced by <paramref name="preset" /> exist in <paramref name="source" />.
    /// </summary>
    /// <param name="preset">The model preset to validate.</param>
    /// <param name="source">The canonical Squad source containing bundled model profiles.</param>
    /// <exception cref="SquadSourceValidationException">
    ///     Thrown when <paramref name="preset" /> references a profile category not declared in <paramref name="source" />.
    /// </exception>
    public static void Validate(SquadModelPreset preset, SquadSource source)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(source);

        Validate(preset, source.ModelProfiles);
    }
}
