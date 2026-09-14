using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>Proves that project-local MCP bindings do not cross a shared parent directory.</summary>
public sealed class McpRepositoryRootTests : IDisposable
{
    private readonly TempDirectory _parent = new();

    [Fact]
    public void DocsInitEstablishesTheCurrentDirectoryAsAnMcpRoot()
    {
        DocsScaffolder.Scaffold(_parent.Path);

        string resolved = RepositoryRootResolver.Resolve([], _parent.Path, null);

        Assert.Equal(Path.GetFullPath(_parent.Path), resolved);
    }

    [Fact]
    public void ProjectLocalBindingsKeepQueriesInsideTheirOwnRepository()
    {
        string first = Path.Combine(_parent.Path, "first-repository");
        string second = Path.Combine(_parent.Path, "second-repository");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        DocsScaffolder.Scaffold(first, owner: "first-team");
        DocsScaffolder.Scaffold(second, owner: "second-team");
        WriteMarker(first, "first-only-marker");
        WriteMarker(second, "second-only-marker");

        string firstRoot = RepositoryRootResolver.Resolve(["--repo-root", "."], first, null);
        string secondRoot = RepositoryRootResolver.Resolve(["--repo-root", "."], second, null);

        IReadOnlyList<DocumentHit> firstHits = CreateHost(firstRoot).Current().Explore(
            "first-only-marker", maxDocs: 5, charBudget: DocumentIndex.DefaultCharBudget);
        IReadOnlyList<DocumentHit> secondHits = CreateHost(secondRoot).Current().Explore(
            "second-only-marker", maxDocs: 5, charBudget: DocumentIndex.DefaultCharBudget);

        Assert.Contains(firstHits, hit => hit.Document.RelativePath == "docs/first-only.md");
        Assert.DoesNotContain(firstHits, hit => hit.Document.RelativePath == "docs/second-only.md");
        Assert.Contains(secondHits, hit => hit.Document.RelativePath == "docs/second-only.md");
        Assert.DoesNotContain(secondHits, hit => hit.Document.RelativePath == "docs/first-only.md");
    }

    [Fact]
    public void AnUninitializedWorkingDirectoryFailsInsteadOfGuessingAParentRepository()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            RepositoryRootResolver.Resolve([], _parent.Path, null));

        Assert.Contains("kyber-weave docs init", error.Message, StringComparison.Ordinal);
        Assert.Contains("will not guess a parent repository", error.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _parent.Dispose();

    private static DocumentIndexHost CreateHost(string root)
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(root);
        return new DocumentIndexHost(
            root,
            () => CodeGraphResolverAdapter.ForRepository(root),
            () => new DocumentLoader(root, config.Ontology).Load(),
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);
    }

    private static void WriteMarker(string root, string marker)
    {
        File.WriteAllText(
            Path.Combine(root, "docs", marker.Replace("-marker", string.Empty, StringComparison.Ordinal) + ".md"),
            $"# {marker}\n\nThis repository owns {marker}.\n");
    }
}
