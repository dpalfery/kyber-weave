namespace KyberWeave.Core.Configuration;

/// <summary>One harness profile override under <c>harness.profiles</c>.</summary>
internal sealed class HarnessProfileYamlSection
{
    public string? DirectoryName { get; set; }

    public bool? SupportsNativeParentAgents { get; set; }

    public Dictionary<string, string>? MappedRoleSkillOverrides { get; set; }
}
