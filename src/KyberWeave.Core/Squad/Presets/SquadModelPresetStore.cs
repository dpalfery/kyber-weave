using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Parsing;

namespace KyberWeave.Core.Squad.Model;

/// <summary>
/// File-system store for user-configured Squad model presets.
/// </summary>
public sealed partial class SquadModelPresetStore
{
    private readonly string _presetsDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SquadModelPresetStore"/> class.
    /// </summary>
    /// <param name="userPaths">The user paths provider.</param>
    public SquadModelPresetStore(ISquadUserPaths userPaths)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPaths.HomeDirectory);
        _presetsDirectory = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
    }

    /// <summary>
    /// Lists the names of all valid preset files stored in the presets directory, sorted ordinally.
    /// </summary>
    /// <returns>A list of preset names (file stems).</returns>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_presetsDirectory))
        {
            return [];
        }

        DirectoryInfo directoryInfo = new(_presetsDirectory);
        List<string> stems = new();

        foreach (FileSystemInfo entry in directoryInfo.EnumerateFileSystemInfos("*.yml",
                     new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
        {
            if (entry is FileInfo &&
                entry.LinkTarget is null &&
                string.Equals(Path.GetExtension(entry.Name), ".yml", StringComparison.OrdinalIgnoreCase))
            {
                stems.Add(Path.GetFileNameWithoutExtension(entry.Name));
            }
        }

        stems.Sort(StringComparer.Ordinal);
        return stems;
    }

    /// <summary>
    /// Attempts to load and parse a model preset by name.
    /// </summary>
    /// <param name="name">The preset name (stem).</param>
    /// <returns>The parsed preset, or <c>null</c> if the preset file does not exist.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is invalid.</exception>
    public SquadModelPreset? TryLoad(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !ValidNameRegex().IsMatch(name))
        {
            throw new ArgumentException(
                $"Preset name '{name}' is invalid. Preset names must match '^[a-z0-9][a-z0-9-]{{0,63}}$'.",
                nameof(name));
        }

        string filePath = Path.Combine(_presetsDirectory, $"{name}.yml");
        if (!File.Exists(filePath))
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(filePath);
        return SquadSourceLoader.ParseModelPreset(filePath, bytes);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ValidNameRegex();
}
