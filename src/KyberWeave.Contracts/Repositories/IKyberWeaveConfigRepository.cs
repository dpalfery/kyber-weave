using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Repositories;

/// <summary>
/// Reads the combined Kyber-Weave configuration of a repository (<c>.kyber-weave/kyber-weave.yml</c>, falling back to a
/// legacy root <c>kyber-weave.yml</c>, and optionally overridden by an explicit <c>--config</c> path) and of a user's home.
/// </summary>
public interface IKyberWeaveConfigRepository
{
    /// <summary>Loads a repository's configuration; the product defaults when it has no config file.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="configPath">An explicit config file, absolute or relative to the root; null finds the repository's own.</param>
    /// <exception cref="FileNotFoundException">An explicit config file does not exist.</exception>
    /// <exception cref="YamlDotNet.Core.YamlException">The file is not valid configuration.</exception>
    KyberWeaveConfig Load(string repoRoot, string? configPath = null);

    /// <summary>
    /// Attempts to load a repository's configuration. Invalid YAML and I/O failures become a failed result rather than an
    /// uncaught exception, so CLI entry points can emit a structured <c>KW-CONFIG-001</c> diagnostic.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="configPath">An explicit config file, absolute or relative to the root; null finds the repository's own.</param>
    KyberWeaveConfigLoadResult TryLoad(string repoRoot, string? configPath = null);

    /// <summary>
    /// The host config file a repository root already has, or null when it has none. A writer edits the same file every
    /// reader resolves: creating the default file in a repository configured through the legacy root path would shadow it.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    string? FindConfigPath(string repoRoot);

    /// <summary>
    /// Loads the per-user host config file under <c>~/.kyber-weave/kyber-weave.yml</c>, where <c>arbiter.global.targets</c> is
    /// valid; the product defaults when it does not exist.
    /// </summary>
    /// <param name="homeDirectory">The operator's home directory.</param>
    /// <param name="configPath">An explicit config file, absolute or relative to the home; null uses the user config file.</param>
    /// <exception cref="FileNotFoundException">An explicit config file does not exist.</exception>
    KyberWeaveConfig LoadUserConfig(string homeDirectory, string? configPath = null);

    /// <summary>Attempts to load the per-user host config file, reporting a failure instead of throwing.</summary>
    /// <param name="homeDirectory">The operator's home directory.</param>
    /// <param name="configPath">An explicit config file, absolute or relative to the home; null uses the user config file.</param>
    KyberWeaveConfigLoadResult TryLoadUserConfig(string homeDirectory, string? configPath = null);
}
