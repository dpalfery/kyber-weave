using KyberWeave.Domain.Hooks;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Supplies the effective hook configuration for a repository.</summary>
public interface IHooksConfigSource
{
    /// <summary>Loads the built-in defaults merged with the repository's hook config file.</summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The effective config. Throws when the file exists but cannot be read.</returns>
    HooksConfig Load(string repositoryRoot);
}
