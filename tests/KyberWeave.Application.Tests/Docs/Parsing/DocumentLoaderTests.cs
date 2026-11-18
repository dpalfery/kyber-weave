using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Parsing;

/// <summary>The corpus loader over the repository file seam: the checkout lives in memory, links included.</summary>
public sealed class DocumentLoaderTests
{
    private const string Root = "/repo";

    private const string RelativeRoot = "repo";

    private readonly InMemoryFileSystem _disk = new();

    private readonly FakePathResolver _paths;

    private readonly FakeProcessEnvironment _environment = new(currentDirectory: Root);

    public DocumentLoaderTests() => _paths = new FakePathResolver(_disk);

    private DocumentLoader Loader(OntologyConfig? config = null) =>
        new(new InMemoryRepositoryFileStore(_disk), Root, _paths, _environment,
            config ?? OntologyConfig.ProductDefaults.WithDocsRoot("docs"));

    [Fact]
    public void ARelativeRepositoryRootIsResolvedAgainstTheInjectedWorkingDirectory()
    {
        const string working = "/kw/work";
        _disk.CreateDirectory($"{working}/{RelativeRoot}/docs");
        _disk.WriteAllText($"{working}/{RelativeRoot}/docs/a.md", "plain text\n");
        DocumentLoader loader = new(
            new InMemoryRepositoryFileStore(_disk),
            RelativeRoot,
            _paths,
            new FakeProcessEnvironment(currentDirectory: working),
            OntologyConfig.ProductDefaults.WithDocsRoot("docs"));

        DocumentSet set = loader.Load();

        Assert.Equal(["docs/a.md"], set.Documents.Select(document => document.RelativePath).ToArray());
    }

    private void Write(string relative, string text)
    {
        string path = Root + "/" + relative;
        _disk.CreateDirectory(Path.GetDirectoryName(path)!);
        _disk.WriteAllText(path, text);
    }

    [Fact]
    public void ItParsesFrontmatterAndKeepsTheBodyAndLinksOfEveryMarkdownFileUnderTheRoot()
    {
        Write("docs/b.md", "---\nid: b\ntitle: B\ndoc-type: reference\nstatus: current\n---\nSee [a](a.md).\n");
        Write("docs/a.md", "plain text, no frontmatter\n");
        Write("docs/notes.txt", "not markdown");

        DocumentSet set = Loader().Load();

        Assert.Equal(["docs/a.md", "docs/b.md"], set.Documents.Select(document => document.RelativePath).ToArray());
        DocumentModel plain = set.Documents[0];
        Assert.False(plain.HasFrontmatter);
        Assert.Equal("plain text, no frontmatter\n", plain.Body);
        DocumentModel governed = set.Documents[1];
        Assert.True(governed.HasFrontmatter);
        Assert.Equal("b", governed.Frontmatter.Id);
        Assert.Equal(DocType.Reference, governed.DocType);
        Assert.Equal(DocStatus.Current, governed.Status);
        Assert.Contains("a.md", governed.BodyLinks);
    }

    [Fact]
    public void AMissingDocumentationRootYieldsNoDocuments()
    {
        Assert.Empty(Loader().Load().Documents);
    }

    [Fact]
    public void ADirectoryThatIsALinkIsNeverWalked()
    {
        Write("elsewhere/secret.md", "outside the docs root");
        Write("docs/inside.md", "inside");
        _disk.CreateDirectoryLink(Root + "/docs/linked", Root + "/elsewhere");

        string[] paths = Loader().Load().Documents.Select(document => document.RelativePath).ToArray();

        Assert.Equal(["docs/inside.md"], paths);
    }

    [Fact]
    public void TheCatalogSuppliesTheComponentAndOwnerVocabularies()
    {
        Write(
            "docs/catalog.md",
            "| Component | Type | Source root | Overview | Detail | Owner | Last reviewed | Status |\n"
            + "|---|---|---|---|---|---|---|---|\n"
            + "| Billing | service | src | o | d | Platform | 2026-01-01 | current |\n");

        DocumentSet set = Loader().Load();

        Assert.Contains("Billing", set.Components!);
        Assert.Contains("Platform", set.Owners!);
        Assert.DoesNotContain("Component", set.Components!);
    }

    [Fact]
    public void ALinkedCatalogContributesNoVocabularyAndNoDocument()
    {
        Write("elsewhere/catalog.md", "| x | Billing | a | b | c | Platform | e | f |\n");
        _disk.CreateDirectory(Root + "/docs");
        _disk.CreateFileLink(Root + "/docs/catalog.md", Root + "/elsewhere/catalog.md");

        DocumentSet set = Loader().Load();

        Assert.Empty(set.Components!);
        Assert.Empty(set.Owners!);
        Assert.DoesNotContain(set.Documents, document => document.RelativePath == "docs/catalog.md");
    }

    [Fact]
    public void AnArchivedEntryIsReadByName()
    {
        Write("docs/archive/old.md", "---\nid: old\n---\nbody\n");

        DocumentModel document = Loader().LoadArchivedDocument("docs/archive/old.md");

        Assert.Equal("old", document.Frontmatter.Id);
    }

    [Fact]
    public void AnArchivedEntryOutsideTheRepositoryIsRefusedByReason()
    {
        ArchivedEntryRefusalException refusal = Assert.Throws<ArchivedEntryRefusalException>(
            () => Loader().LoadArchivedDocument("../outside.md"));

        Assert.Equal(ArchivedEntryRefusalReason.EscapesRepositoryRoot, refusal.Reason);
    }

    [Fact]
    public void AnArchivedEntryBehindALinkIsRefusedByReason()
    {
        Write("elsewhere/x.md", "x");
        _disk.CreateDirectory(Root + "/docs");
        _disk.CreateDirectoryLink(Root + "/docs/archive", Root + "/elsewhere");

        ArchivedEntryRefusalException refusal = Assert.Throws<ArchivedEntryRefusalException>(
            () => Loader().LoadArchivedDocument("docs/archive/x.md"));

        Assert.Equal(ArchivedEntryRefusalReason.CrossesSymbolicLink, refusal.Reason);
    }
}
