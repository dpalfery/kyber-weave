using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using YamlDotNet.Core;

namespace KyberWeave.Data.Configuration;

/// <summary>Reads the combined Kyber-Weave configuration from the real file system.</summary>
/// <param name="tree">Answers what the file system says about a gate's <c>working-directory</c>.</param>
public sealed class FileKyberWeaveConfigRepository(IRepositoryTreeProbe tree) : IKyberWeaveConfigRepository
{
    /// <inheritdoc />
    public KyberWeaveConfig Load(string repoRoot, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string root = Path.GetFullPath(repoRoot);

        string? path = ResolveConfigPath(root, configPath);
        if (path is null)
            return KyberWeaveConfig.ProductDefaults;

        return FromFile(path);
    }

    /// <inheritdoc />
    public KyberWeaveConfigLoadResult TryLoad(string repoRoot, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string root = Path.GetFullPath(repoRoot);
        string? path = null;

        try
        {
            path = ResolveConfigPath(root, configPath);
            if (path is null)
                return new KyberWeaveConfigLoadResult(success: true, config: KyberWeaveConfig.ProductDefaults);

            return new KyberWeaveConfigLoadResult(success: true, config: FromFile(path), configPath: path);
        }
        catch (YamlException ex)
        {
            return new KyberWeaveConfigLoadResult(
                success: false,
                error: ex.Message,
                configPath: path ?? configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return new KyberWeaveConfigLoadResult(
                success: false,
                error: ex.Message,
                configPath: path ?? configPath);
        }
    }

    /// <inheritdoc />
    public string? FindConfigPath(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        string defaultPath = Path.Combine(
            Path.GetFullPath(repoRoot),
            KyberWeaveYamlParser.DefaultDirectoryName,
            KyberWeaveYamlParser.DefaultFileName);
        if (File.Exists(defaultPath))
            return defaultPath;

        // Legacy location: repo-root kyber-weave.yml, kept working for existing hosts.
        string legacyPath = Path.Combine(Path.GetFullPath(repoRoot), KyberWeaveYamlParser.DefaultFileName);
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    /// <inheritdoc />
    public KyberWeaveConfig LoadUserConfig(string homeDirectory, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string root = Path.GetFullPath(homeDirectory);
        string? path = ResolveUserConfigPath(root, configPath);
        if (!string.IsNullOrWhiteSpace(configPath) && (path is null || !File.Exists(path)))
        {
            throw new FileNotFoundException(
                $"Kyber-Weave config file not found: {path}",
                path);
        }

        if (path is null || !File.Exists(path))
            return KyberWeaveConfig.ProductDefaults;

        string yaml = File.ReadAllText(path);
        return KyberWeaveConfigAssembler.FromUserYaml(yaml);
    }

    /// <inheritdoc />
    public KyberWeaveConfigLoadResult TryLoadUserConfig(string homeDirectory, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string root = Path.GetFullPath(homeDirectory);
        string? path = ResolveUserConfigPath(root, configPath);
        if (path is null)
            return new KyberWeaveConfigLoadResult(
                success: true,
                config: KyberWeaveConfig.ProductDefaults,
                configPath: path);

        if (!string.IsNullOrWhiteSpace(configPath) && !File.Exists(path))
        {
            return new KyberWeaveConfigLoadResult(
                success: false,
                error: $"Kyber-Weave config file not found: {path}",
                configPath: path);
        }

        if (!File.Exists(path))
            return new KyberWeaveConfigLoadResult(
                success: true,
                config: KyberWeaveConfig.ProductDefaults,
                configPath: path);

        try
        {
            string yaml = File.ReadAllText(path);
            return new KyberWeaveConfigLoadResult(
                success: true,
                config: KyberWeaveConfigAssembler.FromUserYaml(yaml),
                configPath: path);
        }
        catch (YamlException ex)
        {
            return new KyberWeaveConfigLoadResult(success: false, error: ex.Message, configPath: path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new KyberWeaveConfigLoadResult(success: false, error: ex.Message, configPath: path);
        }
    }

    /// <summary>Reads and parses one config file into its sections, without merging them over defaults.</summary>
    /// <param name="yamlPath">The config file.</param>
    internal KyberWeaveYamlDocument ReadDocument(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);
        string yaml = File.ReadAllText(yamlPath);
        return KyberWeaveYamlParser.Parse(yaml);
    }

    /// <summary>Loads the <c>ontology:</c> section of a config file, reporting a failure instead of throwing.</summary>
    /// <param name="yamlPath">The config file.</param>
    internal OntologyConfigLoadResult TryLoadOntology(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);

        try
        {
            KyberWeaveYamlDocument document = ReadDocument(yamlPath);
            return OntologyConfigLoadResult.Ok(OntologyConfigLoader.Merge(OntologyConfig.ProductDefaults, document.Ontology));
        }
        catch (YamlException ex)
        {
            return OntologyConfigLoadResult.Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            // YamlDotNet throws ArgumentException for a null mapping key (e.g. required-keys
            // with an empty key). Surface it as a config failure rather than an unhandled crash.
            return OntologyConfigLoadResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OntologyConfigLoadResult.Fail(ex.Message);
        }
    }

    private string? ResolveConfigPath(string repoRoot, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return FindConfigPath(repoRoot);

        string explicitPath = Path.IsPathRooted(configPath)
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

    private static string? ResolveUserConfigPath(string homeDirectory, string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            return Path.IsPathRooted(configPath)
                ? configPath
                : Path.Combine(homeDirectory, configPath);
        }

        return UserHostConfigPath.Of(homeDirectory);
    }

    /// <summary>
    /// Reads a config file. Its repository root is the parent of the <c>.kyber-weave/</c> directory that
    /// holds it; a legacy root-level file, or one outside such a directory, uses its own directory.
    /// </summary>
    private KyberWeaveConfig FromFile(string path)
    {
        string yaml = File.ReadAllText(path);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Path.GetFullPath(path);
        string root = Path.GetFileName(directory) == KyberWeaveYamlParser.DefaultDirectoryName
            ? Path.GetDirectoryName(directory) ?? directory
            : directory;

        return KyberWeaveConfigAssembler.FromRepositoryYaml(
            yaml,
            root,
            new ReviewWorkingDirectoryFacts(tree.DirectoryExists, tree.LeadsOutsideThroughSymlink),
            ReadRepositoryText);
    }

    /// <summary>
    /// Reads a repository file for the assembler, or returns null when it does not exist. The domain layer parses the
    /// text; this data layer owns the disk access, so <c>DomainPurityTests</c> keeps holding.
    /// </summary>
    private static string? ReadRepositoryText(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;
}
