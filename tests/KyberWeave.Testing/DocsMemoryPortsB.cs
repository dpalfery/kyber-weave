using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;

namespace KyberWeave.Tests;

/// <summary>
/// An empty repository checkout in memory, with the documentation scaffolder, the configuration repository, the
/// loader, the validator and the cache-safety check built over the same in-memory file system, path resolver,
/// environment, clock and a scripted git. A unit test of scaffolding or migration arranges files here and reads
/// them back; nothing touches the disk, the clock or a process.
/// </summary>
internal sealed class MemoryScaffoldHost
{
    /// <summary>The date the fixed clock reads, as the scaffolder stamps it.</summary>
    public const string Today = "2026-11-18";

    private readonly ScriptedProcessExecutor _git = new(_ => new ProcessResult(128, string.Empty, "fatal: not a git repository"));

    /// <summary>Creates a host over a new in-memory file system, with an empty checkout.</summary>
    public MemoryScaffoldHost()
    {
        Memory = new InMemoryFileSystem();
        Root = Path.Combine(Memory.Root, "repo");
        Memory.CreateDirectory(Root);
        Store = new InMemoryRepositoryFileStore(Memory);
        Paths = new FakePathResolver(Memory);
        Environment = new FakeProcessEnvironment(
            currentDirectory: Root,
            homeDirectory: Path.Combine(Memory.Root, "home"),
            tempDirectory: Path.Combine(Memory.Root, "tmp"),
            isFileSystemCaseInsensitive: !Memory.CaseSensitive);
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero));
        FileRepositoryTreeProbe tree = new(Memory, Paths, Environment);
        Configs = new FileKyberWeaveConfigRepository(tree, Memory, Paths, Environment);
        Scaffolder = new DocsScaffolder(
            Store,
            Configs,
            new FileRepositoryFileLister(new GitlessProgramRunner(), Memory),
            tree,
            Paths,
            Environment,
            Clock,
            ShippedKyberStandards.Templates);
        CacheSafety = new AnalysisCacheSafety(Memory, Paths, Environment, _git);
    }

    /// <summary>The in-memory file system the whole host reads and writes.</summary>
    public InMemoryFileSystem Memory { get; }

    /// <summary>The checkout root, a folder under the in-memory file system's root.</summary>
    public string Root { get; }

    /// <summary>The repository file store over <see cref="Memory"/>.</summary>
    public IRepositoryFileStore Store { get; }

    /// <summary>The path resolver over <see cref="Memory"/>.</summary>
    public IPathResolver Paths { get; }

    /// <summary>The environment, whose working directory is <see cref="Root"/>.</summary>
    public IProcessEnvironment Environment { get; }

    /// <summary>The fixed clock the scaffolder stamps dates from.</summary>
    public FakeTimeProvider Clock { get; }

    /// <summary>The configuration repository over <see cref="Memory"/>.</summary>
    public IKyberWeaveConfigRepository Configs { get; }

    /// <summary>The scaffolder over the in-memory seams.</summary>
    public DocsScaffolder Scaffolder { get; }

    /// <summary>The cache-safety check over the in-memory checkout and a git that reports no work tree.</summary>
    public AnalysisCacheSafety CacheSafety { get; }

    /// <summary>Scaffolds <see cref="Root"/>.</summary>
    public ScaffoldResult Scaffold(
        string? docsRoot = null,
        string owner = "unassigned",
        bool force = false,
        bool kyberStandards = false,
        bool reviewGates = true) =>
        Scaffolder.Scaffold(Root, docsRoot, owner, force, kyberStandards, reviewGates);

    /// <summary>Loads the host configuration of <see cref="Root"/>.</summary>
    public KyberWeaveConfig LoadConfig() => Configs.Load(Root);

    /// <summary>The loader over this checkout with the given ontology.</summary>
    public DocumentLoader Loader(OntologyConfig ontology) => new(Store, Root, Paths, Environment, ontology);

    /// <summary>The loader over this checkout with one documentation root.</summary>
    public DocumentLoader Loader(string docsRelativeRoot) => new(Store, Root, Paths, Environment, docsRelativeRoot);

    /// <summary>The validator over this checkout with the given ontology.</summary>
    public DocSpecValidator Validator(OntologyConfig ontology) => new(Root, ontology, Store, Paths, Environment);

    /// <summary>The standards-folder migration over this checkout with the given configuration.</summary>
    public StandardsFolderMigration Migration(KyberWeaveConfig config) =>
        new(Memory, Root, config, Paths, Environment, new FakeIdGenerator());

    /// <summary>The registry validator over this checkout with the given configuration.</summary>
    public ConfigRegValidator RegistryValidator(KyberWeaveConfig config) => new(Root, config, Store, Paths, Environment);

    /// <summary>The full path of a repository-relative path written with forward slashes.</summary>
    public string FullPath(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Reads a repository-relative file.</summary>
    public string Read(string relativePath) => Memory.ReadAllText(FullPath(relativePath));

    /// <summary>Writes a repository-relative file, creating its folder.</summary>
    public void Write(string relativePath, string content)
    {
        string path = FullPath(relativePath);
        Memory.CreateDirectory(Path.GetDirectoryName(path)!);
        Memory.WriteAllText(path, content);
    }

    /// <summary>Creates a repository-relative folder.</summary>
    public void CreateDirectory(string relativePath) => Memory.CreateDirectory(FullPath(relativePath));

    /// <summary>Whether a repository-relative file exists.</summary>
    public bool FileExists(string relativePath) => Memory.FileExists(FullPath(relativePath));

    /// <summary>Whether a repository-relative folder exists.</summary>
    public bool DirectoryExists(string relativePath) => Memory.DirectoryExists(FullPath(relativePath));

    /// <summary>Every file under <paramref name="directory"/>, however deep.</summary>
    public string[] AllFiles(string? directory = null) =>
        [.. Memory.EnumerateFiles(directory ?? Root, "*", recursive: true)];

    /// <summary>A program runner for which git is not installed, so a lister walks the file system instead.</summary>
    private sealed class GitlessProgramRunner : IProgramRunner
    {
        public ProcessResult Run(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan? timeout = null,
            long? maxOutputBytes = null,
            CancellationToken cancellationToken = default) =>
            throw new Win32Exception(2);
    }
}
