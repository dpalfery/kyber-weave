using KyberWeave.Contracts.Repositories;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Core.Squad.Model;

/// <summary>
/// File-system store for user-configured Squad model presets.
/// </summary>
public sealed partial class SquadModelPresetStore : ISquadModelPresetStore
{
    private readonly IFileSystem _files;
    private readonly string _presetsDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SquadModelPresetStore"/> class.
    /// </summary>
    /// <param name="userPaths">The user paths provider.</param>
    /// <param name="files">The file system the presets are read from.</param>
    public SquadModelPresetStore(ISquadUserPaths userPaths, IFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        ArgumentException.ThrowIfNullOrWhiteSpace(userPaths.HomeDirectory);
        _presetsDirectory = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
    }

    /// <summary>
    /// Lists the names of all valid preset files stored in the presets directory, sorted ordinally.
    /// </summary>
    /// <returns>A list of preset names (file stems).</returns>
    public IReadOnlyList<string> List()
    {
        if (!_files.DirectoryExists(_presetsDirectory))
        {
            return [];
        }

        List<string> stems = new();

        foreach (FileSystemEntry entry in _files.EnumerateEntries(_presetsDirectory))
        {
            string name = Path.GetFileName(entry.Path);
            if (!entry.IsDirectory &&
                !entry.IsLink &&
                string.Equals(Path.GetExtension(name), ".yml", StringComparison.OrdinalIgnoreCase))
            {
                stems.Add(Path.GetFileNameWithoutExtension(name));
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
        if (!_files.FileExists(filePath))
        {
            return null;
        }

        byte[] bytes = _files.ReadAllBytes(filePath);
        return SquadSourceLoader.ParseModelPreset(filePath, bytes);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ValidNameRegex();
}
