using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Text;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Regression tests for the three retrieval defects found by using the MCP server against
/// the real corpus: a generic doc-type word outranking the named subject, a title-only
/// stub winning section selection, and a bare symbol name joining across project
/// boundaries.
/// </summary>
public class DocumentIndexRankingTests
{
    /// <summary>
    /// "WebUI application overview architecture" returned the API, system and Azure
    /// architecture documents and never returned the Web UI one. Every candidate shares
    /// the word "architecture"; only one is about the Web UI, and it says so in its
    /// declared id — which exact-equality scoring never consulted for a free-text query.
    /// </summary>
    [Fact]
    public void Generic_DocType_Word_Does_Not_Outrank_The_Named_Subject()
    {
        var query = TextVectorizer.Vectorize("WebUI application overview architecture");

        var webui = DocumentIndex.Coverage("webui/architecture", query);
        var api = DocumentIndex.Coverage("api/architecture", query);
        var system = DocumentIndex.Coverage("system/architecture", query);

        Assert.Equal(1.0, webui);
        Assert.True(webui > api, $"webui {webui} should outrank api {api}");
        Assert.True(webui > system, $"webui {webui} should outrank system {system}");
    }

    /// <summary>
    /// People write "WebUI" as one word; the catalog writes the component as "Web UI".
    /// Neighbouring tokens are therefore also matched fused together.
    /// </summary>
    [Fact]
    public void Component_Matches_When_The_Query_Fuses_Adjacent_Words()
    {
        var query = TextVectorizer.Vectorize("tell me about the WebUI app");

        Assert.Equal(1.0, DocumentIndex.Coverage("Web UI", query));
    }

    [Fact]
    public void An_Unrelated_Query_Covers_No_Identity()
    {
        var query = TextVectorizer.Vectorize("database migration rollback");

        Assert.Equal(0.0, DocumentIndex.Coverage("webui/architecture", query));
    }
}

public class DocumentSectionTests
{
    /// <summary>
    /// The run before the first '##' is normally just the H1, which the caller already
    /// has as the title. It used to be emitted as a section and then win selection,
    /// because a two-word section scores near-perfect cosine similarity — so the answer
    /// was a heading and the caller had to read the whole file anyway.
    /// </summary>
    [Fact]
    public void A_TitleOnly_Preamble_Is_Not_A_Section()
    {
        var sections = DocumentLoader.SplitSections("""
            # MotorcycleRAG Web UI Architecture

            ## Overview

            The Web UI is a React SPA paired with an ASP.NET Core BFF.
            """);

        Assert.Single(sections);
        Assert.Equal("Overview", sections[0].Heading);
    }

    [Fact]
    public void A_Preamble_With_Real_Prose_Is_Kept()
    {
        var sections = DocumentLoader.SplitSections("""
            # Title

            This intro says something before the first subheading.

            ## Overview

            Body.
            """);

        Assert.Equal(2, sections.Count);
        Assert.Equal(string.Empty, sections[0].Heading);
        Assert.Contains("says something", sections[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Hash_Inside_A_Fenced_Block_Does_Not_Open_A_Section()
    {
        var sections = DocumentLoader.SplitSections("""
            ## Commands

            ```bash
            ## not a heading
            echo hi
            ```

            Text.
            """);

        Assert.Single(sections);
        Assert.Equal("Commands", sections[0].Heading);
    }
}

/// <summary>
/// The budget replaced "return exactly one section", which cost more than it saved: one
/// section is about a third of a median document, so the caller who needed the rest read
/// the whole file anyway and retrieval was a net loss.
/// </summary>
public sealed class ExcerptBudgetTests : IDisposable
{
    private readonly DocFixture _fixture = new();

    // The document declares "routing" in its identity as well as its prose. A real
    // document that answers a question does the same, and without it the fixture sits
    // below the relevance floor — which is the floor doing its job, not a defect.
    private const string Frontmatter = """
        ---
        id: api/routing
        title: Routing
        doc-type: architecture
        status: current
        component: MotorcycleRAG API
        source-root: 1-Presentation/Api
        owner: API maintainers
        last-reviewed: 2026-07-21
        ---
        # Routing

        """;

    private DocumentIndex Build(params (string Heading, string Body)[] sections)
    {
        var text = Frontmatter + string.Join("\n", sections.Select(s => $"## {s.Heading}\n\n{s.Body}\n"));
        _fixture.WithCatalog().WithSourceRoot("1-Presentation/Api").Write("6-Docs/webui/architecture.md", text);
        return DocumentIndex.Build(_fixture.Load(), CodeGraphResolverAdapter.ForRepository(_fixture.Root));
    }

    private static string Prose(string subject, int words) =>
        string.Join(' ', Enumerable.Repeat(subject, words));

    [Fact]
    public void A_Generous_Budget_Returns_Every_Section_And_Says_So()
    {
        var index = Build(
            ("Overview", Prose("routing", 40)),
            ("Components", Prose("routing", 40)),
            ("Testing", Prose("routing", 40)));

        var hit = index.Explore("routing", maxDocs: 1, charBudget: 60_000).Single();

        Assert.Equal(3, hit.Excerpt.Sections.Count);
        Assert.True(hit.Excerpt.IsComplete);
        Assert.Empty(hit.Excerpt.OmittedHeadings);
    }

    [Fact]
    public void A_Tight_Budget_Names_What_It_Left_Out()
    {
        var index = Build(
            ("Overview", Prose("routing", 200)),
            ("Components", Prose("routing", 200)),
            ("Testing", Prose("routing", 200)));

        var hit = index.Explore("routing", maxDocs: 1, charBudget: 1000).Single();

        Assert.False(hit.Excerpt.IsComplete);
        Assert.True(hit.Excerpt.BudgetExhausted);
        Assert.NotEmpty(hit.Excerpt.OmittedHeadings);
        Assert.Equal(3, hit.Excerpt.Sections.Count + hit.Excerpt.OmittedHeadings.Count);
    }

    /// <summary>
    /// A section dropped for irrelevance will not come back however much budget is
    /// offered, so it must not be reported as a budget casualty — telling the caller to
    /// retry with a bigger budget would waste a whole round trip.
    /// </summary>
    [Fact]
    public void An_Irrelevant_Section_Is_Not_Reported_As_A_Budget_Casualty()
    {
        var index = Build(
            ("Overview", Prose("routing", 40)),
            ("Unrelated", Prose("kangaroo", 40)));

        var hit = index.Explore("routing", maxDocs: 1, charBudget: 60_000).Single();

        Assert.Contains("Unrelated", hit.Excerpt.OmittedHeadings, StringComparer.Ordinal);
        Assert.False(hit.Excerpt.BudgetExhausted);
        Assert.False(hit.Excerpt.IsComplete);
    }

    /// <summary>
    /// Sections are chosen by relevance but emitted in file order: prose written to be
    /// read in sequence is confusing when shuffled.
    /// </summary>
    [Fact]
    public void Sections_Are_Returned_In_Document_Order()
    {
        var index = Build(
            ("Alpha", Prose("routing", 30)),
            ("Beta", Prose("routing", 90)),
            ("Gamma", Prose("routing", 30)));

        var hit = index.Explore("routing", maxDocs: 1, charBudget: 60_000).Single();

        Assert.Equal(["Alpha", "Beta", "Gamma"], hit.Excerpt.Sections.Select(s => s.Heading));
    }

    /// <summary>
    /// A single oversized section still comes back. Returning only a path would make the
    /// tool a directory listing — the exact failure that sent callers to Read.
    /// </summary>
    [Fact]
    public void The_Top_Section_Survives_A_Budget_Smaller_Than_Itself()
    {
        var index = Build(("Overview", Prose("routing", 2000)));

        var hit = index.Explore("routing", maxDocs: 1, charBudget: 1000).Single();

        Assert.Single(hit.Excerpt.Sections);
        Assert.Equal("Overview", hit.Excerpt.Sections[0].Heading);
    }

    /// <summary>Narrowing the result set must deepen it, not just shorten the list.</summary>
    [Fact]
    public void Fewer_Documents_Buys_More_Prose_Per_Document()
    {
        var index = Build(
            ("Overview", Prose("routing", 300)),
            ("Components", Prose("routing", 300)),
            ("Testing", Prose("routing", 300)));

        var deep = index.Explore("routing", maxDocs: 1, charBudget: 12_000).Single();
        var shallow = index.Explore("routing", maxDocs: 20, charBudget: 3_000).Single();

        Assert.True(deep.Excerpt.Sections.Count > shallow.Excerpt.Sections.Count,
            $"deep {deep.Excerpt.Sections.Count} should exceed shallow {shallow.Excerpt.Sections.Count}");
    }

    public void Dispose() => _fixture.Dispose();
}

public class CodeJoinScopingTests
{
    private static DocumentModel Doc(string sourceRoot) => new()
    {
        RelativePath = "6-Docs/MotorcycleRag.WebUI/architecture.md",
        FilePath = "/tmp/architecture.md",
        HasFrontmatter = true,
        Frontmatter = new DocumentFrontmatter { Id = "webui/architecture", SourceRoot = sourceRoot }
    };

    private static CodeGraphNode Node(string kind, string path, string language, int line = 1) =>
        new($"id-{path}", kind, "AuthProvider", "AuthProvider", path, language, line);

    /// <summary>
    /// The Web UI architecture document declares code-refs [AuthProvider], meaning the
    /// React context provider. The index also holds a C# property of that name in the API.
    /// Taking the first match joined the document to a different project and a different
    /// language; the document's own source-root settles it.
    /// </summary>
    [Fact]
    public void A_Join_Prefers_A_Symbol_Beneath_The_Documents_SourceRoot()
    {
        var nodes = new[]
        {
            Node("property", "1-Presentation/MotorcycleRAG.API/Services/CurrentUserService.cs", "csharp", 42),
            Node("function", "1-Presentation/MotorcycleRag.WebUI/src/contexts/AuthContext.tsx", "typescript", 17)
        };

        var join = DocumentIndex.ToJoin(Doc("1-Presentation/MotorcycleRag.WebUI"), "AuthProvider", nodes);

        Assert.Equal("1-Presentation/MotorcycleRag.WebUI/src/contexts/AuthContext.tsx:17", join.Location);
        Assert.Equal("function", join.Kind);
        Assert.True(join.InSourceRoot);
        Assert.Equal(0, join.OtherCandidates);
    }

    /// <summary>
    /// When nothing beneath the source-root matches, the repo-wide fallback still answers
    /// — but flags itself, because that is exactly the weak evidence a reader must check.
    /// </summary>
    [Fact]
    public void A_Join_Outside_The_SourceRoot_Is_Flagged()
    {
        var nodes = new[]
        {
            Node("property", "1-Presentation/MotorcycleRAG.API/Services/CurrentUserService.cs", "csharp", 42)
        };

        var join = DocumentIndex.ToJoin(Doc("1-Presentation/MotorcycleRag.WebUI"), "AuthProvider", nodes);

        Assert.False(join.InSourceRoot);
        Assert.Contains("CurrentUserService.cs", join.Location, StringComparison.Ordinal);
    }

    /// <summary>Remaining ambiguity inside the source-root is reported, not hidden.</summary>
    [Fact]
    public void Remaining_SameNamed_Candidates_Are_Counted()
    {
        var nodes = new[]
        {
            Node("class", "1-Presentation/MotorcycleRag.WebUI/src/a.tsx", "typescript", 3),
            Node("class", "1-Presentation/MotorcycleRag.WebUI/src/b.tsx", "typescript", 9)
        };

        var join = DocumentIndex.ToJoin(Doc("1-Presentation/MotorcycleRag.WebUI"), "AuthProvider", nodes);

        Assert.Equal(1, join.OtherCandidates);
    }

    /// <summary>
    /// A declaration beats an incidental member of the same name: documentation calling
    /// something "X" means the class X far more often than a property that returns X.
    /// </summary>
    [Fact]
    public void A_Declaration_Outranks_A_SameNamed_Property()
    {
        var nodes = new[]
        {
            Node("property", "1-Presentation/MotorcycleRag.WebUI/src/a.tsx", "typescript", 3),
            Node("class", "1-Presentation/MotorcycleRag.WebUI/src/z.tsx", "typescript", 9)
        };

        var join = DocumentIndex.ToJoin(Doc("1-Presentation/MotorcycleRag.WebUI"), "AuthProvider", nodes);

        Assert.Equal("class", join.Kind);
    }

    [Fact]
    public void An_Unresolved_Reference_Reports_Itself_As_Unresolved()
    {
        var join = DocumentIndex.ToJoin(Doc("1-Presentation/MotorcycleRag.WebUI"), "Gone", []);

        Assert.Equal("unresolved", join.Kind);
        Assert.Equal(string.Empty, join.Location);
    }
}
