using KyberWeave.Application.Review;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using System.ComponentModel;

namespace KyberWeave.Tests;

/// <summary>
/// A repository checkout in memory with the shipped scaffolder and configuration repository over it: the in-memory
/// stand-in for a scratch folder a test used to scaffold into and then validate.
/// </summary>
/// <remarks>
/// Every port is the shipped class over <see cref="InMemoryFileSystem"/>, so nothing touches the disk, starts git or
/// reads the clock beyond the date a generated document carries. Git is answered as "not installed", so the file lister
/// walks the in-memory tree, exactly as it does in a folder that is not a work tree.
/// </remarks>
internal sealed class MemoryScaffoldedRepository
{
    /// <summary>Creates a checkout root under a new in-memory file system.</summary>
    /// <param name="folder">The folder name of the checkout under the file system root.</param>
    public MemoryScaffoldedRepository(string folder = "repo")
    {
        Memory = new InMemoryFileSystem();
        Root = Path.Combine(Memory.Root, folder);
        Memory.CreateDirectory(Root);
        Store = new InMemoryRepositoryFileStore(Memory);
        Paths = new FakePathResolver(Memory);
        Environment = new FakeProcessEnvironment(
            currentDirectory: Root,
            homeDirectory: Path.Combine(Memory.Root, "home"),
            tempDirectory: Path.Combine(Memory.Root, "tmp"),
            isFileSystemCaseInsensitive: !Memory.CaseSensitive,
            applicationBaseDirectory: Path.Combine(Memory.Root, "app"));
        FileRepositoryTreeProbe tree = new(Memory, Paths, Environment);
        Config = new FileKyberWeaveConfigRepository(tree, Memory, Paths, Environment);
        Scaffolding = new DocsScaffolder(
            Store,
            Config,
            new FileRepositoryFileLister(new GitNotInstalled(), Memory),
            tree,
            Paths,
            Environment,
            TimeProvider.System,
            ShippedKyberStandards.Templates);
    }

    /// <summary>Gets the in-memory file system.</summary>
    public InMemoryFileSystem Memory { get; }

    /// <summary>Gets the checkout root.</summary>
    public string Root { get; }

    /// <summary>Gets the repository file store over <see cref="Memory"/>.</summary>
    public IRepositoryFileStore Store { get; }

    /// <summary>Gets the path resolver over <see cref="Memory"/>.</summary>
    public IPathResolver Paths { get; }

    /// <summary>Gets the environment, with <see cref="Root"/> as its working directory.</summary>
    public IProcessEnvironment Environment { get; }

    /// <summary>Gets the shipped configuration repository over <see cref="Memory"/>.</summary>
    public IKyberWeaveConfigRepository Config { get; }

    /// <summary>Gets the shipped scaffolder over <see cref="Memory"/>.</summary>
    public IDocsScaffoldingService Scaffolding { get; }

    /// <summary>Writes a file under <see cref="Root"/>, creating its folders.</summary>
    /// <param name="relativePath">The slash-separated path under the root.</param>
    /// <param name="content">The file text.</param>
    public void Write(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Memory.CreateDirectory(Path.GetDirectoryName(full)!);
        Memory.WriteAllText(full, content);
    }

    private sealed class GitNotInstalled : IProgramRunner
    {
        public Core.Processes.ProcessResult Run(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan? timeout = null,
            long? maxOutputBytes = null,
            CancellationToken cancellationToken = default) =>
            throw new Win32Exception($"{program} is not installed in the in-memory repository.");
    }
}
