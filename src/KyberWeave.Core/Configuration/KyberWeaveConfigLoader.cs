using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>
/// Loads the combined Kyber-Weave configuration from YAML text or a repository root
/// (<c>.kyber-weave/kyber-weave.yml</c>, falling back to a legacy root <c>kyber-weave.yml</c>,
/// and optionally overridden by an explicit <c>--config</c> path).
/// </summary>
public static class KyberWeaveConfigLoader
{
    /// <summary>Stable rule code for invalid or unreadable <c>kyber-weave.yml</c>.</summary>
    public const string ConfigLoadErrorCode = "KW-CONFIG-001";

    public static KyberWeaveConfig LoadFromYaml(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var document = KyberWeaveYamlParser.Parse(yaml);
        return FromDocument(document);
    }

    public static KyberWeaveConfig Load(string repoRoot, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var root = Path.GetFullPath(repoRoot);

        var path = ResolveConfigPath(root, configPath);
        if (path is null)
            return KyberWeaveConfig.ProductDefaults;

        var document = KyberWeaveYamlParser.ParseFile(path);
        return FromDocument(document);
    }

    /// <summary>
    /// Attempts to load host configuration. Invalid YAML and I/O failures become a failed
    /// result rather than an uncaught exception so CLI entry points can emit a structured
    /// <see cref="ConfigLoadErrorCode"/> diagnostic.
    /// </summary>
    public static KyberWeaveConfigLoadResult TryLoad(string repoRoot, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var root = Path.GetFullPath(repoRoot);
        string? path = null;

        try
        {
            path = ResolveConfigPath(root, configPath);
            if (path is null)
                return KyberWeaveConfigLoadResult.Ok(KyberWeaveConfig.ProductDefaults, null);

            var document = KyberWeaveYamlParser.ParseFile(path);
            return KyberWeaveConfigLoadResult.Ok(FromDocument(document), path);
        }
        catch (YamlException ex)
        {
            return KyberWeaveConfigLoadResult.Fail(ex.Message, path ?? configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return KyberWeaveConfigLoadResult.Fail(ex.Message, path ?? configPath);
        }
    }

    private static string? ResolveConfigPath(string repoRoot, string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var explicitPath = Path.IsPathRooted(configPath)
                ? configPath
                : Path.Combine(repoRoot, configPath);
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException(
                    $"Kyber-Weave config file not found: {explicitPath}",
                    explicitPath);
            }

            return explicitPath;
        }

        var defaultPath = Path.Combine(
            repoRoot,
            KyberWeaveYamlParser.DefaultDirectoryName,
            KyberWeaveYamlParser.DefaultFileName);
        if (File.Exists(defaultPath))
            return defaultPath;

        // Legacy location: repo-root kyber-weave.yml, kept working for existing hosts.
        var legacyPath = Path.Combine(repoRoot, KyberWeaveYamlParser.DefaultFileName);
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static KyberWeaveConfig FromDocument(KyberWeaveYamlDocument document) =>
        new()
        {
            Ontology = OntologyConfigLoader.Merge(OntologyConfig.ProductDefaults, document.Ontology),
            Harness = HarnessProfileConfigLoader.Merge(HarnessProfileConfig.ProductDefaults, document.Harness)
        };
}
