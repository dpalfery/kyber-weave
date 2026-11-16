using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Contracts.Services;

/// <summary>Loads a repository's configuration for a command and says what went wrong as report diagnostics.</summary>
public interface IConfigLoadService
{
    /// <summary>
    /// Loads <c>.kyber-weave/kyber-weave.yml</c>. On failure adds <c>KW-CONFIG-001</c> and returns false; each unknown key
    /// that loaded is added as a <c>KW-CONFIG-002</c> warning.
    /// </summary>
    /// <param name="repoRoot">The repository root the configuration is resolved under.</param>
    /// <param name="configPath">An explicit <c>--config</c> path, or null for the repository's own.</param>
    /// <param name="report">The report a failure or warning is recorded in.</param>
    /// <param name="config">The loaded configuration, or the product defaults on failure.</param>
    /// <param name="resolvedConfigPath">The file that was read, or null when the repository has none.</param>
    /// <returns>True when the configuration loaded.</returns>
    bool TryLoad(
        string repoRoot,
        string? configPath,
        DiagnosticReport report,
        out KyberWeaveConfig config,
        out string? resolvedConfigPath);

    /// <summary>
    /// Loads the configuration and, when <paramref name="docsRoots"/> is not empty, replaces the configured documentation
    /// roots with them. A rejected root is reported as <c>KW-CONFIG-001</c> under <c>--docs-root</c>.
    /// </summary>
    /// <param name="repoRoot">The repository root the configuration is resolved under.</param>
    /// <param name="configPath">An explicit <c>--config</c> path, or null for the repository's own.</param>
    /// <param name="docsRoots">The roots given on the command line.</param>
    /// <param name="report">The report a failure or warning is recorded in.</param>
    /// <param name="config">The configuration with the roots applied.</param>
    /// <param name="ontology">The ontology the roots belong to.</param>
    /// <returns>True when the configuration loaded and the roots were accepted.</returns>
    bool TryLoadWithDocsRoots(
        string repoRoot,
        string? configPath,
        IReadOnlyList<string> docsRoots,
        DiagnosticReport report,
        out KyberWeaveConfig config,
        out OntologyConfig ontology);
}
