using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Checks that a model preset only names profile categories the Squad source declares.</summary>
public interface ISquadModelPresetValidator
{
    /// <summary>Validates a preset against the bundled profiles.</summary>
    /// <param name="preset">The preset to validate.</param>
    /// <param name="bundled">The bundled model profiles to validate against.</param>
    void Validate(SquadModelPreset preset, SquadModelProfiles bundled);
}
