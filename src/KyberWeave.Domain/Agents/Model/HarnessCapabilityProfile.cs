namespace KyberWeave.Core.Agents.Model;

/// <summary>
/// Defines the capabilities and mappings for a specific development harness platform.
/// Used by validation and linting engines to understand whether parent agents (like conductor)
/// are implemented natively in the harness agent folder or via skill mappings.
/// </summary>
/// <remarks>
/// Product defaults deliberately omit host-specific role→skill satisfaction maps
/// (e.g. MotorcycleRAG conductor→conductor). Hosts restore those via <c>.kyber-weave/kyber-weave.yml</c>.
/// </remarks>
public sealed class HarnessCapabilityProfile
{
    public HarnessKind Harness { get; init; }
    public string DirectoryName { get; init; } = string.Empty;
    public bool SupportsNativeParentAgents { get; init; } = true;
    public Dictionary<string, string> MappedRoleSkillOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
