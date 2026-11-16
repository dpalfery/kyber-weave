using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Services;

/// <summary>Loads the configuration the Arbiter hook host decides under, for the project hook or the user-level hook.</summary>
public interface IArbiterHostConfig
{
    /// <summary>
    /// Loads the host configuration for <paramref name="repoRoot"/>. A missing or malformed
    /// <c>.kyber-weave/kyber-weave.yml</c> is an internal error carrying <c>KW-ARB-HOOK-001</c>: the host fails closed on
    /// classified events.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="scope">The project hook, or the user-level hook, which loads the user config when the project does not enable the Arbiter.</param>
    /// <param name="homeDirectory">The operator's home, or null for the user profile.</param>
    /// <returns>The configuration.</returns>
    /// <exception cref="InvalidOperationException">The configuration is missing or malformed.</exception>
    KyberWeaveConfig Load(string repoRoot, HookScope scope = HookScope.Project, string? homeDirectory = null);

    /// <summary>Loads the project's configuration, for the user-level hook to evaluate under.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <returns>The configuration; null when the project has no config file.</returns>
    /// <exception cref="InvalidOperationException">The file exists but does not load, so the user-level hook fails closed rather than guessing which configuration applies.</exception>
    KyberWeaveConfig? LoadProject(string repoRoot);

    /// <summary>Resolves the operator's home: <c>~</c> and <c>~/…</c> expand against the user profile.</summary>
    /// <param name="homeDirectory">The <c>--home</c> value, or null for the user profile.</param>
    /// <returns>The home directory.</returns>
    string ResolveHomeDirectory(string? homeDirectory);
}
