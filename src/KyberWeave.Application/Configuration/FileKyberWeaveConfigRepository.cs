using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using YamlDotNet.Core;

namespace KyberWeave.Data.Configuration;

/// <summary>Reads the combined Kyber-Weave configuration through the file-system, path and environment seams.</summary>
/// <param name="tree">Answers what the file system says about a gate's <c>working-directory</c>.</param>
/// <param name="files">Reads the configuration files.</param>
/// <param name="paths">Turns the caller's root spelling into a full path.</param>
/// <param name="environment">Supplies the current directory a relative root is resolved against.</param>
public sealed class FileKyberWeaveConfigRepository(
    IRepositoryTreeProbe tree,
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : IKyberWeaveConfigRepository
{
    /// <inheritdoc />
    public KyberWeaveConfig Load(string repoRoot, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string root = FullPath(repoRoot);

        string? path = ResolveConfigPath(root, configPath);
        if (path is null)
            return KyberWeaveConfigDefaults.Product;

        return FromFile(path);
    }

    /// <inheritdoc />
    public KyberWeaveConfigLoadResult TryLoad(string repoRoot, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string root = FullPath(repoRoot);
        string? path = null;

        try
        {
            path = ResolveConfigPath(root, configPath);
            if (path is null)
                return new KyberWeaveConfigLoadResult(success: true, config: KyberWeaveConfigDefaults.Product);

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
            FullPath(repoRoot),
            KyberWeaveYamlParser.DefaultDirectoryName,
            KyberWeaveYamlParser.DefaultFileName);
        if (files.FileExists(defaultPath))
            return defaultPath;

        // Legacy location: repo-root kyber-weave.yml, kept working for existing hosts.
        string legacyPath = Path.Combine(FullPath(repoRoot), KyberWeaveYamlParser.DefaultFileName);
        return files.FileExists(legacyPath) ? legacyPath : null;
    }

    /// <inheritdoc />
    public KyberWeaveConfig LoadUserConfig(string homeDirectory, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string root = FullPath(homeDirectory);
        string? path = ResolveUserConfigPath(root, configPath);
        if (!string.IsNullOrWhiteSpace(configPath) && (path is null || !files.FileExists(path)))
        {
            throw new FileNotFoundException(
                $"Kyber-Weave config file not found: {path}",
                path);
        }

        if (path is null || !files.FileExists(path))
            return KyberWeaveConfigDefaults.Product;

        string yaml = files.ReadAllText(path);
        return KyberWeaveConfigAssembler.FromUserYaml(yaml);
    }

    /// <inheritdoc />
    public KyberWeaveConfigLoadResult TryLoadUserConfig(string homeDirectory, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string root = FullPath(homeDirectory);
        string? path = ResolveUserConfigPath(root, configPath);
        if (path is null)
            return new KyberWeaveConfigLoadResult(
                success: true,
                config: KyberWeaveConfigDefaults.Product,
                configPath: path);

        try
        {
            // The probes are inside the try because the file-system seam may throw where the static
            // File.Exists answered false: a failure must come back as a result, as it does for TryLoad.
            if (!string.IsNullOrWhiteSpace(configPath) && !files.FileExists(path))
            {
                return new KyberWeaveConfigLoadResult(
                    success: false,
                    error: $"Kyber-Weave config file not found: {path}",
                    configPath: path);
            }

            if (!files.FileExists(path))
                return new KyberWeaveConfigLoadResult(
                    success: true,
                    config: KyberWeaveConfigDefaults.Product,
                    configPath: path);

            string yaml = files.ReadAllText(path);
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
        string yaml = files.ReadAllText(yamlPath);
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
        if (!files.FileExists(explicitPath))
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
        string yaml = files.ReadAllText(path);
        string fullPath = FullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? fullPath;
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
    /// Reads a repository file for the assembler, or returns null when it does not exist. The assembler parses the
    /// text; this class owns the file-system access, so <c>DomainPurityTests</c> keeps holding.
    /// </summary>
    private string? ReadRepositoryText(string path) =>
        files.FileExists(path) ? files.ReadAllText(path) : null;

    /// <summary>Turns any spelling of a root into the full path the seam's answers use.</summary>
    private string FullPath(string path) => paths.GetFullPath(path, environment.CurrentDirectory);
}
