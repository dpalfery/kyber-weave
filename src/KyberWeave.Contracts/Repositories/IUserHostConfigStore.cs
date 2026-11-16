namespace KyberWeave.Contracts.Repositories;

/// <summary>The per-user host config file, <c>~/.kyber-weave/kyber-weave.yml</c>, and the Arbiter global target ledger it holds.</summary>
public interface IUserHostConfigStore
{
    /// <summary>Creates the user config with the Arbiter enabled, or enables it in the existing file.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The file's text after the update.</returns>
    string EnsureUserConfig(string home);

    /// <summary>Replaces the Arbiter global targets in the user config.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <param name="targets">The target tokens to record.</param>
    void WriteGlobalTargets(string home, IReadOnlyList<string> targets);

    /// <summary>Removes one Arbiter global target from the user config. A missing file is left missing.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <param name="target">The target token to remove.</param>
    void RemoveGlobalTarget(string home, string target);

    /// <summary>Reads the Arbiter global targets.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The distinct target tokens; empty when the file is missing, unreadable or declares none.</returns>
    IReadOnlyList<string> ReadGlobalTargets(string home);
}
