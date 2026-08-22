using System.Collections.ObjectModel;

namespace KyberWeave.Core.Agents.Model;

/// <summary>
/// Unified in-memory model representing an Agent definition loaded from a coding harness configuration.
/// </summary>
public sealed class AgentModel
{
    public required string RoleName { get; init; }
    public required HarnessKind Harness { get; init; }
    public required string FilePath { get; init; }
    public required string DirectoryPath { get; init; }
    public string Description { get; init; } = string.Empty;
    public string InstructionsBody { get; init; } = string.Empty;
    public string ModelPreference { get; init; } = string.Empty;
    public Collection<string> Tools { get; init; } = [];
    public Dictionary<string, string> FrontmatterOrMetadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
