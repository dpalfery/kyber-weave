using YamlDotNet.Serialization;

namespace KyberWeave.Core.Skills.Model;

/// <summary>
/// The YAML front matter of a SKILL.md file, per the Agent Skills open format
/// (https://agentskills.io/specification). Field names mirror the spec; property
/// names follow .NET conventions and are mapped via [YamlMember].
/// </summary>
public sealed class SkillFrontmatter
{
    private static readonly char[] AllowedToolsSeparators = [' ', '\t', ','];

    /// <summary>Required. Lowercase a-z, 0-9 and hyphens; must match the parent directory name.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Required. What the skill does AND when to use it. This is the routing signal.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>Optional. SPDX license identifier.</summary>
    [YamlMember(Alias = "license")]
    public string? License { get; set; }

    /// <summary>Optional. Free-text compatibility note (recommended max 500 chars).</summary>
    [YamlMember(Alias = "compatibility")]
    public string? Compatibility { get; set; }

    /// <summary>Optional. Free-form string-to-string metadata (author, version, etc.).</summary>
    [YamlMember(Alias = "metadata")]
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// Optional, EXPERIMENTAL. Space-separated list of tools the skill is allowed to use.
    /// Not a security control — runtimes are not required to enforce it.
    /// </summary>
    [YamlMember(Alias = "allowed-tools")]
    public string? AllowedToolsRaw { get; set; }

    /// <summary>Parsed view of <see cref="AllowedToolsRaw"/>.</summary>
    [YamlIgnore]
    public IReadOnlyList<string> AllowedTools =>
        string.IsNullOrWhiteSpace(AllowedToolsRaw)
            ? Array.Empty<string>()
            : AllowedToolsRaw.Split(AllowedToolsSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Captures any front matter keys not part of the spec. Compliant runtimes ignore
    /// unrecognized keys, but the linter surfaces them as a warning.
    /// </summary>
    [YamlIgnore]
    public Dictionary<string, string> UnknownKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
}
