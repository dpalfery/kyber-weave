using KyberWeave.Core.Agents.Model;
using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads harness profile overrides from <c>kyber-weave.yml</c>.</summary>
public static class HarnessProfileConfigLoader
{
    public static HarnessProfileConfig Load(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);
        KyberWeaveYamlDocument document = KyberWeaveYamlParser.ParseFile(yamlPath);
        return Merge(HarnessProfileConfig.ProductDefaults, document.Harness);
    }

    internal static HarnessProfileConfig Merge(HarnessProfileConfig defaults, HarnessYamlSection? section)
    {
        if (section?.Profiles is null || section.Profiles.Count == 0)
            return defaults;

        Dictionary<HarnessKind, HarnessCapabilityProfile> merged = new Dictionary<HarnessKind, HarnessCapabilityProfile>();
        foreach ((HarnessKind kind, HarnessCapabilityProfile profile) in defaults.Profiles)
            merged[kind] = CloneProfile(profile);

        foreach ((string name, HarnessProfileYamlSection? overrideSection) in section.Profiles)
        {
            if (!TryParseHarnessKind(name, out HarnessKind kind))
            {
                throw new YamlException(
                    $"Unknown harness.profiles name '{name}'. " +
                    "Known profiles: codex, cursor, claude, githubcopilot, opencode, kilo.");
            }

            if (!merged.TryGetValue(kind, out HarnessCapabilityProfile? existing))
            {
                existing = new HarnessCapabilityProfile
                {
                    Harness = kind,
                    DirectoryName = $".{name}/agents"
                };
            }

            merged[kind] = ApplyOverride(existing, overrideSection);
        }

        return defaults.CloneWithProfiles(merged);
    }

    private static HarnessCapabilityProfile CloneProfile(HarnessCapabilityProfile profile) =>
        new()
        {
            Harness = profile.Harness,
            DirectoryName = profile.DirectoryName,
            SupportsNativeParentAgents = profile.SupportsNativeParentAgents,
            MappedRoleSkillOverrides = new Dictionary<string, string>(
                profile.MappedRoleSkillOverrides,
                StringComparer.OrdinalIgnoreCase)
        };

    private static HarnessCapabilityProfile ApplyOverride(
        HarnessCapabilityProfile existing,
        HarnessProfileYamlSection? section)
    {
        if (section is null)
            return existing;

        Dictionary<string, string> overrides = existing.MappedRoleSkillOverrides;
        if (section.MappedRoleSkillOverrides is not null)
        {
            overrides = new Dictionary<string, string>(
                section.MappedRoleSkillOverrides,
                StringComparer.OrdinalIgnoreCase);
        }

        return new HarnessCapabilityProfile
        {
            Harness = existing.Harness,
            DirectoryName = section.DirectoryName ?? existing.DirectoryName,
            SupportsNativeParentAgents = section.SupportsNativeParentAgents ?? existing.SupportsNativeParentAgents,
            MappedRoleSkillOverrides = overrides
        };
    }

    private static bool TryParseHarnessKind(string name, out HarnessKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string normalized = name.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        foreach (HarnessKind candidate in Enum.GetValues<HarnessKind>())
        {
            if (candidate == HarnessKind.Custom)
                continue;

            if (string.Equals(candidate.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        return false;
    }
}
