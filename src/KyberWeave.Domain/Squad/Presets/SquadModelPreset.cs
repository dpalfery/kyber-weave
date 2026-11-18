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

}
