using KyberWeave.Core.Agents.Model;

namespace KyberWeave.Core.Configuration;

/// <summary>
/// Host-overridable harness capability profiles. Product defaults include the six
/// supported harness namespaces without MotorcycleRAG conductor→skill satisfaction.
/// </summary>
public sealed class HarnessProfileConfig
{
    public IReadOnlyDictionary<HarnessKind, HarnessCapabilityProfile> Profiles { get; init; } =
        CreateProductDefaultProfiles();

    public static HarnessProfileConfig ProductDefaults { get; } = new();

    internal HarnessProfileConfig CloneWithProfiles(
        IReadOnlyDictionary<HarnessKind, HarnessCapabilityProfile> profiles) =>
        new() { Profiles = profiles };

    private static Dictionary<HarnessKind, HarnessCapabilityProfile> CreateProductDefaultProfiles() =>
        new()
        {
            [HarnessKind.Codex] = new HarnessCapabilityProfile
            {
                Harness = HarnessKind.Codex,
                DirectoryName = ".codex/agents",
                SupportsNativeParentAgents = false
            },
            [HarnessKind.Cursor] = new HarnessCapabilityProfile
            {
                Harness = HarnessKind.Cursor,
                DirectoryName = ".cursor/agents",
                SupportsNativeParentAgents = false
            },
            [HarnessKind.Claude] = new HarnessCapabilityProfile
            {
                Harness = HarnessKind.Claude,
                DirectoryName = ".claude/agents",
                SupportsNativeParentAgents = false
            },
            [HarnessKind.GitHubCopilot] = new HarnessCapabilityProfile
            {
                Harness = HarnessKind.GitHubCopilot,
                DirectoryName = ".github/agents",
                SupportsNativeParentAgents = false
            },
            [HarnessKind.OpenCode] = new HarnessCapabilityProfile
            {
                Harness = HarnessKind.OpenCode,
                DirectoryName = ".opencode/agents",
                SupportsNativeParentAgents = true
            },
            [HarnessKind.Kilo] = new HarnessCapabilityProfile
            {
                Harness = HarnessKind.Kilo,
                DirectoryName = ".kilo/agents",
                SupportsNativeParentAgents = true
            }
        };
}
