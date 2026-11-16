using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Core.Doctor;

/// <summary>Reads the doctor's two repository settings through the combined configuration repository.</summary>
/// <remarks>
/// The settings are the whole configuration's answer, not a read of two keys: a file that fails to load anywhere,
/// the Arbiter section included, leaves both at their defaults.
/// </remarks>
/// <param name="config">Reads the repository's combined configuration.</param>
public sealed class ConfigFileToolSettings(IKyberWeaveConfigRepository config) : IRepositoryToolSettings
{
    /// <inheritdoc />
    public RepositoryToolSettings Read(string repositoryRoot)
    {
        KyberWeaveConfigLoadResult result = config.TryLoad(repositoryRoot);
        return result.Config is { } loaded
            ? new RepositoryToolSettings(loaded.StopsOnToolFailure, loaded.WorktreeIndexHook)
            : new RepositoryToolSettings();
    }
}
