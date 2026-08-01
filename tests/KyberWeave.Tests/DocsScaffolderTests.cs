using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Bootstrapping a host repository. The property that matters most is the last one: what
/// <c>docs init</c> emits must itself pass <c>docs validate</c>, or adoption starts with
/// the tool reporting failures against its own scaffolding.
/// </summary>
public sealed class DocsScaffolderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(_temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData("docs")]
    [InlineData("6-Docs")]
    [InlineData("doc")]
    [InlineData("documentation")]
    public void DetectsAnExistingConventionalDocsRoot(string existing)
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, existing));

        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal(existing, result.DocsRoot);
        Assert.True(result.DocsRootDetected);
    }

    [Fact]
    public void PrefersDocsWhenSeveralConventionalRootsExist()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "6-Docs"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));

        Assert.Equal("docs", DocsScaffolder.Scaffold(_temp.Path).DocsRoot);
    }

    [Fact]
    public void FallsBackToDocsWhenNoConventionalRootExists()
    {
        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal("docs", result.DocsRoot);
        Assert.True(Directory.Exists(Path.Combine(_temp.Path, "docs")));
    }

    [Fact]
    public void AnExplicitDocsRootOverridesDetection()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));

        var result = DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        Assert.Equal("handbook", result.DocsRoot);
        Assert.False(result.DocsRootDetected);
        Assert.True(File.Exists(Path.Combine(_temp.Path, "handbook", "catalog.md")));
    }

    [Fact]
    public void TheEmittedConfigPointsAtTheResolvedDocsRoot()
    {
        DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        var config = KyberWeaveConfigLoader.Load(_temp.Path);

        Assert.Equal("handbook", config.Ontology.DocsRoot);
    }

    /// <summary>
    /// The product defaults carry five DevOps exclusions from the repository the ontology
    /// was first built for. A fresh host must not inherit them.
    /// </summary>
    [Fact]
    public void TheEmittedConfigClearsInheritedFileExclusions()
    {
        DocsScaffolder.Scaffold(_temp.Path);

        var config = KyberWeaveConfigLoader.Load(_temp.Path);

        Assert.Empty(config.Ontology.ExcludedFiles);
    }

    [Fact]
    public void ExistingFilesAreLeftAloneAndReportedAsSuch()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));
        File.WriteAllText(Path.Combine(_temp.Path, "docs", "catalog.md"), "hand written");

        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal("hand written", Read("docs/catalog.md"));
        Assert.False(result.Files.Single(f => f.RelativePath == "docs/catalog.md").Written);
        Assert.True(result.Files.Single(f => f.RelativePath == "docs/documentation-ontology.md").Written);
    }

    [Fact]
    public void ForceOverwritesExistingFiles()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));
        File.WriteAllText(Path.Combine(_temp.Path, "docs", "catalog.md"), "hand written");

        var result = DocsScaffolder.Scaffold(_temp.Path, force: true);

        Assert.NotEqual("hand written", Read("docs/catalog.md"));
        Assert.True(result.Files.Single(f => f.RelativePath == "docs/catalog.md").Written);
    }

    /// <summary>
    /// Every <c>KW-DOC-SPEC-001</c> diagnostic tells the author to read
    /// <c>&lt;docs-root&gt;/documentation-ontology.md</c>. Before <c>docs init</c> existed
    /// that hint pointed at a file the tool never produced.
    /// </summary>
    [Fact]
    public void TheOntologyReferenceTheDiagnosticHintNamesIsEmitted()
    {
        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.True(File.Exists(
            Path.Combine(_temp.Path, result.DocsRoot, "documentation-ontology.md")));
    }

    [Fact]
    public void TheSeededCatalogSuppliesTheOwnerTheScaffoldedDocumentsClaim()
    {
        DocsScaffolder.Scaffold(_temp.Path, owner: "platform-team");

        var set = new DocumentLoader(_temp.Path, "docs").Load();

        Assert.Contains("platform-team", set.Owners);
        Assert.All(set.Documents, d => Assert.Equal("platform-team", d.Frontmatter.Owner));
    }

    /// <summary>The whole point: a freshly initialized repository validates clean.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("handbook")]
    public void AFreshlyScaffoldedCorpusPassesDocsValidate(string? docsRoot)
    {
        var result = DocsScaffolder.Scaffold(_temp.Path, docsRoot);

        var ontology = KyberWeaveConfigLoader.Load(_temp.Path).Ontology;
        var set = new DocumentLoader(_temp.Path, ontology).Load();
        var report = new DocSpecValidator(_temp.Path, ontology).Validate(set);

        Assert.Equal(2, set.Documents.Count);
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
        Assert.Equal(result.DocsRoot, ontology.DocsRoot);
    }
}
