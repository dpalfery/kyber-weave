using System.Text.RegularExpressions;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;
using YamlDotNet.Core;

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

/// <summary>Resolves the Arbiter configuration over the registered user settings, environment and file system.</summary>
/// <remarks>
/// The load itself still calls <see cref="KyberWeaveConfigLoader"/>, which lives in the legacy Core project until the
/// configuration hub moves behind a repository port (BLOCKED-BY CR: KyberWeaveConfigLoader).
/// </remarks>
/// <param name="userSettings">Applies the user override to the repository provider.</param>
/// <param name="environment">Supplies the home directory the override lives under.</param>
/// <param name="files">Probes and reads the project config file.</param>
public sealed class ArbiterConfigResolver(
    IArbiterUserSettings userSettings,
    IProcessEnvironment environment,
    IFileSystem files) : IArbiterConfigResolver
{
    /// <inheritdoc />
    public bool TryResolve(
        string path,
        string? configFile,
        DiagnosticReport report,
        out KyberWeaveConfig config,
        out string? configPath)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (string.IsNullOrWhiteSpace(path))
        {
            report.Add(MapLoadFailure("Repository path must not be empty.", null));
            config = KyberWeaveConfig.ProductDefaults;
            configPath = null;
            return false;
        }

        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(path, configFile);
        if (!loaded.Success)
        {
            if (TryLoadProjectConfigWithoutGlobal(path, configFile, out config, out configPath))
            {
                report.Add(new Diagnostic(
                    RuleValidator.UserGlobalTargetsForbidden,
                    Severity.Warning,
                    $"[{RuleValidator.UserGlobalTargetsForbidden}] arbiter.global is only valid in the user-level config file; project config may not declare global targets.",
                    "arbiter.global",
                    FilePath: loaded.ConfigPath,
                    Hint: "Remove arbiter.global from project configuration or move it to ~/.kyber-weave/kyber-weave.yml."));
                return true;
            }

            report.Add(MapLoadFailure(loaded.Error ?? "Failed to load kyber-weave.yml.", loaded.ConfigPath));
            config = KyberWeaveConfig.ProductDefaults;
            configPath = loaded.ConfigPath;
            return false;
        }

        config = loaded.Config!;
        configPath = loaded.ConfigPath;

        try
        {
            ArbiterProviderConfig repository = config.Arbiter.Provider;
            ArbiterProviderConfig merged = userSettings.ApplyTo(repository, environment.HomeDirectory);
            if (!merged.Equals(repository))
            {
                config = new KyberWeaveConfig
                {
                    Ontology = config.Ontology,
                    Harness = config.Harness,
                    DocsAnalysis = config.DocsAnalysis,
                    Squad = config.Squad,
                    ConfigReg = config.ConfigReg,
                    Review = config.Review,
                    Arbiter = config.Arbiter with { Provider = merged },
                };
            }
        }
        catch (YamlException exception)
        {
            report.Add(MapLoadFailure(
                exception.Message,
                ArbiterUserSettings.GetPath(environment.HomeDirectory)));
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public KyberWeaveConfig? TryLoadRepository(string root)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root);
        return loaded.Success ? loaded.Config : null;
    }

    /// <inheritdoc />
    public KyberWeaveConfigLoadResult Load(string root, string? configFile, bool projectScope) =>
        projectScope
            ? KyberWeaveConfigLoader.TryLoad(root, configFile)
            : KyberWeaveConfigLoader.TryLoadUserConfig(root, configFile);

    /// <inheritdoc />
    public KyberWeaveConfig LoadFromYaml(string yaml) => KyberWeaveConfigLoader.LoadFromYaml(yaml);

    private bool TryLoadProjectConfigWithoutGlobal(
        string path,
        string? configFile,
        out KyberWeaveConfig config,
        out string? configPath)
    {
        string? repoConfigPath = ResolveProjectConfigPath(path, configFile);
        if (string.IsNullOrWhiteSpace(repoConfigPath) || !files.FileExists(repoConfigPath))
        {
            config = KyberWeaveConfig.ProductDefaults;
            configPath = repoConfigPath;
            return false;
        }

        try
        {
            string yaml = files.ReadAllText(repoConfigPath);
            string sanitized = ArbiterCliText.StripProjectGlobalTargets(yaml);
            if (string.Equals(yaml, sanitized, StringComparison.Ordinal))
            {
                config = KyberWeaveConfig.ProductDefaults;
                configPath = repoConfigPath;
                return false;
            }

            config = KyberWeaveConfigLoader.LoadFromYaml(sanitized);
            configPath = repoConfigPath;
            return true;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is YamlException)
        {
            config = KyberWeaveConfig.ProductDefaults;
            configPath = repoConfigPath;
            return false;
        }
    }

    private string? ResolveProjectConfigPath(string repoRoot, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(configPath))
        {
            return Path.IsPathRooted(configPath)
                ? configPath
                : Path.Combine(repoRoot, configPath);
        }

        string defaultPath = Path.Combine(repoRoot, ".kyber-weave", "kyber-weave.yml");
        if (files.FileExists(defaultPath))
        {
            return defaultPath;
        }

        string legacyPath = Path.Combine(repoRoot, "kyber-weave.yml");
        return files.FileExists(legacyPath) ? legacyPath : null;
    }

    private static Diagnostic MapLoadFailure(string message, string? filePath)
    {
        Match match = Regex.Match(message, @"KW-ARB-CONFIG-\d{3}");
        string code = match.Success
            ? match.Value
            : KyberWeaveConfigLoader.ConfigLoadErrorCode;
        return new Diagnostic(
            code,
            Severity.Error,
            message,
            "arbiter",
            filePath,
            Hint: "Fix the arbiter: section, or remove it to take the product defaults.");
    }
}
