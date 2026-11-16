using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the user's named model presets.</summary>
public interface ISquadModelPresetStore
{
    /// <summary>Lists the names of the presets that can be loaded.</summary>
    IReadOnlyList<string> List();

    /// <summary>Loads a preset, or null when it does not exist.</summary>
    SquadModelPreset? TryLoad(string name);
}
