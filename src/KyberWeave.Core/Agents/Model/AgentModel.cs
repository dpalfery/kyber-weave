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
    public string Description { get; set; } = string.Empty;
    public string InstructionsBody { get; set; } = string.Empty;
    public string ModelPreference { get; set; } = string.Empty;
    public Collection<string> Tools { get; set; } = [];
    public Dictionary<string, string> FrontmatterOrMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
