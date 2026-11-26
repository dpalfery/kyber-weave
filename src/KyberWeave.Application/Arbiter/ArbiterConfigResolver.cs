using System.Text.RegularExpressions;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;
using YamlDotNet.Core;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Resolves the Arbiter configuration over the registered user settings, environment and file system.</summary>
/// <param name="userSettings">Applies the user override to the repository provider.</param>
/// <param name="environment">Supplies the home directory the override lives under.</param>
/// <param name="files">Probes and reads the project config file.</param>
/// <param name="repository">Reads the project and the per-user host configuration.</param>
public sealed class ArbiterConfigResolver(
    IArbiterUserSettings userSettings,
    IProcessEnvironment environment,
    IFileSystem files,
    IKyberWeaveConfigRepository repository) : IArbiterConfigResolver
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
            config = KyberWeaveConfigDefaults.Product;
            configPath = null;
            return false;
        }

        KyberWeaveConfigLoadResult loaded = repository.TryLoad(path, configFile);
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
            config = KyberWeaveConfigDefaults.Product;
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
                config = config.WithArbiter(config.Arbiter with { Provider = merged });
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
        KyberWeaveConfigLoadResult loaded = repository.TryLoad(root);
        return loaded.Success ? loaded.Config : null;
    }

    /// <inheritdoc />
    public KyberWeaveConfigLoadResult Load(string root, string? configFile, bool projectScope) =>
        projectScope
            ? repository.TryLoad(root, configFile)
            : repository.TryLoadUserConfig(root, configFile);

    /// <inheritdoc />
    public KyberWeaveConfig LoadFromYaml(string yaml) => KyberWeaveConfigAssembler.FromYaml(yaml);

    private bool TryLoadProjectConfigWithoutGlobal(
        string path,
        string? configFile,
        out KyberWeaveConfig config,
        out string? configPath)
    {
        string? repoConfigPath = ResolveProjectConfigPath(path, configFile);
        if (string.IsNullOrWhiteSpace(repoConfigPath) || !files.FileExists(repoConfigPath))
        {
            config = KyberWeaveConfigDefaults.Product;
            configPath = repoConfigPath;
            return false;
        }

        try
        {
            string yaml = files.ReadAllText(repoConfigPath);
            string sanitized = ArbiterCliText.StripProjectGlobalTargets(yaml);
            if (string.Equals(yaml, sanitized, StringComparison.Ordinal))
            {
                config = KyberWeaveConfigDefaults.Product;
                configPath = repoConfigPath;
                return false;
            }

            config = KyberWeaveConfigAssembler.FromYaml(sanitized);
            configPath = repoConfigPath;
            return true;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is YamlException)
        {
            config = KyberWeaveConfigDefaults.Product;
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

        string defaultPath = Path.Combine(repoRoot, UserStatePaths.DirectoryName, "kyber-weave.yml");
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
            : IDocsHostConfiguration.ConfigLoadErrorCode;
        return new Diagnostic(
            code,
            Severity.Error,
            message,
            "arbiter",
            filePath,
            Hint: "Fix the arbiter: section, or remove it to take the product defaults.");
    }
}
