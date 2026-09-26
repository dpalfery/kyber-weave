using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <see cref="DocumentIndexHost.ComputeDocsStamp()"/> never changes when a symlink
/// encountered during the walk is edited outside the root, whether it escapes the root or
/// points within it, ensuring the stamp fingerprints only the actual documentation tree and
/// not content accessible through links.
/// </summary>
public sealed class DocumentIndexHostSymlinkContainmentTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly TempDirectory _outside = new();

    public void Dispose()
    {
        _root.Dispose();
        _outside.Dispose();
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
        string docsDir = Path.Combine(_root.Path, "docs");
        Directory.CreateDirectory(docsDir);
        string insideFile = Path.Combine(docsDir, "inside.md");
        File.WriteAllText(insideFile, "# Inside");
        var fixedTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(insideFile, fixedTime);

        // Create a directory symlink in the docs root pointing to the outside directory
        string symlinkDir = Path.Combine(docsDir, "linked-outside");
        Directory.CreateSymbolicLink(symlinkDir, _outside.Path);

        // Create a DocumentIndexHost pointing to this root
        DocumentIndexHost host = CreateHost(_root.Path, ["docs"]);

        // Act: Compute the initial stamp
        long initialStamp = host.ComputeDocsStamp();

        // Now add a file to the outside directory (which is linked)
        string outsideFile = Path.Combine(_outside.Path, "outside.md");
        File.WriteAllText(outsideFile, "# Outside");
        File.SetLastWriteTimeUtc(outsideFile, fixedTime.AddHours(1));

        // Compute stamp again — it should be unchanged because the symlink is not descended
        long stampAfterAddingOutsideFile = host.ComputeDocsStamp();

        // Assert: The stamp should not change when content is added to the linked directory
        Assert.Equal(initialStamp, stampAfterAddingOutsideFile);

        // Act: Now remove the file from the outside directory
        File.Delete(outsideFile);

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
        string docsDir = Path.Combine(_root.Path, "docs");
        Directory.CreateDirectory(docsDir);
        string insideFile = Path.Combine(docsDir, "inside.md");
        var fixedTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(insideFile, "# Inside");
        File.SetLastWriteTimeUtc(insideFile, fixedTime);

        // Step 1: Compute stamp WITHOUT any catalog file
        DocumentIndexHost hostWithoutCatalog = CreateHost(_root.Path, ["docs"], "catalog.md");
        long stampWithoutCatalog = hostWithoutCatalog.ComputeDocsStamp();

        // Step 2: Create a real file in the outside directory (will be the symlink target)
        string outsideFile = Path.Combine(_outside.Path, "catalog.md");
        File.WriteAllText(outsideFile, "# Catalog");
        File.SetLastWriteTimeUtc(outsideFile, fixedTime);

        // Create a symlink in the root pointing to the outside file as the catalog
        string catalogSymlink = Path.Combine(_root.Path, "catalog.md");
        File.CreateSymbolicLink(catalogSymlink, outsideFile);

        // Compute stamp with the symlinked catalog; on HEAD (before T6), the symlink
        // is counted because File.Exists follows the link, making this differ from
        // stampWithoutCatalog. After T6's fix, the symlink is skipped and this equals
        // stampWithoutCatalog, passing the assertion.
        DocumentIndexHost hostWithSymlink = CreateHost(_root.Path, ["docs"], "catalog.md");
        long stampWithLinkedCatalog = hostWithSymlink.ComputeDocsStamp();

        // Assert: Symlink should contribute nothing (after T6 fix is applied).
        // RED on HEAD: assertion fails because stampWithLinkedCatalog != stampWithoutCatalog
        // (the symlink is counted, adding 1 to count and changing newest).
        Assert.Equal(stampWithoutCatalog, stampWithLinkedCatalog);

        // Step 3: Edit the outside file (content and mtime)
        File.WriteAllText(outsideFile, "# Updated Catalog");
        var laterTime = fixedTime.AddHours(1);
        File.SetLastWriteTimeUtc(outsideFile, laterTime);

        // Recompute stamp; after T6, this should still equal stampWithoutCatalog
        // because the symlink is skipped entirely.
        DocumentIndexHost hostAfterEdit = CreateHost(_root.Path, ["docs"], "catalog.md");
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
        string docsDir = Path.Combine(_root.Path, "docs");
        Directory.CreateDirectory(docsDir);
        string file1 = Path.Combine(docsDir, "doc1.md");
        File.WriteAllText(file1, "# Doc 1");

        // Set LastWriteTimeUtc to a specific time so we can control it
        var fixedTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file1, fixedTime);

        // Create a DocumentIndexHost pointing to this root
        DocumentIndexHost host = CreateHost(_root.Path, ["docs"]);

        // Act: Compute the initial stamp
        long initialStamp = host.ComputeDocsStamp();

        // Now add a new file with a later timestamp
        string file2 = Path.Combine(docsDir, "doc2.md");
        File.WriteAllText(file2, "# Doc 2");
        var laterTime = fixedTime.AddHours(1);
        File.SetLastWriteTimeUtc(file2, laterTime);

        // Compute stamp again
        long stampAfterAddingFile = host.ComputeDocsStamp();

        // Assert: The stamp should change because a new file with a later timestamp was added
        Assert.NotEqual(initialStamp, stampAfterAddingFile);

        // Act: Now remove the newer file
        File.Delete(file2);

        // Compute stamp again
        long stampAfterRemovingFile = host.ComputeDocsStamp();

        // Assert: The stamp should revert to (or at least change from) the middle value
        Assert.NotEqual(stampAfterAddingFile, stampAfterRemovingFile);
    }

    /// <summary>
    /// Helper to create a DocumentIndexHost with the specified configuration.
    /// </summary>
    private static DocumentIndexHost CreateHost(
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
            docsRelativeRoots,
            catalogRelativePath);
    }
}
