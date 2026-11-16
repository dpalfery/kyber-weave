using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Loads the host configuration for an Arbiter command and applies the user override.</summary>
public interface IArbiterConfigResolver
{
    /// <summary>
    /// Loads the host configuration and applies the user override, reporting failures as diagnostics. A load that
    /// already names a <c>KW-ARB-CONFIG-*</c> code keeps it; any other load failure surfaces as <c>KW-CONFIG-001</c>.
    /// </summary>
    /// <param name="path">The repository path.</param>
    /// <param name="configFile">The <c>--config</c> path, or null.</param>
    /// <param name="report">Receives the diagnostics.</param>
    /// <param name="config">The effective configuration, or the product defaults on failure.</param>
    /// <param name="configPath">The configuration file read, when one was.</param>
    /// <returns>True when a configuration was produced.</returns>
    bool TryResolve(string path, string? configFile, DiagnosticReport report, out KyberWeaveConfig config, out string? configPath);

    /// <summary>Loads the repository's own configuration, with no user override and no diagnostics.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The configuration, or null when none loads.</returns>
    KyberWeaveConfig? TryLoadRepository(string root);

    /// <summary>Loads the project configuration under a root, or the per-user host configuration for global scope.</summary>
    /// <param name="root">The project root, or the home directory for global scope.</param>
    /// <param name="configFile">The <c>--config</c> path, or null.</param>
    /// <param name="projectScope">True for the project file, false for the per-user file.</param>
    /// <returns>The load result, successful or not.</returns>
    KyberWeaveConfigLoadResult Load(string root, string? configFile, bool projectScope);

    /// <summary>Parses configuration text, with no repository on disk.</summary>
    /// <param name="yaml">The configuration text.</param>
    /// <returns>The configuration.</returns>
    KyberWeaveConfig LoadFromYaml(string yaml);
}
