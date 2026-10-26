using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Squad.Model;

/// <summary>
///     Recorded model preset selection for a specific project root.
/// </summary>
/// <param name="Root">The canonical physical root path of the project.</param>
/// <param name="ModelPresets">The mapping of deployment targets to preset names.</param>
public sealed record SquadModelPresetSelection(
    string Root,
    IReadOnlyDictionary<SquadTarget, string> ModelPresets);

/// <summary>
///     File-system store for project-level model preset selections, keyed by physical canonical root.
/// </summary>
/// <remarks>
///     Project model preset selections are stored exclusively under the user's home directory
///     (<c>~/.kyber-weave/squad/selections/</c>) rather than inside the project tree. This preserves
///     strict project isolation and prevents uncommitted artifacts from appearing in working copies.
/// </remarks>
public sealed partial class SquadModelPresetSelectionStore
{
    /// <summary>
    ///     The YAML schema version identifier for project model preset selections.
    /// </summary>
    public const string Schema = "kyber-squad.project-selection/v1";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex PresetNameRegex();

    private static readonly IReadOnlyDictionary<string, SquadTarget> TargetsByCanonicalToken =
        SquadTargetCatalog.All.ToDictionary(SquadTargetCatalog.GetToken, static t => t, StringComparer.Ordinal);

    private readonly string _selectionsDirectory;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SquadModelPresetSelectionStore" /> class.
    /// </summary>
    /// <param name="userPaths">The user paths provider defining the home directory anchor.</param>
    public SquadModelPresetSelectionStore(ISquadUserPaths userPaths)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPaths.HomeDirectory);
        _selectionsDirectory = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "selections");
    }

    /// <summary>
    ///     Reads the stored model preset selection for the specified project root, or <see langword="null" /> if absent.
    /// </summary>
    /// <param name="projectRoot">The project root directory.</param>
    /// <returns>The stored preset selection, or <see langword="null" /> if no selection file exists or has targets.</returns>
    public SquadModelPresetSelection? Read(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(projectRoot);
        string filePath = Path.Combine(_selectionsDirectory, $"{identity.Key}.yml");
        if (!File.Exists(filePath)) return null;

        try
        {
            string text = File.ReadAllText(filePath, Encoding.UTF8);
            YamlStream stream = new();
            using (StringReader reader = new(text))
            {
                stream.Load(reader);
            }

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode rootNode)
                return null;

            if (!rootNode.Children.TryGetValue(new YamlScalarNode("schema"), out YamlNode? schemaNode) ||
                schemaNode is not YamlScalarNode schemaScalar ||
                !string.Equals(schemaScalar.Value, Schema, StringComparison.Ordinal))
                return null;

            string root = identity.PhysicalPath;
            if (rootNode.Children.TryGetValue(new YamlScalarNode("root"), out YamlNode? rootNodeVal) &&
                rootNodeVal is YamlScalarNode rootScalar &&
                !string.IsNullOrWhiteSpace(rootScalar.Value))
                root = rootScalar.Value;

            Dictionary<SquadTarget, string> presets = new();
            if (rootNode.Children.TryGetValue(new YamlScalarNode("model-presets"), out YamlNode? presetsNode) &&
                presetsNode is YamlMappingNode presetsMapping)
                foreach (KeyValuePair<YamlNode, YamlNode> child in presetsMapping.Children)
                    if (child.Key is YamlScalarNode { Value: not null } keyScalar &&
                        child.Value is YamlScalarNode { Value: not null } valueScalar &&
                        TryGetTarget(keyScalar.Value, out SquadTarget target))
                        presets[target] = valueScalar.Value;

            if (presets.Count == 0) return null;

            return new SquadModelPresetSelection(root, presets);
        }
        catch (Exception exception) when (exception is YamlException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"Squad project selection YAML at '{filePath}' is invalid: {exception.Message}",
                exception);
        }
    }

    /// <summary>
    ///     Sets or updates the model preset selection for a specific target in the given project root.
    /// </summary>
    /// <param name="projectRoot">The project root directory.</param>
    /// <param name="target">The deployment target harness.</param>
    /// <param name="presetName">The preset name to configure.</param>
    public void Set(string projectRoot, SquadTarget target, string presetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(presetName);
        if (!PresetNameRegex().IsMatch(presetName))
            throw new ArgumentException(
                $"Invalid preset name '{presetName}'. Preset names must match '^[a-z0-9][a-z0-9-]{{0,63}}$'.",
                nameof(presetName));

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(projectRoot);
        SquadModelPresetSelection? existing = Read(projectRoot);

        Dictionary<SquadTarget, string> presets = existing is not null
            ? new Dictionary<SquadTarget, string>(existing.ModelPresets)
            : new Dictionary<SquadTarget, string>();

        presets[target] = presetName;
        WriteSelection(identity, presets);
    }

    /// <summary>
    ///     Clears the model preset selection for a specific target in the given project root.
    ///     If no targets remain configured, the selection file is deleted.
    /// </summary>
    /// <param name="projectRoot">The project root directory.</param>
    /// <param name="target">The deployment target to remove.</param>
    public void Clear(string projectRoot, SquadTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(projectRoot);
        SquadModelPresetSelection? existing = Read(projectRoot);
        if (existing is null) return;

        Dictionary<SquadTarget, string> presets = new(existing.ModelPresets);
        presets.Remove(target);

        string targetPath = Path.Combine(_selectionsDirectory, $"{identity.Key}.yml");
        if (presets.Count == 0)
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
        }
        else
        {
            WriteSelection(identity, presets);
        }
    }

    /// <summary>
    ///     Clears all model preset selections for the given project root by deleting its selection file.
    /// </summary>
    /// <param name="projectRoot">The project root directory.</param>
    public void Clear(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(projectRoot);
        string targetPath = Path.Combine(_selectionsDirectory, $"{identity.Key}.yml");
        if (File.Exists(targetPath)) File.Delete(targetPath);
    }

    private static string FormatYamlValue(string value)
    {
        if (NeedsQuoting(value))
        {
            return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
        }

        return value;
    }

    private static bool NeedsQuoting(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        if (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1])) return true;
        return s.IndexOfAny(['#', ':', '\'', '"', '\n', '\r', '\t', '`', '*', '&', '!', '|', '>', '%', '@', '[', ']', '{', '}']) >= 0;
    }

    private void WriteSelection(SquadPhysicalRootIdentity identity, IReadOnlyDictionary<SquadTarget, string> presets)
    {
        Directory.CreateDirectory(_selectionsDirectory);
        string targetPath = Path.Combine(_selectionsDirectory, $"{identity.Key}.yml");
        string temporaryPath = Path.Combine(_selectionsDirectory, $"{identity.Key}.{Guid.NewGuid():N}.tmp");

        StringBuilder builder = new();
        builder.Append("schema: ");
        builder.Append(Schema);
        builder.Append('\n');
        builder.Append("root: ");
        builder.Append(FormatYamlValue(identity.PhysicalPath));
        builder.Append('\n');
        builder.Append("model-presets:\n");

        foreach (KeyValuePair<SquadTarget, string> entry in presets.OrderBy(
                     static kvp => SquadTargetCatalog.GetToken(kvp.Key), StringComparer.Ordinal))
        {
            builder.Append("  ");
            builder.Append(SquadTargetCatalog.GetToken(entry.Key));
            builder.Append(": ");
            builder.Append(FormatYamlValue(entry.Value));
            builder.Append('\n');
        }

        string text = builder.ToString();
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporaryPath, targetPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool TryGetTarget(string token, out SquadTarget target)
    {
        if (TargetsByCanonicalToken.TryGetValue(token, out target)) return true;

        try
        {
            IReadOnlyList<SquadTarget> parsed = SquadTargetCatalog.Parse([token]);
            if (parsed.Count == 1)
            {
                target = parsed[0];
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        target = default;
        return false;
    }
}
