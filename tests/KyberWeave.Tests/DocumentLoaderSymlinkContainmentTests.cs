using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Symlinks encountered while walking a docs root must not be descended into or
/// followed. A docs root containing a symlink whose target leaves the configured root
/// should never read files from the target, and the catalog path itself should be
/// skipped if it resolves through a symlink.
/// </summary>
public sealed class DocumentLoaderSymlinkContainmentTests : IDisposable
{
    private readonly TempDirectory _repoRoot = new();
    private readonly TempDirectory _outside = new();

    /// <summary>Deletes the repository and outside temp directories.</summary>
    public void Dispose()
    {
        _repoRoot.Dispose();
        _outside.Dispose();
    }

    /// <summary>Writes <c>.kyber-weave/kyber-weave.yml</c> in the repository root.</summary>
    private void WriteConfig(string yaml)
    {
        string configDir = Path.Combine(_repoRoot.Path, ".kyber-weave");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "kyber-weave.yml"), yaml);
    }

    /// <summary>
    /// Writes <paramref name="content"/> at <paramref name="path"/> under the repository root,
    /// or under the outside directory when <paramref name="root"/> is anything but <c>docs</c>.
    /// </summary>
    private void WriteDocument(string path, string content, string root = "docs")
    {
        string fullPath = Path.Combine(root == "docs" ? _repoRoot.Path : _outside.Path, path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, content);
    }

    /// <summary>
    /// A directory symlink inside a docs root pointing outside it: Load().Documents
    /// does not contain files from the linked directory, and Load() does not throw.
    /// </summary>
    [Fact]
    public void DirectorySymlinkToOutsideIsNotDescended()
    {
        // Arrange
        WriteConfig("""
            ontology:
              docs-root: docs
            """);

        // Create a normal document inside the docs root
        WriteDocument("docs/inside.md", """
            ---
            id: inside
            title: Inside Document
            doc-type: reference
            status: current
            ---
            # Inside
            This document is inside the docs root.
            """);

        // Create a document outside the docs root
        WriteDocument("outside/escaped.md", """
            ---
            id: outside
            title: Outside Document
            doc-type: reference
            status: current
            ---
            # Outside
            This document is outside the docs root.
            """, root: "outside");

        // Create a symlink inside docs root pointing to the outside directory
        string docsPath = Path.Combine(_repoRoot.Path, "docs");
        string outsideLink = Path.Combine(docsPath, "symlinked-outside");
        Directory.CreateSymbolicLink(outsideLink, _outside.Path);

        OntologyConfig config = OntologyConfig.ProductDefaults.WithDocsRoot("docs");
        DocumentLoader loader = new(_repoRoot.Path, config);

        // Act
        DocumentSet result = loader.Load();

        // Assert
        Assert.Single(result.Documents, doc => doc.Frontmatter.Id == "inside");
        Assert.DoesNotContain(result.Documents, doc => doc.Frontmatter.Id == "outside");
    }

    /// <summary>
    /// The catalog path itself is replaced by a file-level symlink to a file
    /// outside the docs root: the symlinked catalog is excluded from Load().Documents,
    /// Load() does not throw, and Components/Owners are empty rather than reflecting
    /// the outside catalog's content.
    /// </summary>
    [Fact]
    public void CatalogAsFileSymlinkToOutsideIsExcluded()
    {
        // Arrange
        WriteConfig("""
            ontology:
              docs-root: docs
            """);

        // Create a normal document inside the docs root
        WriteDocument("docs/normal.md", """
            ---
            id: normal
            title: Normal Document
            doc-type: reference
            status: current
            component: TestComponent
            owner: TestOwner
            ---
            # Normal
            A normal document.
            """);

        // Create an outside catalog with content
        WriteDocument("outside/catalog.md", """
            ---
            id: catalog
            title: Outside Catalog
            doc-type: index
            status: current
            ---
            # Catalog

            | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            |---|---|---|---|---|---|---|---|
            | OutsideComponent | service | outside | A component from outside | link | OutsideOwner | 2026-01-01 | current |
            """, root: "outside");

        // Replace the catalog path with a file symlink to the outside catalog
        string catalogPath = Path.Combine(_repoRoot.Path, "docs", "catalog.md");
        string outsideCatalogPath = Path.Combine(_outside.Path, "outside", "catalog.md");
        File.CreateSymbolicLink(catalogPath, outsideCatalogPath);

        OntologyConfig config = OntologyConfig.ProductDefaults.WithDocsRoot("docs");
        DocumentLoader loader = new(_repoRoot.Path, config);

        // Act
        DocumentSet result = loader.Load();

        // Assert
        // The normal document should be loaded
        Assert.Single(result.Documents, doc => doc.Frontmatter.Id == "normal");

        // The catalog should not contribute a document
        Assert.DoesNotContain(result.Documents, doc => doc.Frontmatter.Id == "catalog");

        // Components and Owners should be empty (not read from the symlinked catalog)
        Assert.NotNull(result.Components);
        Assert.Empty(result.Components);
        Assert.NotNull(result.Owners);
        Assert.Empty(result.Owners);
    }

    /// <summary>
    /// Guard: an ordinary catalog and ordinary nested .md files, with no symlinks,
    /// are still loaded exactly as today. Ensures the fix does not break normal behavior.
    /// </summary>
    [Fact]
    public void OrdinaryFilesWithoutSymlinksLoadAsToday()
    {
        // Arrange
        WriteConfig("""
            ontology:
              docs-root: docs
            """);

        // Create a normal catalog with component and owner info
        WriteDocument("docs/catalog.md", """
            ---
            id: catalog
            title: Catalog
            doc-type: index
            status: current
            ---
            # Catalog

            | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            |---|---|---|---|---|---|---|---|
            | MyComponent | service | docs | Overview text | link | MyOwner | 2026-01-01 | current |
            """);

        // Create nested documents
        WriteDocument("docs/section1/doc1.md", """
            ---
            id: doc1
            title: Document One
            doc-type: reference
            status: current
            component: MyComponent
            owner: MyOwner
            ---
            # Document One
            Content of document one.
            """);

        WriteDocument("docs/section2/subsection/doc2.md", """
            ---
            id: doc2
            title: Document Two
            doc-type: reference
            status: current
            component: MyComponent
            owner: MyOwner
            ---
            # Document Two
            Content of document two.
            """);

        OntologyConfig config = OntologyConfig.ProductDefaults.WithDocsRoot("docs");
        DocumentLoader loader = new(_repoRoot.Path, config);

        // Act
        DocumentSet result = loader.Load();

        // Assert - all documents loaded
        Assert.Equal(3, result.Documents.Count);
        Assert.Single(result.Documents, doc => doc.Frontmatter.Id == "catalog");
        Assert.Single(result.Documents, doc => doc.Frontmatter.Id == "doc1");
        Assert.Single(result.Documents, doc => doc.Frontmatter.Id == "doc2");

        // Assert - catalog vocabularies are populated
        Assert.NotNull(result.Components);
        Assert.Contains("MyComponent", result.Components);
        Assert.NotNull(result.Owners);
        Assert.Contains("MyOwner", result.Owners);
    }

    /// <summary>
    /// A catalog configured beneath a symlinked directory is refused, even though the file
    /// itself is not a link: the walk would never reach it, so neither may the catalog read.
    /// </summary>
    [Fact]
    public void CatalogBeneathSymlinkedDirectoryIsExcluded()
    {
        WriteDocument("docs/normal.md", """
            ---
            id: normal
            title: Normal Document
            doc-type: reference
            status: current
            ---
            # Normal
            A normal document.
            """);

        WriteDocument("catalog.md", OutsideCatalog, root: "outside");
        Directory.CreateSymbolicLink(Path.Combine(_repoRoot.Path, "docs", "linked"), _outside.Path);

        OntologyConfig config = OntologyConfig.ProductDefaults
            .WithDocsRoot("docs")
            .Clone(catalogPath: "docs/linked/catalog.md");
        DocumentSet result = new DocumentLoader(_repoRoot.Path, config).Load();

        Assert.Single(result.Documents, doc => doc.Frontmatter.Id == "normal");
        Assert.DoesNotContain(result.Documents, doc => doc.Frontmatter.Id == "catalog");
        Assert.Empty(result.Components);
        Assert.Empty(result.Owners);
    }

    /// <summary>
    /// A configured root that is itself a symlink still supplies its catalog. Root selection
    /// is the host's decision; only links encountered beneath the root are refused.
    /// </summary>
    [Fact]
    public void CatalogInsideSymlinkedConfiguredRootIsLoaded()
    {
        WriteDocument("catalog.md", OutsideCatalog, root: "outside");
        Directory.CreateSymbolicLink(Path.Combine(_repoRoot.Path, "docs"), _outside.Path);

        OntologyConfig config = OntologyConfig.ProductDefaults.WithDocsRoot("docs");
        DocumentSet result = new DocumentLoader(_repoRoot.Path, config).Load();

        Assert.Single(result.Documents, doc => doc.Frontmatter.Id == "catalog");
        Assert.Contains("OutsideComponent", result.Components);
        Assert.Contains("OutsideOwner", result.Owners);
    }

    private const string OutsideCatalog = """
        ---
        id: catalog
        title: Outside Catalog
        doc-type: index
        status: current
        ---
        # Catalog

        | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
        |---|---|---|---|---|---|---|---|
        | OutsideComponent | service | outside | A component from outside | link | OutsideOwner | 2026-01-01 | current |
        """;
}
