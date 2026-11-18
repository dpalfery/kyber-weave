using KyberWeave.Data.Configuration;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Configuration;

/// <summary>
/// The shipped configuration repository over an in-memory file system, for a unit test that arranges config text and
/// asks what the product reads from it: the same parse, merge and failure results as <see cref="ConfigFileReader"/>,
/// with no disk.
/// </summary>
internal sealed class InMemoryConfigFileReader
{
    private int _next;

    /// <summary>Creates an empty file system and the repository over it.</summary>
    public InMemoryConfigFileReader()
    {
        Files = new InMemoryFileSystem();
        Root = Path.Combine(Files.Root, "config-repo");
        Files.CreateDirectory(Root);
        Repository = new FileKyberWeaveConfigRepository(
            new FakeRepositoryTreeProbe(),
            Files,
            new FakePathResolver(Files),
            new FakeProcessEnvironment(
                currentDirectory: Root,
                homeDirectory: Path.Combine(Files.Root, "home"),
                tempDirectory: Path.Combine(Files.Root, "tmp"),
                isFileSystemCaseInsensitive: !Files.CaseSensitive));
    }

    /// <summary>Gets the file system the repository reads.</summary>
    public InMemoryFileSystem Files { get; }

    /// <summary>Gets the repository root the repository config is written under.</summary>
    public string Root { get; }

    /// <summary>Gets the repository over <see cref="Files"/>.</summary>
    public FileKyberWeaveConfigRepository Repository { get; }

    /// <summary>Writes a config file anywhere in the in-memory tree and returns its path.</summary>
    /// <param name="content">The config text.</param>
    /// <returns>The full path written.</returns>
    public string WriteYaml(string content)
    {
        string path = Path.Combine(Root, $"config-{_next++}.yml");
        Files.WriteAllText(path, content);
        return path;
    }

    /// <summary>Writes the repository's own <c>.kyber-weave/kyber-weave.yml</c> under <see cref="Root"/>.</summary>
    /// <param name="content">The config text.</param>
    /// <returns>The full path written.</returns>
    public string WriteRepositoryConfig(string content)
    {
        string path = Path.Combine(Root, ".kyber-weave", "kyber-weave.yml");
        Files.CreateDirectory(Path.GetDirectoryName(path)!);
        Files.WriteAllText(path, content);
        return path;
    }

    /// <summary>Loads the <c>ontology:</c> section of a config file, reporting a failure instead of throwing.</summary>
    /// <param name="yamlPath">The config file.</param>
    /// <returns>The load result.</returns>
    public OntologyConfigLoadResult TryLoadOntology(string yamlPath) => Repository.TryLoadOntology(yamlPath);

    /// <summary>Merges the <c>ontology:</c> section of a config file over <paramref name="defaults"/>.</summary>
    /// <param name="defaults">The ontology to merge over.</param>
    /// <param name="yamlPath">The config file.</param>
    /// <returns>The merged ontology.</returns>
    public OntologyConfig LoadOntologyMerged(OntologyConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return OntologyConfigLoader.Merge(defaults, Repository.ReadDocument(yamlPath).Ontology);
    }
}
