
namespace KyberWeave.Core.Squad.Model;

/// <summary>Merges a model preset's profile overrides into the bundled model profiles.</summary>
public static class SquadModelPresetExtensions
{
    /// <summary>
    ///     Applies the preset's profile overrides to the bundled model profiles.
    /// </summary>
    /// <param name="bundled">The bundled base model profiles.</param>
    /// <returns>A merged <see cref="SquadModelProfiles" /> instance.</returns>
    /// <remarks>
    ///     Key-by-key merge per category. Categories omitted by the preset remain unchanged from bundled.
    ///     For categories present in the preset, the preset default takes precedence if specified, and
    ///     preset harness models override or augment bundled harness models.
    /// </remarks>
    /// <param name="preset">The preset whose overrides apply.</param>
    public static SquadModelProfiles ApplyTo(this SquadModelPreset preset, SquadModelProfiles bundled)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(bundled);

        Dictionary<string, SquadModelProfile> merged = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, SquadModelProfile> kvp in bundled.Profiles)
        {
            if (!preset.Profiles.TryGetValue(kvp.Key, out SquadModelProfile? presetProfile))
            {
                merged[kvp.Key] = kvp.Value;
                continue;
            }

            string? mergedDefault = presetProfile.Default ?? kvp.Value.Default;
            if (string.IsNullOrWhiteSpace(mergedDefault))
                throw new InvalidOperationException(
                    $"Profile category '{kvp.Key}' has no default model configured in either preset or bundled profiles.");

            Dictionary<string, string> mergedHarnesses = new(kvp.Value.HarnessModels, StringComparer.Ordinal);

            foreach (KeyValuePair<string, string> harness in presetProfile.HarnessModels)
                mergedHarnesses[harness.Key] = harness.Value;

            merged[kvp.Key] = new SquadModelProfile(mergedDefault, mergedHarnesses);
        }

        foreach (KeyValuePair<string, SquadModelProfile> kvp in preset.Profiles)
            if (!merged.ContainsKey(kvp.Key))
                merged[kvp.Key] = kvp.Value;

        return new SquadModelProfiles(bundled.Schema, merged, bundled.SourcePath);
    }
}
