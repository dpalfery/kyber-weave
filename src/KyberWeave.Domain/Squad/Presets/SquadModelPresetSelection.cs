using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Squad.Model;

/// <summary>
///     Recorded model preset selection for a specific project root.
/// </summary>
/// <param name="Root">The canonical physical root path of the project.</param>
/// <param name="ModelPresets">The mapping of deployment targets to preset names.</param>
public sealed record SquadModelPresetSelection(
    string Root,
    IReadOnlyDictionary<SquadTarget, string> ModelPresets);
