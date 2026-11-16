namespace KyberWeave.Core.Squad.Model;

/// <summary>
///     A named model preset containing profile overrides.
/// </summary>
/// <param name="Name">The preset name (derived from file stem).</param>
/// <param name="SourcePath">The source file path of the preset.</param>
/// <param name="Digest">The lowercase SHA-256 hex digest of the preset file content.</param>
/// <param name="Profiles">The mapping of profile category names to model profiles.</param>
public sealed record SquadModelPreset(
    string Name,
    string SourcePath,
    string Digest,
    IReadOnlyDictionary<string, SquadModelProfile> Profiles)
{
    /// <summary>
    ///     Constructs a preset from a base <see cref="SquadModelProfiles" /> instance.
    /// </summary>
    public SquadModelPreset(string name, string sourcePath, string digest, SquadModelProfiles profiles)
        : this(name, sourcePath, digest, profiles?.Profiles ?? throw new ArgumentNullException(nameof(profiles)))
    {
    }

    /// <summary>
    ///     Applies this preset's profile overrides to the bundled model profiles.
    /// </summary>
    /// <param name="bundled">The bundled base model profiles.</param>
    /// <returns>A merged <see cref="SquadModelProfiles" /> instance.</returns>
    /// <remarks>
    ///     Key-by-key merge per category. Categories omitted by the preset remain unchanged from bundled.
    ///     For categories present in the preset, the preset default takes precedence if specified, and
    ///     preset harness models override or augment bundled harness models.
    /// </remarks>
    public SquadModelProfiles ApplyTo(SquadModelProfiles bundled)
    {
        ArgumentNullException.ThrowIfNull(bundled);

        Dictionary<string, SquadModelProfile> merged = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, SquadModelProfile> kvp in bundled.Profiles)
        {
            if (!Profiles.TryGetValue(kvp.Key, out SquadModelProfile? presetProfile))
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

        foreach (KeyValuePair<string, SquadModelProfile> kvp in Profiles)
            if (!merged.ContainsKey(kvp.Key))
                merged[kvp.Key] = kvp.Value;

        return new SquadModelProfiles(bundled.Schema, merged, bundled.SourcePath);
    }
}
