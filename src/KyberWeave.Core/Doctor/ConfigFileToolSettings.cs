using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Core.Doctor;

/// <summary>Reads the doctor's two repository settings through the combined configuration loader.</summary>
/// <remarks>
/// The settings are the whole configuration's answer, not a read of two keys: a file that fails to load anywhere,
/// the Arbiter section included, leaves both at their defaults. That needs the combined loader, which cannot leave
/// Core until the Arbiter configuration types do, so this adapter lives here until then.
/// </remarks>
public sealed class ConfigFileToolSettings : IRepositoryToolSettings
{
    /// <inheritdoc />
    public RepositoryToolSettings Read(string repositoryRoot)
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(repositoryRoot);
        return result.Config is { } config
            ? new RepositoryToolSettings(config.StopsOnToolFailure, config.WorktreeIndexHook)
            : new RepositoryToolSettings();
    }
}
