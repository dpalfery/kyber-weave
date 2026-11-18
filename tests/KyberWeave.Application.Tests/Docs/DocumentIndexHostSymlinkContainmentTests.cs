using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <see cref="DocumentIndexHost.ComputeDocsStamp()"/> never changes when a symlink
/// encountered during the walk is edited outside the root, whether it escapes the root or
/// points within it, ensuring the stamp fingerprints only the actual documentation tree and
/// not content accessible through links.
/// </summary>
public sealed class DocumentIndexHostSymlinkContainmentTests
{
    private readonly SettableClock _clock = new();
    private readonly InMemoryFileSystem _files;
    private readonly string _root;
    private readonly string _outside;

    public DocumentIndexHostSymlinkContainmentTests()
    {
        _files = new InMemoryFileSystem(_clock);
        _root = Path.Combine(_files.Root, "repo");
        _outside = Path.Combine(_files.Root, "outside-root");
        _files.CreateDirectory(_root);
        _files.CreateDirectory(_outside);
    }

    // The in-memory file system stamps a write with its clock, so a file's modification time is set by moving the
    // clock to it and rewriting the file.
    private void SetLastWrite(string path, DateTime utc)
    {
        string content = _files.ReadAllText(path);
        _clock.Now = new DateTimeOffset(utc, TimeSpan.Zero);
        _files.WriteAllText(path, content);
    }

    /// <summary>
    /// A docs root with a directory symlink to the sibling temp directory: adding or
    /// removing a .md file inside the sibling directory does not change the stamp returned
    /// by <see cref="DocumentIndexHost.ComputeDocsStamp()"/>.
    /// </summary>
    [Fact]
    public void DirectorySymlinkToOutsideRootDoesNotAffectStamp()
    {
        // Arrange: Set up repo root structure with a real markdown file inside
        string docsDir = Path.Combine(_root, "docs");
        _files.CreateDirectory(docsDir);
        string insideFile = Path.Combine(docsDir, "inside.md");
        _files.WriteAllText(insideFile, "# Inside");
        var fixedTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        SetLastWrite(insideFile, fixedTime);

        // Create a directory symlink in the docs root pointing to the outside directory
        string symlinkDir = Path.Combine(docsDir, "linked-outside");
        _files.CreateDirectoryLink(symlinkDir, _outside);

        // Create a DocumentIndexHost pointing to this root
        DocumentIndexHost host = CreateHost(_root, ["docs"]);

        // Act: Compute the initial stamp
        long initialStamp = host.ComputeDocsStamp();

        // Now add a file to the outside directory (which is linked)
        string outsideFile = Path.Combine(_outside, "outside.md");
        _files.WriteAllText(outsideFile, "# Outside");
        SetLastWrite(outsideFile, fixedTime.AddHours(1));

        // Compute stamp again — it should be unchanged because the symlink is not descended
        long stampAfterAddingOutsideFile = host.ComputeDocsStamp();

        // Assert: The stamp should not change when content is added to the linked directory
        Assert.Equal(initialStamp, stampAfterAddingOutsideFile);

        // Act: Now remove the file from the outside directory
        _files.DeleteFile(outsideFile);

        // Compute stamp again
        long stampAfterRemovingOutsideFile = host.ComputeDocsStamp();

        // Assert: The stamp should still equal the initial stamp
        Assert.Equal(initialStamp, stampAfterRemovingOutsideFile);
    }

    /// <summary>
    /// The configured catalog path itself replaced by a file-level symlink to a file in
    /// the sibling directory: the symlink contributes nothing to the stamp, so the stamp
    /// remains equal to a configuration with no catalog file at all, regardless of edits
    /// to the symlink target.
    /// </summary>
    [Fact]
    public void CatalogAsFileSymlinkToOutsideIsExcludedFromStamp()
    {
        // Arrange: Set up docs root with one real markdown file and pin its mtime
        string docsDir = Path.Combine(_root, "docs");
        _files.CreateDirectory(docsDir);
        string insideFile = Path.Combine(docsDir, "inside.md");
        var fixedTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        _files.WriteAllText(insideFile, "# Inside");
        SetLastWrite(insideFile, fixedTime);

        // Step 1: Compute stamp WITHOUT any catalog file
        DocumentIndexHost hostWithoutCatalog = CreateHost(_root, ["docs"], "catalog.md");
        long stampWithoutCatalog = hostWithoutCatalog.ComputeDocsStamp();

        // Step 2: Create a real file in the outside directory (will be the symlink target)
        string outsideFile = Path.Combine(_outside, "catalog.md");
        _files.WriteAllText(outsideFile, "# Catalog");
        SetLastWrite(outsideFile, fixedTime);

        // Create a symlink in the root pointing to the outside file as the catalog
        string catalogSymlink = Path.Combine(_root, "catalog.md");
        _files.CreateFileLink(catalogSymlink, outsideFile);

        // Compute stamp with the symlinked catalog; on HEAD (before T6), the symlink
        // is counted because File.Exists follows the link, making this differ from
        // stampWithoutCatalog. After T6's fix, the symlink is skipped and this equals
        // stampWithoutCatalog, passing the assertion.
        DocumentIndexHost hostWithSymlink = CreateHost(_root, ["docs"], "catalog.md");
        long stampWithLinkedCatalog = hostWithSymlink.ComputeDocsStamp();

        // Assert: Symlink should contribute nothing (after T6 fix is applied).
        // RED on HEAD: assertion fails because stampWithLinkedCatalog != stampWithoutCatalog
        // (the symlink is counted, adding 1 to count and changing newest).
        Assert.Equal(stampWithoutCatalog, stampWithLinkedCatalog);

        // Step 3: Edit the outside file (content and mtime)
        _files.WriteAllText(outsideFile, "# Updated Catalog");
        var laterTime = fixedTime.AddHours(1);
        SetLastWrite(outsideFile, laterTime);

        // Recompute stamp; after T6, this should still equal stampWithoutCatalog
        // because the symlink is skipped entirely.
        DocumentIndexHost hostAfterEdit = CreateHost(_root, ["docs"], "catalog.md");
        long stampAfterEdit = hostAfterEdit.ComputeDocsStamp();

        // Assert: Stamp should remain unchanged, equal to the baseline without catalog.
        Assert.Equal(stampWithoutCatalog, stampAfterEdit);
    }

    /// <summary>
    /// An ordinary .md file added or removed inside the docs root (no symlink involved)
    /// still changes the stamp. This guards against the fix making the stamp inert or
    /// over-broad in excluding regular files.
    /// </summary>
    [Fact]
    public void OrdinaryFileAddOrRemoveInsideRootDoesChangeStamp()
    {
        // Arrange: Set up repo root structure with one markdown file
        string docsDir = Path.Combine(_root, "docs");
        _files.CreateDirectory(docsDir);
        string file1 = Path.Combine(docsDir, "doc1.md");
        _files.WriteAllText(file1, "# Doc 1");

        // Set LastWriteTimeUtc to a specific time so we can control it
        var fixedTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        SetLastWrite(file1, fixedTime);

        // Create a DocumentIndexHost pointing to this root
        DocumentIndexHost host = CreateHost(_root, ["docs"]);

        // Act: Compute the initial stamp
        long initialStamp = host.ComputeDocsStamp();

        // Now add a new file with a later timestamp
        string file2 = Path.Combine(docsDir, "doc2.md");
        _files.WriteAllText(file2, "# Doc 2");
        var laterTime = fixedTime.AddHours(1);
        SetLastWrite(file2, laterTime);

        // Compute stamp again
        long stampAfterAddingFile = host.ComputeDocsStamp();

        // Assert: The stamp should change because a new file with a later timestamp was added
        Assert.NotEqual(initialStamp, stampAfterAddingFile);

        // Act: Now remove the newer file
        _files.DeleteFile(file2);

        // Compute stamp again
        long stampAfterRemovingFile = host.ComputeDocsStamp();

        // Assert: The stamp should revert to (or at least change from) the middle value
        Assert.NotEqual(stampAfterAddingFile, stampAfterRemovingFile);
    }

    /// <summary>
    /// Helper to create a DocumentIndexHost with the specified configuration.
    /// </summary>
    /// <summary>
    /// A catalog configured beneath a symlinked directory contributes nothing to the stamp,
    /// even though the file itself is not a link.
    /// </summary>
    [Fact]
    public void CatalogBeneathSymlinkedDirectoryIsExcludedFromStamp()
    {
        string docsDir = Path.Combine(_root, "docs");
        _files.CreateDirectory(docsDir);
        string insideFile = Path.Combine(docsDir, "inside.md");
        _files.WriteAllText(insideFile, "# Inside");
        SetLastWrite(insideFile, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        long stampWithoutCatalog = CreateHost(_root, ["docs"], "docs/linked/catalog.md").ComputeDocsStamp();

        string outsideCatalog = Path.Combine(_outside, "catalog.md");
        _files.WriteAllText(outsideCatalog, "# Catalog");
        SetLastWrite(outsideCatalog, new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        _files.CreateDirectoryLink(Path.Combine(docsDir, "linked"), _outside);

        long stampWithLinkedCatalog = CreateHost(_root, ["docs"], "docs/linked/catalog.md").ComputeDocsStamp();

        Assert.Equal(stampWithoutCatalog, stampWithLinkedCatalog);
    }

    /// <summary>
    /// A host whose factories are inert: <see cref="DocumentIndexHost.ComputeDocsStamp()"/>
    /// reads the filesystem directly and never calls them.
    /// </summary>
    private DocumentIndexHost CreateHost(
        string repoRoot,
        IReadOnlyList<string>? docsRelativeRoots = null,
        string? catalogRelativePath = null)
    {
        // Create minimal factories that satisfy the DocumentIndexHost constructor.
        // The factories themselves don't need to be called for ComputeDocsStamp() to work.
        Func<ICodeGraphResolver> resolverFactory = () =>
            new FakeCodeGraphResolver();

        Func<DocumentSet> documentSetFactory = () =>
            new DocumentSet { Documents = [] };

        docsRelativeRoots ??= [OntologyConfig.DefaultDocsRoot];

        return new DocumentIndexHost(
            repoRoot,
            resolverFactory,
            documentSetFactory,
            new InMemoryRepositoryFileStore(_files),
            new FakePathResolver(_files),
            new FakeProcessEnvironment(currentDirectory: repoRoot, isFileSystemCaseInsensitive: !_files.CaseSensitive),
            docsRelativeRoots,
            catalogRelativePath);
    }

    private sealed class SettableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
