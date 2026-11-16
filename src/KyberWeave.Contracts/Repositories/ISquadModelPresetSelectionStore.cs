using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Records which preset each project selects per target.</summary>
public interface ISquadModelPresetSelectionStore
{
    /// <summary>Reads a project's selection, or null when it has none.</summary>
    SquadModelPresetSelection? Read(string projectRoot);

    /// <summary>Records the preset a project selects for a target.</summary>
    void Record(string projectRoot, SquadTarget target, string presetName);

    /// <summary>Clears a project's selection for one target.</summary>
    void Clear(string projectRoot, SquadTarget target);

    /// <summary>Clears every selection of a project.</summary>
    void Clear(string projectRoot);
}
