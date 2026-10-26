using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Squad.Model;

/// <summary>A version-keyed snapshot of the canonical Squad model profiles and agent roster.</summary>
/// <param name="SquadVersion">The Squad release version the snapshot was rendered from, without a leading <c>v</c>.</param>
/// <param name="Profiles">The canonical model profiles, mirroring the <c>models.yml</c> shape.</param>
/// <param name="AgentProfiles">The canonical agent name to profile category map.</param>
/// <remarks>
/// The snapshot lets the preset verbs resolve canonical profiles on an installed CLI, where no
/// checkout exists to load <c>products/kyber-squad</c> from. Install and update persist it after a
/// committed transaction; the preset verbs read it back without ever downloading a release.
/// </remarks>
public sealed record SquadCanonicalProfiles(
    string SquadVersion,
    SquadModelProfiles Profiles,
    IReadOnlyDictionary<string, string> AgentProfiles);

/// <summary>Persists one canonical profile snapshot per Squad release version under the home Squad state.</summary>
/// <remarks>
/// Snapshots live at <c>&lt;home&gt;/.kyber-weave/squad/profiles/&lt;version&gt;.yml</c>, derived from
/// <see cref="ISquadUserPaths.HomeDirectory"/> the same way <see cref="SquadModelPresetStore"/>
/// derives its presets directory, so both stay colocated under the home Squad root the state store owns.
/// Writes go through a temporary file beside the target and rename it into place, the
/// SquadStateMigration.WriteAtomically pattern, so a crash never leaves a half-written snapshot behind.
/// A plain flush is enough here: unlike a lock or receipt, a lost snapshot is rebuilt by the next
/// squad update rather than by journal recovery. Reads never throw for file damage: a missing,
/// unreadable, or corrupt snapshot counts as absent (null) so the preset verbs can fail closed with
/// the update remedy. Only caller bugs, a null snapshot or a version that escapes the profiles
/// directory, throw. TryReadLatest orders by last write time because release versions carry
/// pre-release suffixes no numeric comparison understands; install and update always rewrite the
/// snapshot they render from, so the most recently written file is the most recently installed release.
/// </remarks>
public sealed class SquadCanonicalProfileStore
{
    /// <summary>The schema identifier written into every snapshot file.</summary>
    public const string Schema = "kyber-squad.canonical-profiles/v1";

    /// <summary>The models document schema a snapshot's profiles section mirrors.</summary>
    /// <remarks>
    /// The loader keeps its copy private, so this repeats the literal instead of reaching past that
    /// encapsulation. Both name a versioned contract that only changes with a schema bump, which would
    /// be a deliberate cross-file edit anyway.
    /// </remarks>
    private const string ModelProfilesSchema = "kyber-squad.model-profiles/v1";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding PlainUtf8 = new(false);

    private readonly string _profilesDirectory;

    /// <summary>Creates a snapshot store over the given user paths.</summary>
    /// <param name="userPaths">The user paths provider.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="userPaths"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the home directory is empty.</exception>
    public SquadCanonicalProfileStore(ISquadUserPaths userPaths)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPaths.HomeDirectory);
        _profilesDirectory = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "profiles");
    }

    /// <summary>Persists a snapshot, replacing any snapshot already stored for its Squad version.</summary>
    /// <param name="snapshot">The snapshot to persist; its Squad version selects the file.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the snapshot version is not a valid snapshot name.</exception>
    /// <exception cref="InvalidDataException">Thrown when the snapshot content cannot round-trip.</exception>
    public void Write(SquadCanonicalProfiles snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Profiles);
        ArgumentNullException.ThrowIfNull(snapshot.AgentProfiles);
        RequireVersion(snapshot.SquadVersion, nameof(snapshot));
        ValidateSnapshot(snapshot);
        WriteAtomically(SnapshotPath(snapshot.SquadVersion), PlainUtf8.GetBytes(Serialize(snapshot)));
    }

    /// <summary>Reads the snapshot stored for one Squad release version.</summary>
    /// <param name="version">The Squad release version, without a leading <c>v</c>.</param>
    /// <returns>The snapshot, or null when no readable snapshot exists for the version.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="version"/> is not a valid snapshot name.</exception>
    public SquadCanonicalProfiles? TryRead(string version)
    {
        string validated = RequireVersion(version, nameof(version));
        string path = SnapshotPath(validated);
        try
        {
            if (!File.Exists(path))
                return null;
            string content = StrictUtf8.GetString(File.ReadAllBytes(path));
            if (content.Length > 0 && content[0] == '\uFEFF')
                content = content[1..];
            return ParseSnapshot(content, validated, path);
        }
        catch (Exception exception) when (exception is IOException or
                                              UnauthorizedAccessException or
                                              YamlException or
                                              InvalidDataException or
                                              FormatException or
                                              DecoderFallbackException or
                                              ArgumentException)
        {
            // Every failure here means the snapshot is unusable: missing or denied files, broken
            // UTF-8, YAML the composer rejects (duplicate keys surface as ArgumentException), or a
            // schema violation the parser reports. All count as absent so callers fail closed with
            // the update remedy instead of throwing over home-state damage.
            return null;
        }
    }

    /// <summary>Reads the most recently written snapshot.</summary>
    /// <returns>The newest readable snapshot, or null when none exists.</returns>
    public SquadCanonicalProfiles? TryReadLatest()
    {
        List<string> stems = ListSnapshotVersions();
        SquadCanonicalProfiles? latest = null;
        DateTime latestWrite = DateTime.MinValue;
        string latestStem = string.Empty;
        foreach (string stem in stems)
        {
            SquadCanonicalProfiles? candidate;
            DateTime write;
            try
            {
                candidate = TryRead(stem);
                if (candidate is null)
                    continue;
                write = File.GetLastWriteTimeUtc(SnapshotPath(stem));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The profiles directory changed under the enumeration; the entry counts as absent.
                continue;
            }

            if (latest is null ||
                write > latestWrite ||
                (write == latestWrite && CompareVersionStems(stem, latestStem) > 0))
            {
                latest = candidate;
                latestWrite = write;
                latestStem = stem;
            }
        }

        return latest;
    }

    private static void ValidateSnapshot(SquadCanonicalProfiles snapshot)
    {
        foreach (KeyValuePair<string, SquadModelProfile> category in snapshot.Profiles.Profiles)
        {
            if (string.IsNullOrWhiteSpace(category.Key))
                throw new InvalidDataException("Squad snapshot profile categories must have non-empty names.");
            if (category.Value is null)
                throw new InvalidDataException(
                    $"Squad snapshot profile category '{category.Key}' must not be null.");
            if (string.IsNullOrWhiteSpace(category.Value.Default))
                throw new InvalidDataException(
                    $"Squad snapshot profile category '{category.Key}' must declare a 'default' model, mirroring models.yml.");
        }

        foreach (KeyValuePair<string, string> agent in snapshot.AgentProfiles)
        {
            if (string.IsNullOrWhiteSpace(agent.Key))
                throw new InvalidDataException("Squad snapshot agent names must be non-empty.");
            if (string.IsNullOrWhiteSpace(agent.Value))
                throw new InvalidDataException(
                    $"Squad snapshot agent '{agent.Key}' must reference a profile category.");
            if (!snapshot.Profiles.Profiles.ContainsKey(agent.Value))
                throw new InvalidDataException(
                    $"Squad snapshot agent '{agent.Key}' references unknown profile category '{agent.Value}'. " +
                    "Rebuild the snapshot from the Squad source it was rendered from.");
        }
    }

    private static string Serialize(SquadCanonicalProfiles snapshot)
    {
        StringBuilder yaml = new();
        AppendScalar(yaml, "schema", Schema, 0);
        AppendScalar(yaml, "squad-version", snapshot.SquadVersion, 0);
        yaml.Append("profiles:");
        if (snapshot.Profiles.Profiles.Count == 0)
        {
            yaml.Append(" {}\n");
        }
        else
        {
            yaml.Append('\n');
            foreach (string category in snapshot.Profiles.Profiles.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                SquadModelProfile profile = snapshot.Profiles.Profiles[category];
                yaml.Append("  ").Append(ToYamlScalar(category)).Append(":\n");
                AppendScalar(yaml, "default", profile.Default, 4);
                foreach (string harness in profile.HarnessModels.Keys.OrderBy(key => key, StringComparer.Ordinal))
                    AppendScalar(yaml, harness, profile.HarnessModels[harness], 4);
            }
        }

        yaml.Append("agents:");
        if (snapshot.AgentProfiles.Count == 0)
        {
            yaml.Append(" {}\n");
        }
        else
        {
            yaml.Append('\n');
            foreach (string agent in snapshot.AgentProfiles.Keys.OrderBy(key => key, StringComparer.Ordinal))
                AppendScalar(yaml, agent, snapshot.AgentProfiles[agent], 2);
        }

        return yaml.ToString();
    }

    private static SquadCanonicalProfiles ParseSnapshot(string content, string version, string path)
    {
        RejectYamlIndirection(content);
        YamlStream stream = new();
        stream.Load(new StringReader(content));
        if (stream.Documents.Count != 1 ||
            stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException(
                $"Squad snapshot '{path}' must contain exactly one mapping document.");

        RequireExactFields(root, ["schema", "squad-version", "profiles", "agents"], path);

        string schema = RequireScalar(root, "schema", path);
        if (!string.Equals(schema, Schema, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Squad snapshot '{path}' schema '{schema}' must be '{Schema}'. " +
                "Restore the snapshot with 'squad update'.");

        string embeddedVersion = RequireScalar(root, "squad-version", path);
        if (!string.Equals(embeddedVersion, version, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Squad snapshot '{path}' records squad-version '{embeddedVersion}' but is stored as '{version}'. " +
                "Restore the snapshot with 'squad update' rather than renaming snapshot files.");

        SortedDictionary<string, SquadModelProfile> profiles =
            ParseProfiles(RequireMapping(root, "profiles", path), path);
        Dictionary<string, string> agents = ParseAgents(RequireMapping(root, "agents", path), profiles, path);

        return new SquadCanonicalProfiles(
            embeddedVersion,
            new SquadModelProfiles(ModelProfilesSchema, profiles, path),
            agents);
    }

    private static SortedDictionary<string, SquadModelProfile> ParseProfiles(YamlMappingNode profilesNode, string path)
    {
        SortedDictionary<string, SquadModelProfile> profiles = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in profilesNode.Children)
        {
            string category = RequireKey(pair.Key, "profiles", path);
            if (!seen.Add(category))
                throw new InvalidDataException(
                    $"Squad snapshot '{path}' declares profile category '{category}' more than once.");
            profiles.Add(category, ParseProfile(category, pair.Value, path));
        }

        return profiles;
    }

    private static SquadModelProfile ParseProfile(string category, YamlNode node, string path)
    {
        if (node is not YamlMappingNode mapping)
            throw new InvalidDataException(
                $"Squad snapshot '{path}' profile category '{category}' must be a mapping.");

        string? defaultModel = null;
        SortedDictionary<string, string> harnessModels = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            string key = RequireKey(pair.Key, $"profiles.{category}", path);
            if (!SquadSourceLoader.ModelProfileFields.Contains(key))
                throw new InvalidDataException(
                    $"Squad snapshot '{path}' profile category '{category}' uses unknown harness '{key}'. " +
                    "Align the snapshot with the models.yml harness vocabulary.");
            if (!seen.Add(key))
                throw new InvalidDataException(
                    $"Squad snapshot '{path}' profile category '{category}' declares '{key}' more than once.");
            string value = RequireScalarValue(pair.Value, $"profiles.{category}.{key}", path);
            if (string.Equals(key, "default", StringComparison.Ordinal))
                defaultModel = value;
            else
                harnessModels.Add(key, value);
        }

        if (defaultModel is null)
            throw new InvalidDataException(
                $"Squad snapshot '{path}' profile category '{category}' must declare a 'default' model, mirroring models.yml.");

        return new SquadModelProfile(defaultModel, harnessModels);
    }

    private static Dictionary<string, string> ParseAgents(
        YamlMappingNode agentsNode,
        IReadOnlyDictionary<string, SquadModelProfile> profiles,
        string path)
    {
        Dictionary<string, string> agents = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in agentsNode.Children)
        {
            string name = RequireKey(pair.Key, "agents", path);
            if (!seen.Add(name))
                throw new InvalidDataException(
                    $"Squad snapshot '{path}' declares agent '{name}' more than once.");
            string category = RequireScalarValue(pair.Value, $"agents.{name}", path);
            if (!profiles.ContainsKey(category))
                throw new InvalidDataException(
                    $"Squad snapshot '{path}' maps agent '{name}' to unknown profile category '{category}'. " +
                    "Restore the snapshot with 'squad update'.");
            agents.Add(name, category);
        }

        return agents;
    }

    private static void RejectYamlIndirection(string yaml)
    {
        if (yaml.Contains('&', StringComparison.Ordinal) ||
            yaml.Contains('*', StringComparison.Ordinal) ||
            yaml.Contains("<<:", StringComparison.Ordinal) ||
            yaml.Contains("!!", StringComparison.Ordinal))
            throw new InvalidDataException(
                "Squad snapshot YAML does not allow anchors, aliases, merge keys, or custom tags.");
    }

    private static void RequireExactFields(YamlMappingNode mapping, IReadOnlyCollection<string> expected, string path)
    {
        HashSet<string> actual = new(StringComparer.Ordinal);
        foreach (YamlNode keyNode in mapping.Children.Keys)
            if (keyNode is not YamlScalarNode { Value: not null } key ||
                !actual.Add(key.Value))
                throw new InvalidDataException(
                    $"Squad snapshot '{path}' contains a duplicate or non-scalar field.");

        string[] missing = expected.Where(field => !actual.Contains(field)).ToArray();
        string[] unknown = actual.Where(field => !expected.Contains(field)).ToArray();
        if (missing.Length > 0 || unknown.Length > 0)
            throw new InvalidDataException(
                $"Squad snapshot '{path}' has missing or unknown fields. " +
                $"Missing: {string.Join(", ", missing)}; unknown: {string.Join(", ", unknown)}.");
    }

    private static YamlNode RequireNode(YamlMappingNode mapping, string field, string path)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(field), out YamlNode? node))
            throw new InvalidDataException($"Squad snapshot '{path}' is missing field '{field}'.");
        return node;
    }

    private static string RequireScalar(YamlMappingNode mapping, string field, string path)
    {
        return RequireScalarValue(RequireNode(mapping, field, path), field, path);
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode mapping, string field, string path)
    {
        if (RequireNode(mapping, field, path) is not YamlMappingNode section)
            throw new InvalidDataException(
                $"Squad snapshot '{path}' field '{field}' must be a mapping.");
        return section;
    }

    private static string RequireKey(YamlNode node, string section, string path)
    {
        if (node is not YamlScalarNode { Value: string key } || string.IsNullOrWhiteSpace(key))
            throw new InvalidDataException(
                $"Squad snapshot '{path}' section '{section}' must use non-empty string keys.");
        return key;
    }

    private static string RequireScalarValue(YamlNode node, string field, string path)
    {
        if (node is not YamlScalarNode { Value: string value } ||
            string.IsNullOrWhiteSpace(value) ||
            value is "null" or "Null" or "NULL" or "~")
            throw new InvalidDataException(
                $"Squad snapshot '{path}' field '{field}' must be a non-empty string.");
        return value;
    }

    private static void AppendScalar(StringBuilder yaml, string name, string value, int indentation)
    {
        yaml.Append(' ', indentation);
        yaml.Append(ToYamlScalar(name));
        yaml.Append(": ");
        yaml.Append(ToYamlScalar(value));
        yaml.Append('\n');
    }

    private static string ToYamlScalar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0 &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/') &&
            value is not "null" and not "Null" and not "NULL" and
                not "true" and not "True" and not "TRUE" and
                not "false" and not "False" and not "FALSE" and not "~")
            return value;

        // A JSON double-quoted string is also a valid YAML double-quoted scalar, so values with
        // spaces, brackets, or parentheses stay exact without a hand-rolled quoting rule. This is
        // the same plain-scalar rule SquadStateStore applies to lock values.
        return JsonSerializer.Serialize(value);
    }

    private static bool IsSnapshotVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;
        if (string.Equals(version, ".", StringComparison.Ordinal) ||
            string.Equals(version, "..", StringComparison.Ordinal))
            return false;
        if (!string.Equals(version, Path.GetFileName(version), StringComparison.Ordinal))
            return false;
        return version.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static string RequireVersion(string version, string parameterName)
    {
        if (!IsSnapshotVersion(version))
            throw new ArgumentException(
                $"Squad version '{version}' is not a valid snapshot name. " +
                "Use the release version without a leading 'v', for example '1.2.3'.",
                parameterName);
        return version;
    }

    private string SnapshotPath(string version)
    {
        return Path.Combine(_profilesDirectory, $"{version}.yml");
    }

    /// <summary>Compares snapshot version stems so a tied write time still prefers the newer release.</summary>
    /// <remarks>
    ///     Release versions sort numerically per dot-separated component, not lexically: a plain
    ///     ordinal tie-break crowns 1.9.0 over 1.10.0 because '9' sorts after '1', and show would
    ///     then display the wrong release's models. Digit runs compare by length then ordinally
    ///     (no parsing, so no overflow on hostile stems); anything else compares ordinally, and a
    ///     stem with more components sorts after its own prefix. The order is total either way.
    /// </remarks>
    private static int CompareVersionStems(string left, string right)
    {
        string[] leftParts = left.Split('.');
        string[] rightParts = right.Split('.');
        int shared = Math.Min(leftParts.Length, rightParts.Length);
        for (int index = 0; index < shared; index++)
        {
            int leftDigits = CountLeadingDigits(leftParts[index]);
            int rightDigits = CountLeadingDigits(rightParts[index]);
            int compared = leftDigits.CompareTo(rightDigits);
            if (compared != 0)
                return compared;
            compared = string.Compare(
                leftParts[index], 0,
                rightParts[index], 0,
                Math.Max(leftDigits, rightDigits),
                StringComparison.Ordinal);
            if (compared != 0)
                return compared;
            compared = leftParts[index].AsSpan(leftDigits).CompareTo(
                rightParts[index].AsSpan(rightDigits),
                StringComparison.Ordinal);
            if (compared != 0)
                return compared;
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CountLeadingDigits(string component)
    {
        int digits = 0;
        while (digits < component.Length && char.IsAsciiDigit(component[digits]))
            digits++;
        return digits;
    }

    private List<string> ListSnapshotVersions()
    {
        List<string> stems = new();
        try
        {
            if (!Directory.Exists(_profilesDirectory))
                return [];
            DirectoryInfo directory = new(_profilesDirectory);
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos(
                         "*.yml",
                         new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
            {
                // A symlink inside the profiles directory is never followed: only real files
                // written by Write are candidates, mirroring SquadModelPresetStore.List.
                if (entry is FileInfo &&
                    entry.LinkTarget is null &&
                    string.Equals(Path.GetExtension(entry.Name), ".yml", StringComparison.OrdinalIgnoreCase))
                {
                    string stem = Path.GetFileNameWithoutExtension(entry.Name);
                    if (IsSnapshotVersion(stem))
                        stems.Add(stem);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The home state vanished mid-enumeration; there is nothing readable to report.
            return [];
        }

        return stems;
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException($"Squad snapshot path '{path}' has no directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush();
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
