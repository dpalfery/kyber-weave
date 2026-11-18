using KyberWeave.Data.Configuration;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Configuration;

/// <summary>
/// The configuration reads of <see cref="ConfigFileReader"/> over an in-memory file system: a test writes a config file
/// as text, reads it back through the shipped repository and parse, and touches no disk.
/// </summary>
public sealed class InMemoryConfigFiles
{
    private readonly FileKyberWeaveConfigRepository _repository;

    /// <summary>Creates the reader over a file system of its own, or over one the test already arranged.</summary>
    /// <param name="files">The file system holding the config files; a new empty one when null.</param>
    public InMemoryConfigFiles(InMemoryFileSystem? files = null)
    {
        Files = files ?? new InMemoryFileSystem();
        FakePathResolver paths = new(Files);
        FakeProcessEnvironment environment = new(
            currentDirectory: Path.Combine(Files.Root, "work"),
            homeDirectory: Path.Combine(Files.Root, "home"),
            isFileSystemCaseInsensitive: !Files.CaseSensitive);
        _repository = new FileKyberWeaveConfigRepository(
            new FileRepositoryTreeProbe(Files, paths, environment),
            Files,
            paths,
            environment);
    }

    /// <summary>Gets the file system holding the config files.</summary>
    public InMemoryFileSystem Files { get; }

    /// <summary>Gets the shipped repository over <see cref="Files"/>, for code that takes the seam.</summary>
    public IKyberWeaveConfigRepository Repository => _repository;

    /// <summary>Writes a config file, creating its directory.</summary>
    /// <param name="path">The full path of the file.</param>
    /// <param name="yaml">The config text.</param>
    /// <returns>The path written.</returns>
    public string Write(string path, string yaml)
    {
        Files.CreateDirectory(Path.GetDirectoryName(path)!);
        Files.WriteAllText(path, yaml);
        return path;
    }

    /// <summary>Writes a repository's <c>.kyber-weave/kyber-weave.yml</c>.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="yaml">The config text.</param>
    /// <returns>The path written.</returns>
    public string WriteRepositoryConfig(string repoRoot, string yaml) =>
        Write(FakeKyberWeaveConfigRepository.RepositoryConfigPath(repoRoot), yaml);

    /// <summary>Merges the <c>harness:</c> section of a config file over the product defaults.</summary>
    /// <param name="yamlPath">The config file.</param>
    public HarnessProfileConfig LoadHarness(string yamlPath) =>
        HarnessProfileConfigLoader.Merge(HarnessProfileConfig.ProductDefaults, _repository.ReadDocument(yamlPath).Harness);

    /// <summary>Merges the <c>squad:</c> section of a config file over <paramref name="defaults"/>.</summary>
    /// <param name="defaults">The defaults to merge over.</param>
    /// <param name="yamlPath">The config file.</param>
    public SquadConfig LoadSquadMerged(SquadConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return SquadConfigLoader.Merge(defaults, _repository.ReadDocument(yamlPath).Squad);
    }

    /// <summary>Loads a repository's configuration, reporting a failure instead of throwing.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="configPath">An explicit config path, or null to find it under the root.</param>
    public KyberWeaveConfigLoadResult TryLoad(string repoRoot, string? configPath = null) =>
        _repository.TryLoad(repoRoot, configPath);
}
