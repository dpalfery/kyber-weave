using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Search;

/// <summary>
/// What the documentation stamp counts, proven over the in-memory file system: a stamp moves when a document
/// in the tree changes and holds still when a change is only reachable through a symbolic link.
/// </summary>
public sealed class DocumentIndexHostStampTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";
    private static readonly string Outside = OperatingSystem.IsWindows() ? @"C:\outside" : "/outside";

    private readonly InMemoryFileSystem _files = new();

    public DocumentIndexHostStampTests()
    {
        _files.CreateDirectory(Path.Combine(Root, "docs"));
        _files.CreateDirectory(Outside);
        _files.WriteAllText(Path.Combine(Root, "docs", "inside.md"), "# Inside");
    }

    [Fact]
    public void AddingADocumentInTheTreeChangesTheStamp()
    {
        DocumentIndexHost host = Host(["docs"]);
        long before = host.ComputeDocsStamp();

        _files.WriteAllText(Path.Combine(Root, "docs", "second.md"), "# Second");

        Assert.NotEqual(before, host.ComputeDocsStamp());
    }

    [Fact]
    public void AddingADocumentBehindADirectoryLinkLeavesTheStampUnchanged()
    {
        _files.CreateDirectoryLink(Path.Combine(Root, "docs", "linked"), Outside);
        DocumentIndexHost host = Host(["docs"]);
        long before = host.ComputeDocsStamp();

        _files.WriteAllText(Path.Combine(Outside, "outside.md"), "# Outside");

        Assert.Equal(before, host.ComputeDocsStamp());
    }

    [Fact]
    public void ARootWithNoDirectoryHasNoStamp()
    {
        Assert.Equal(0, Host(["missing"]).ComputeDocsStamp());
    }

    [Fact]
    public void ACatalogOutsideTheRootsChangesTheStamp()
    {
        long without = Host(["docs"]).ComputeDocsStamp();
        _files.WriteAllText(Path.Combine(Root, "catalog.md"), "# Catalog");

        Assert.NotEqual(without, Host(["docs"], "catalog.md").ComputeDocsStamp());
    }

    [Fact]
    public void ACatalogReachedThroughALinkIsLeftOutOfTheStamp()
    {
        _files.WriteAllText(Path.Combine(Outside, "catalog.md"), "# Catalog");
        _files.CreateDirectoryLink(Path.Combine(Root, "linked"), Outside);
        long without = Host(["docs"]).ComputeDocsStamp();

        Assert.Equal(without, Host(["docs"], "linked/catalog.md").ComputeDocsStamp());
    }

    [Fact]
    public void ACatalogInsideARootIsCountedOnce()
    {
        long without = Host(["docs"]).ComputeDocsStamp();

        Assert.Equal(without, Host(["docs"], "docs/inside.md").ComputeDocsStamp());
    }

    [Fact]
    public void ARelativeRepositoryRootIsResolvedAgainstTheCurrentDirectory()
    {
        DocumentIndexHost host = new(
            "repo",
            () => throw new InvalidOperationException("The stamp never resolves the code graph."),
            () => new DocumentSet { Documents = [] },
            new InMemoryRepositoryFileStore(_files),
            FakePathResolver.OverEmptyFileSystem(),
            new FakeProcessEnvironment(currentDirectory: Path.GetDirectoryName(Root)!));

        Assert.Equal(Root, host.RepositoryRoot);
    }

    private DocumentIndexHost Host(IReadOnlyList<string> roots, string? catalog = null) => new(
        Root,
        () => throw new InvalidOperationException("The stamp never resolves the code graph."),
        () => new DocumentSet { Documents = [] },
        new InMemoryRepositoryFileStore(_files),
        FakePathResolver.OverEmptyFileSystem(),
        new FakeProcessEnvironment(),
        roots,
        catalog);
}
