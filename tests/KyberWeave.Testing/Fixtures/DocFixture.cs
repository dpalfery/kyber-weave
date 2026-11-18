using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests;

/// <summary>
/// A documentation corpus for the loader and validator, in one of two modes. <see cref="Create"/> builds a real
/// directory on disk, for a test whose subject is the disk itself (an adapter or an end-to-end run).
/// <see cref="CreateInMemory"/> builds the same corpus on an <see cref="InMemoryFileSystem"/> and reads it through
/// the same loader and validator over the file-system seam, so a unit test needs no disk. The two modes share every
/// member, so a test switches with that one call.
/// </summary>
internal sealed class DocFixture : IDisposable
{
    /// <summary>Creates the fixture. A factory, not <c>new</c>, because the fluent methods return
    /// this instance and the analyzer cannot follow that ownership across an assembly.</summary>
    public static DocFixture Create() => new();

    /// <summary>Creates the fixture over a new in-memory file system; nothing touches the disk.</summary>
    public static DocFixture CreateInMemory() => new(new InMemoryFileSystem());

    private readonly InMemoryFileSystem? _memory;

    /// <summary>The checkout root: a temporary directory on disk, or a folder under the in-memory file system's root.</summary>
    public string Root { get; }

    /// <summary>Gets the in-memory file system, or null for a fixture on disk.</summary>
    public InMemoryFileSystem? Memory => _memory;

    /// <summary>Gets the repository file store the loader and validator read: the in-memory store, or the physical one.</summary>
    public IRepositoryFileStore Store { get; }

    /// <summary>Gets the path resolver over the same file system.</summary>
    public IPathResolver Paths { get; }

    /// <summary>Gets the environment the loader and validator read: a fake rooted at <see cref="Root"/> in memory.</summary>
    public IProcessEnvironment Environment { get; }

    public DocFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sf-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "6-Docs"));
        Store = DocsPorts.Files;
        Paths = DocsPorts.Paths;
        Environment = DocsPorts.Environment;
    }

    /// <summary>Creates a fixture for another checkout (a worktree) on the same in-memory file system as <paramref name="sibling"/>.</summary>
    /// <param name="sibling">The in-memory fixture whose file system the new checkout shares.</param>
    /// <param name="folder">The new checkout's folder name beside the sibling's.</param>
    public static DocFixture CreateInMemoryBeside(DocFixture sibling, string folder)
    {
        ArgumentNullException.ThrowIfNull(sibling);
        return new DocFixture(sibling._memory ?? throw new InvalidOperationException("The sibling must be in memory."), folder);
    }

    private DocFixture(InMemoryFileSystem memory, string folder = "docs-repo")
    {
        _memory = memory;
        Root = Path.Combine(memory.Root, folder);
        memory.CreateDirectory(Path.Combine(Root, "6-Docs"));
        Store = new InMemoryRepositoryFileStore(memory);
        Paths = new FakePathResolver(memory);
        Environment = new FakeProcessEnvironment(
            currentDirectory: Root,
            homeDirectory: Path.Combine(memory.Root, "home"),
            tempDirectory: Path.Combine(memory.Root, "tmp"),
            isFileSystemCaseInsensitive: !memory.CaseSensitive,
            applicationBaseDirectory: Path.Combine(memory.Root, "app"));
    }

    /// <summary>
    /// Writes a catalog with the vocabularies the validator checks against. The catalog
    /// is itself an in-scope document, so it carries conforming frontmatter — exactly as
    /// the real one must.
    /// </summary>
    public DocFixture WithCatalog()
    {
        Write("6-Docs/catalog.md", """
            ---
            id: system/catalog
            title: Component Catalog
            doc-type: index
            status: current
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---

            # Catalog

            | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            | --- | --- | --- | --- | --- | --- | --- | --- |
            | MotorcycleRAG API | Application | `1-Presentation/Api` | [README](x) | [docs](y) | API maintainers | 2026-07-21 | Current |
            | MotorcycleRAG system | System | repository root | [README](x) | [docs](y) | Maintainers | 2026-07-21 | Current |
            """);
        return this;
    }

    /// <summary>Documents other than the fixture catalog itself.</summary>
    public IReadOnlyList<DocumentModel> LoadSubjects() =>
        Load().Documents.Where(d => d.RelativePath != "6-Docs/catalog.md").ToList();

    public DocFixture WithSourceRoot(string relativePath)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (_memory is not null)
        {
            _memory.CreateDirectory(full);
            return this;
        }

        Directory.CreateDirectory(full);
        return this;
    }

    public DocFixture Write(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (_memory is not null)
        {
            _memory.CreateDirectory(Path.GetDirectoryName(full)!);
            _memory.WriteAllText(full, content);
            return this;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public DocumentSet Load() => Loader(OntologyConfig.ProductDefaults.WithDocsRoot(OntologyConfig.DefaultDocsRoot)).Load();

    public DiagnosticReport Validate() => Validate(OntologyConfig.ProductDefaults);

    /// <summary>Validates against a host ontology — the technologies a repository declared.</summary>
    public DiagnosticReport Validate(OntologyConfig ontology) =>
        new DocSpecValidator(Root, ontology, Store, Paths, Environment).Validate(Loader(ontology).Load());

    /// <summary>The loader over this corpus; the file seam is the in-memory store or the physical one, per mode.</summary>
    /// <param name="ontology">The ontology naming the roots, exclusions and catalog.</param>
    public DocumentLoader Loader(OntologyConfig ontology) => new(Store, Root, Paths, Environment, ontology);

    public void Dispose()
    {
        if (_memory is null && Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
