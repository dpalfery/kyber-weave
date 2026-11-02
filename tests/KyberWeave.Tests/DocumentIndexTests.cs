using System.Collections.ObjectModel;
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
    public void GenericDocTypeWordDoesNotOutrankTheNamedSubject()
    {
        Dictionary<string, double> query = TextVectorizer.Vectorize("WebUI application overview architecture");

        double webui = DocumentIndex.Coverage("webui/architecture", query);
        double api = DocumentIndex.Coverage("api/architecture", query);
        double system = DocumentIndex.Coverage("system/architecture", query);

        Assert.Equal(1.0, webui);
        Assert.True(webui > api, $"webui {webui} should outrank api {api}");
        Assert.True(webui > system, $"webui {webui} should outrank system {system}");
    }

    /// <summary>
    /// People write "WebUI" as one word; the catalog writes the component as "Web UI".
    /// Neighbouring tokens are therefore also matched fused together.
    /// </summary>
    [Fact]
    public void ComponentMatchesWhenTheQueryFusesAdjacentWords()
    {
        Dictionary<string, double> query = TextVectorizer.Vectorize("tell me about the WebUI app");

        Assert.Equal(1.0, DocumentIndex.Coverage("Web UI", query));
    }

    [Fact]
    public void AnUnrelatedQueryCoversNoIdentity()
    {
        Dictionary<string, double> query = TextVectorizer.Vectorize("database migration rollback");

        Assert.Equal(0.0, DocumentIndex.Coverage("webui/architecture", query));
    }

    [Fact]
    public void CoverageSplitsCamelCaseIdentities()
    {
        Dictionary<string, double> query = TextVectorizer.Vectorize("dash");

        // "KyberDash" splits into "kyber" and "dash" (2 tokens); query has "dash" (1 token matched).
        double coverage = DocumentIndex.Coverage("KyberDash", query);

        Assert.Equal(0.5, coverage);
    }

    [Fact]
    public void CoverageSymmetricPrefixMatchesWhenQueryTokenStartsWithIdentityToken()
    {
        // Identity token "dash" (|p|=4 >= 3); query token "dashboard" (|q|=9 >= 3) starts with "dash"
        Dictionary<string, double> query = TextVectorizer.Vectorize("dashboard");

        double coverage = DocumentIndex.Coverage("dash", query);

        Assert.Equal(1.0, coverage);
    }

    [Fact]
    public void CoverageSymmetricPrefixMatchesWhenIdentityTokenStartsWithQueryToken()
    {
        // Identity token "dashboard" (|p|=9 >= 3); starts with query token "dash" (|q|=4 >= 3)
        Dictionary<string, double> query = TextVectorizer.Vectorize("dash");

        double coverage = DocumentIndex.Coverage("dashboard", query);

        Assert.Equal(1.0, coverage);
    }

    [Fact]
    public void CoverageDoesNotMatchPrefixWhenShorterTokenLengthIsLessThanThree()
    {
        // Shorter token with length 2 (< 3) must NOT match as prefix.
        // Identity "iteration" vs query token "it" (length 2)
        Dictionary<string, double> queryWithShortToken = new(StringComparer.Ordinal) { ["it"] = 1.0 };
        double identityCoverage = DocumentIndex.Coverage("iteration", queryWithShortToken);

        Assert.Equal(0.0, identityCoverage);

        // Identity "db" (length 2) vs query token "database" (length 8)
        Dictionary<string, double> queryDatabase = TextVectorizer.Vectorize("database");
        double shortIdentityCoverage = DocumentIndex.Coverage("db", queryDatabase);

        Assert.Equal(0.0, shortIdentityCoverage);
    }
}

public class DocumentKeywordScoringTests
{
    private static DocumentModel DocWithKeywords(params string[] keywords) => new()
    {
        RelativePath = "docs/sample.md",
        FilePath = "/tmp/docs/sample.md",
        HasFrontmatter = true,
        DocType = DocType.Architecture,
        Status = DocStatus.Current,
        Frontmatter = new DocumentFrontmatter
        {
            Id = "sample/doc",
            Keywords = new Collection<string>(keywords)
        }
    };

    [Fact]
    public void ExactKeywordMatchAddsKeywordWeightInScoring()
    {
        DocumentModel doc = DocWithKeywords("dashboard", "tauri");

        // Exact match adds KeywordWeight = 4.0
        Assert.Equal(4.0, DocumentIndex.ScoreExact(doc, "dashboard"));
        Assert.Equal(4.0, DocumentIndex.ScoreExact(doc, "DASHBOARD")); // case-insensitive
        Assert.Equal(0.0, DocumentIndex.ScoreExact(doc, "unrelated"));
    }

    /// <summary>Partial keyword matches score by the keyword weight, capped at 1.5 times it however many match.</summary>
    [Fact]
    public void PartialKeywordMatchScoresViaKeywordPartialWeightCappedAtOnePointFiveMultiplier()
    {
        DocumentModel singleKeywordDoc = DocWithKeywords("dashboard");
        DocumentModel twoKeywordsDoc = DocWithKeywords("dashboard", "tauri");
        DocumentModel threeKeywordsDoc = DocWithKeywords("dashboard", "tauri", "terminal");

        // 1 matching keyword: 1.0 * KeywordPartialWeight (3.0) = 3.0
        Dictionary<string, double> querySingle = TextVectorizer.Vectorize("dashboard");
        Assert.Equal(3.0, DocumentIndex.ScorePartialIdentity(singleKeywordDoc, querySingle));

        // 2 matching keywords: 2.0 capped at 1.5 * KeywordPartialWeight (3.0) = 4.5
        Dictionary<string, double> queryTwo = TextVectorizer.Vectorize("dashboard tauri");
        Assert.Equal(4.5, DocumentIndex.ScorePartialIdentity(twoKeywordsDoc, queryTwo));

        // 3 matching keywords: 3.0 capped at 1.5 * KeywordPartialWeight (3.0) = 4.5
        Dictionary<string, double> queryThree = TextVectorizer.Vectorize("dashboard tauri terminal");
        Assert.Equal(4.5, DocumentIndex.ScorePartialIdentity(threeKeywordsDoc, queryThree));

        // Unrelated query scores 0.0
        Dictionary<string, double> queryUnrelated = TextVectorizer.Vectorize("unrelated query");
        Assert.Equal(0.0, DocumentIndex.ScorePartialIdentity(twoKeywordsDoc, queryUnrelated));
    }

    /// <summary>
    /// Declared keywords are indexed lower-cased, so lookups match regardless of the case
    /// the author wrote, and undeclared words are absent.
    /// </summary>
    [Fact]
    public void ByKeywordInvertedMapIndexesDeclaredKeywordsCaseInsensitively()
    {
        DocumentModel doc = DocWithKeywords("dashboard", "Tauri");
        DocumentCorpus corpus = DocumentCorpus.Build(new DocumentSet { Documents = [doc] });
        DocumentIndex index = DocumentIndex.Build(corpus, FakeCodeGraphResolver.WithSymbols());

        Assert.True(index.ByKeyword.TryGetValue("dashboard", out List<DocumentModel>? dashboardDocs));
        Assert.Contains(doc, dashboardDocs);

        Assert.True(index.ByKeyword.TryGetValue("tauri", out List<DocumentModel>? tauriDocs));
        Assert.Contains(doc, tauriDocs);

        Assert.False(index.ByKeyword.ContainsKey("unindexed"));
    }
}

/// <summary>
/// What each doc-type is worth as current guidance. A coding standard is the answer to "how
/// should this be written", so it is neither demoted like a record of intent nor boosted
/// above the architecture document on every question that happens to mention code.
/// </summary>
public class DocumentAuthorityTests
{
    private static DocumentModel Doc(DocType docType) => new DocumentModel
    {
        RelativePath = "6-Docs/x.md",
        FilePath = "/tmp/6-Docs/x.md",
        HasFrontmatter = true,
        DocType = docType,
        Status = DocStatus.Current
    };

    [Fact]
    public void ACodingStandardCarriesUndemotedAuthority()
    {
        Assert.Equal(DocumentIndex.Authority(Doc(DocType.Reference)), DocumentIndex.Authority(Doc(DocType.CodingStandard)));
        Assert.True(DocumentIndex.Authority(Doc(DocType.Plan)) < DocumentIndex.Authority(Doc(DocType.CodingStandard)));
    }
}

/// <summary>
/// Title cosine used to count question-scaffolding words. "how do I make a sandwich"
/// shared only "make" with a plan titled "Make 0 unrepresentable…" and still cleared
/// the 0.25 floor after plan demotion.
/// </summary>
public sealed class TitleScoringTests : IDisposable
{
    private readonly DocFixture _fixture = new();

    private DocumentIndex BuildPlanTitledMakeUnrepresentable()
    {
        _fixture.WithCatalog().Write("6-Docs/plans/t2.md", """
            ---
            id: plans/tasks/t2-formatter-sentinel
            title: T2 — Make 0 unrepresentable for an unreported counter
            doc-type: plan
            status: needs-review
            component: MotorcycleRAG API
            owner: API maintainers
            last-reviewed: 2026-09-06
            ---
            # T2 — Make 0 unrepresentable for an unreported counter

            ## Detail

            The formatter must render an em-dash when a counter was not exported.
            """);
        return DocumentIndex.Build(_fixture.Load(), FakeCodeGraphResolver.WithSymbols());
    }

    [Fact]
    public void QuestionScaffoldingInATitleDoesNotAnswerAnUnrelatedQuery()
    {
        DocumentIndex index = BuildPlanTitledMakeUnrepresentable();

        ExploreAnswer miss = index.Explore("how do I make a sandwich");

        Assert.True(miss.IsMiss,
            "expected a miss, got prose from: " + miss.SourceDocument?.RelativePath);
    }

    [Fact]
    public void ATitleThatNamesTheSubjectStillClearsTheFloor()
    {
        DocumentIndex index = BuildPlanTitledMakeUnrepresentable();

        ExploreAnswer hit = index.Explore("unrepresentable counter");
        string id = hit.SourceDocument!.Frontmatter.Id ?? hit.SourceDocument.RelativePath;

        Assert.Contains("plans/tasks/t2-formatter-sentinel", id, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scaffolding and corpus-ubiquity halves of informativeness are separate
    /// judgements, and only the first applies to titles. A corpus about agents says
    /// "agent" everywhere, which is exactly why the document titled for it must keep that
    /// word: filtering titles by ubiquity demoted the right document for "skill authoring".
    /// </summary>
    [Fact]
    public void TitleQueryDropsScaffoldingAndKeepsCorpusVocabulary()
    {
        Dictionary<string, double> query = DocumentIndex.TitleQuery(["make", "tell", "agent", "sandwich"]);

        Assert.Equal(["agent", "sandwich"], query.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    public void Dispose() => _fixture.Dispose();
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
    public void ATitleOnlyPreambleIsNotASection()
    {
        IReadOnlyList<DocumentSection> sections = DocumentLoader.SplitSections("""
            # MotorcycleRAG Web UI Architecture

            ## Overview

            The Web UI is a React SPA paired with an ASP.NET Core BFF.
            """);

        Assert.Single(sections);
        Assert.Equal("Overview", sections[0].Heading);
    }

    [Fact]
    public void APreambleWithRealProseIsKept()
    {
        IReadOnlyList<DocumentSection> sections = DocumentLoader.SplitSections("""
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
    public void AHashInsideAFencedBlockDoesNotOpenASection()
    {
        IReadOnlyList<DocumentSection> sections = DocumentLoader.SplitSections("""
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
    public void AJoinPrefersASymbolBeneathTheDocumentsSourceRoot()
    {
        CodeGraphNode[] nodes =
        [
            Node("property", "1-Presentation/MotorcycleRAG.API/Services/CurrentUserService.cs", "csharp", 42),
            Node("function", "1-Presentation/MotorcycleRag.WebUI/src/contexts/AuthContext.tsx", "typescript", 17)
        ];

        CodeJoin join = DocumentIndex.ToJoin(Doc("1-Presentation/MotorcycleRag.WebUI"), "AuthProvider", nodes);

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
    public void AJoinOutsideTheSourceRootIsFlagged()
    {
        CodeGraphNode[] nodes =
        [
            Node("property", "1-Presentation/MotorcycleRAG.API/Services/CurrentUserService.cs", "csharp", 42)
        ];

        CodeJoin join = DocumentIndex.ToJoin(Doc("1-Presentation/MotorcycleRag.WebUI"), "AuthProvider", nodes);

        Assert.False(join.InSourceRoot);
        Assert.Contains("CurrentUserService.cs", join.Location, StringComparison.Ordinal);
    }

    /// <summary>Remaining ambiguity inside the source-root is reported, not hidden.</summary>
    [Fact]
    public void RemainingSameNamedCandidatesAreCounted()
    {
        CodeGraphNode[] nodes =
        [
            Node("class", "1-Presentation/MotorcycleRag.WebUI/src/a.tsx", "typescript", 3),
            Node("class", "1-Presentation/MotorcycleRag.WebUI/src/b.tsx", "typescript", 9)
        ];

        CodeJoin join = DocumentIndex.ToJoin(Doc("1-Presentation/MotorcycleRag.WebUI"), "AuthProvider", nodes);

        Assert.Equal(1, join.OtherCandidates);
    }

    /// <summary>
    /// A declaration beats an incidental member of the same name: documentation calling
    /// something "X" means the class X far more often than a property that returns X.
    /// </summary>
    [Fact]
    public void ADeclarationOutranksASameNamedProperty()
    {
        CodeGraphNode[] nodes =
        [
            Node("property", "1-Presentation/MotorcycleRag.WebUI/src/a.tsx", "typescript", 3),
            Node("class", "1-Presentation/MotorcycleRag.WebUI/src/z.tsx", "typescript", 9)
        ];

        CodeJoin join = DocumentIndex.ToJoin(Doc("1-Presentation/MotorcycleRag.WebUI"), "AuthProvider", nodes);

        Assert.Equal("class", join.Kind);
    }

    [Fact]
    public void AnUnresolvedReferenceReportsItselfAsUnresolved()
    {
        CodeJoin join = DocumentIndex.ToJoin(Doc("1-Presentation/MotorcycleRag.WebUI"), "Gone", []);

        Assert.Equal("unresolved", join.Kind);
        Assert.Equal(string.Empty, join.Location);
    }
}

/// <summary>
/// A coding standard is looked up by technology, not by similarity: a portable agent names
/// the stack it is working in and must get this repository's standard for that stack, never
/// another stack's prose because it happened to share vocabulary. These tests pin what a key
/// may look like, which documents answer it, and how the returned prose is budgeted.
/// </summary>
public sealed class CodingStandardsLookupTests : IDisposable
{
    /// <summary>The technology's README, which is the standard itself.</summary>
    private const string CsharpStandardPath = "6-Docs/standards/csharp/README.md";

    /// <summary>A second document for the same technology, sorting after the README.</summary>
    private const string CsharpAppendixPath = "6-Docs/standards/csharp/appendix.md";

    private const string TestStandardPath = "6-Docs/standards/test/README.md";

    /// <summary>
    /// Declares the technology but is not a standard: the doc-type decides, so this must
    /// never answer a lookup whichever way the key is written.
    /// </summary>
    private const string ReferenceDeclaringCsharp = """
        ---
        id: reference/csharp
        title: C# reference
        doc-type: reference
        status: current
        technology: csharp
        component: MotorcycleRAG API
        owner: API maintainers
        last-reviewed: 2026-07-21
        ---

        # C# reference

        ## Symbols

        A reference document is prose about the stack, not the standard for it.
        """;

    /// <summary>
    /// A standard that declares no technology. The technology is the key, so there is
    /// nothing to look this document up by and no name to offer a caller.
    /// </summary>
    private const string UndeclaredTechnologyStandard = """
        ---
        id: standards/undeclared
        title: Undeclared coding standard
        doc-type: coding-standard
        status: current
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # Undeclared coding standard

        ## Overview

        A standard with no technology key is reachable by no key at all.
        """;

    private readonly DocFixture _fixture = new();

    /// <summary>One csharp standard, in a corpus of its own.</summary>
    private DocumentIndex OneStandard(string sections) =>
        IndexOf((CsharpStandardPath, StandardDocument("csharp", "standards/csharp", sections)));

    /// <summary>
    /// Two technologies' standards — the csharp one cites a rule id, the test one cites
    /// none — plus the two documents that must never answer a technology key.
    /// </summary>
    private DocumentIndex TwoTechnologies() => IndexOf(
        (CsharpStandardPath, StandardDocument("csharp", "standards/csharp", CsharpSections())),
        (TestStandardPath, StandardDocument("test", "standards/test", Sections(
            ("Overview", Prose("test", 40)),
            ("Conventions", "A test name states the guarantee it pins. " + Prose("test", 40))))),
        ("6-Docs/reference/csharp.md", ReferenceDeclaringCsharp),
        ("6-Docs/standards/undeclared/README.md", UndeclaredTechnologyStandard));

    /// <summary>
    /// Two csharp standards — the technology's README and an appendix beside it — so result
    /// ordering and the multi-document budget split are observable.
    /// </summary>
    private DocumentIndex TwoCsharpStandards() => IndexOf(
        (CsharpStandardPath, StandardDocument("csharp", "standards/csharp", SizedSections(3, 110))),
        (CsharpAppendixPath, StandardDocument("csharp", "standards/csharp-appendix", SizedSections(3, 110))));

    /// <summary>Writes the given documents into a corpus of their own and indexes it.</summary>
    private DocumentIndex IndexOf(params (string RelativePath, string Text)[] documents)
    {
        _fixture.WithCatalog();
        foreach ((string relativePath, string text) in documents)
        {
            _fixture.Write(relativePath, text);
        }

        return DocumentIndex.Build(_fixture.Load(), FakeCodeGraphResolver.WithSymbols());
    }

    /// <summary>
    /// The csharp standard as a real one is shaped: a short overview, a section past the
    /// 6000-character mark, and a rules section whose prose cites the rule id an agent has
    /// to follow.
    /// </summary>
    private static string CsharpSections() => Sections(
        ("Overview", Prose("csharp", 40)),
        ("Naming", Prose("naming", 1000)),
        ("Rules", "Every finding this standard raises cites KW-DOC-SPEC-007 in the report. " + Prose("csharp", 40)));

    /// <summary>
    /// A standard of evenly sized sections, so a budget test can reason about what fits
    /// without depending on how the prose happens to tokenize.
    /// </summary>
    private static string SizedSections(int count, int wordsPerSection)
    {
        List<(string Heading, string Body)> sections = new List<(string Heading, string Body)>();
        for (int index = 1; index <= count; index++)
        {
            sections.Add(($"Section {index}", Prose("rules", wordsPerSection)));
        }

        return Sections([.. sections]);
    }

    private static string Sections(params (string Heading, string Body)[] sections) =>
        string.Join("\n", sections.Select(section => $"## {section.Heading}\n\n{section.Body}\n"));

    private static string Prose(string subject, int words) =>
        string.Join(' ', Enumerable.Repeat(subject, words));

    /// <summary>
    /// A coding-standard document. <c>doc-type</c> is what marks it as a standard and
    /// <c>technology</c> is the key a caller looks it up by, so neither is ever omitted.
    /// </summary>
    /// <param name="technology">
    /// The technology the standard is written for, used as its <c>technology</c> declaration, in
    /// its title, and in the H1 beneath the frontmatter.
    /// </param>
    /// <param name="id">The frontmatter <c>id</c>, which is the identity a lookup reports back.</param>
    /// <param name="body">The markdown below the H1 — sections, or prose with no headings at all.</param>
    /// <param name="declaredTechnology">
    /// The scalar the <c>technology</c> key carries, when a test needs it written otherwise
    /// than as the bare value. Quoting is the only way padding survives the read: the YAML
    /// parser trims whitespace around a bare scalar, so a test that means to exercise how a
    /// padded declaration is matched has to ask for the quotes an author would write.
    /// </param>
    private static string StandardDocument(
        string technology,
        string id,
        string body,
        string? declaredTechnology = null) =>
        $"""
        ---
        id: {id}
        title: {technology} coding standard
        doc-type: coding-standard
        status: current
        technology: {declaredTechnology ?? technology}
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # {technology} coding standard
        """ + "\n\n" + body;

    /// <summary>
    /// A caller names a standard three ways: the bare technology, the configuration
    /// registry's property name, and that property as it is written in prose — wrapped in
    /// the angle brackets the registry uses. All three are one lookup.
    /// </summary>
    [Theory]
    [InlineData("csharp", CsharpStandardPath)]
    [InlineData("csharp-coding-standard", CsharpStandardPath)]
    [InlineData("<csharp-coding-standard>", CsharpStandardPath)]
    [InlineData("<csharp>", CsharpStandardPath)]
    [InlineData("  csharp  ", CsharpStandardPath)]
    [InlineData("CSHARP-Coding-Standard", CsharpStandardPath)]
    [InlineData("test-coding-standard", TestStandardPath)]
    [InlineData("TEST", TestStandardPath)]
    public void AKeyResolvesWhetherItNamesTheTechnologyTheSuffixOrTheRegistryProperty(string key, string standard)
    {
        DocumentIndex index = TwoTechnologies();

        DocumentHit hit = index.CodingStandards(key, charBudget: 60_000).Single();

        Assert.Equal(standard, hit.Document.RelativePath);
    }

    /// <summary>
    /// A key that names no technology finds nothing. Answering a question about Rust with
    /// the C# standard is worse than a miss: the caller cannot tell the two apart.
    /// </summary>
    [Theory]
    [InlineData("rust")]
    [InlineData("csharp-standard")]
    [InlineData("<csharp")]
    [InlineData("<csharp-coding-standard")]
    [InlineData("standards")]
    public void AnUnknownTechnologyFindsNoStandard(string key)
    {
        DocumentIndex index = TwoTechnologies();

        Assert.Empty(index.CodingStandards(key));
    }

    /// <summary>
    /// A key that normalizes to nothing is a miss, not the whole corpus: an empty string
    /// and a bare suffix each say "no technology", so there is nothing to answer with.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("<>")]
    [InlineData("< >")]
    [InlineData("-coding-standard")]
    public void AnEmptyKeyFindsNoStandard(string key)
    {
        DocumentIndex index = TwoTechnologies();

        Assert.Empty(index.CodingStandards(key));
    }

    /// <summary>
    /// Padding around a declared technology is not a second technology. The key a caller
    /// writes is clean, so the match trims the declaration rather than demanding the caller
    /// reproduce an author's stray spaces; and the list a caller chooses from offers the
    /// trimmed name, because a padded string would be an option no key can be written to
    /// select. Both halves have to hold: a trim that survives only one of them leaves a
    /// standard that is findable by a name the list never offers, or offered under a name
    /// that finds nothing.
    /// </summary>
    [Fact]
    public void ADeclaredTechnologyCarryingSurroundingWhitespaceAnswersTheCleanKeyAndIsListedTrimmed()
    {
        DocumentIndex index = IndexOf((CsharpStandardPath, StandardDocument(
            "csharp",
            "standards/csharp",
            SizedSections(1, 20),
            declaredTechnology: "'  csharp  '")));

        DocumentHit hit = index.CodingStandards("csharp", charBudget: 60_000).Single();

        Assert.Equal(CsharpStandardPath, hit.Document.RelativePath);
        Assert.Equal(["csharp"], index.CodingStandardTechnologies());
    }

    /// <summary>
    /// The doc-type decides. A reference document that declares csharp in its frontmatter is
    /// prose about the stack, and answering a standard lookup with it would hand the caller
    /// a document that never claimed to be the standard.
    /// </summary>
    [Fact]
    public void ADocumentThatIsNotACodingStandardDoesNotAnswerTheTechnologyKey()
    {
        DocumentIndex index = TwoTechnologies();

        DocumentHit hit = index.CodingStandards("csharp").Single();

        Assert.Equal(CsharpStandardPath, hit.Document.RelativePath);
    }

    /// <summary>
    /// A standard that declares no technology is reachable by no key and is offered as no
    /// technology: the list a caller chooses from holds declared names, and an absent one is
    /// not a choice.
    /// </summary>
    [Fact]
    public void ACodingStandardThatDeclaresNoTechnologyIsFoundAndListedByNothing()
    {
        DocumentIndex index = TwoTechnologies();

        Assert.Empty(index.CodingStandards("undeclared"));
        Assert.DoesNotContain(index.CodingStandardTechnologies(), string.IsNullOrWhiteSpace);
    }

    /// <summary>
    /// Ordered by relative path, ordinal, so the same corpus answers the same way twice.
    /// Ordinal matters here: the README sorts before an appendix beside it because uppercase
    /// precedes lowercase, and a case-insensitive sort would hand them back the other way.
    /// </summary>
    [Fact]
    public void ASecondStandardForOneTechnologyIsReturnedOrderedByRelativePathOrdinal()
    {
        DocumentIndex index = TwoCsharpStandards();

        List<string> paths = index.CodingStandards("csharp", charBudget: 60_000)
            .Select(hit => hit.Document.RelativePath)
            .ToList();

        Assert.Equal([CsharpStandardPath, CsharpAppendixPath], paths);
    }

    /// <summary>
    /// With no topic the caller asked for the standard itself, so every section comes back
    /// in the order it was written — rule ids included, because the rule a standard cites is
    /// part of the answer rather than decoration around it.
    /// </summary>
    [Fact]
    public void WithoutATopicTheStandardComesBackWholeInDocumentOrder()
    {
        DocumentIndex index = OneStandard(CsharpSections());

        DocumentHit hit = index.CodingStandards("csharp", charBudget: 60_000).Single();

        Assert.Equal(["Overview", "Naming", "Rules"], hit.Excerpt.Sections.Select(section => section.Heading));
        Assert.True(
            hit.Excerpt.IsComplete,
            "a 60000-character budget covers a standard whose sections total under 8000 characters.");
        Assert.Contains(
            "KW-DOC-SPEC-007",
            hit.Excerpt.Sections.Single(section => section.Heading == "Rules").Body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a topic the sections fill the budget in document order, and what did not fit
    /// is named by heading: the caller can ask again deliberately instead of reading the
    /// whole file to find out what it missed.
    /// </summary>
    [Fact]
    public void WithoutATopicTheBudgetFillsInDocumentOrderAndNamesWhatDidNotFit()
    {
        DocumentIndex index = OneStandard(SizedSections(3, 110));

        DocumentHit hit = index.CodingStandards("csharp", charBudget: 1000).Single();

        Assert.Equal(["Section 1", "Section 2"], hit.Excerpt.Sections.Select(section => section.Heading));
        Assert.Equal(["Section 3"], hit.Excerpt.OmittedHeadings);
        Assert.True(hit.Excerpt.BudgetExhausted, "a section a larger budget would have returned must say so.");
        Assert.False(hit.Excerpt.IsComplete);
    }

    /// <summary>
    /// The first section comes back even when it alone exceeds the budget, which is the rule
    /// <c>Explore</c> already follows. Returning only a path would make the tool a directory
    /// listing — the exact failure that sends a caller to Read instead.
    /// </summary>
    [Fact]
    public void WithoutATopicTheFirstSectionSurvivesABudgetSmallerThanItself()
    {
        DocumentIndex index = OneStandard(SizedSections(1, 1100));

        DocumentHit hit = index.CodingStandards("csharp", charBudget: 1000).Single();

        DocumentSection only = Assert.Single(hit.Excerpt.Sections);
        Assert.Equal("Section 1", only.Heading);
        Assert.True(
            only.Body.Length >= 6000,
            $"an oversized first section comes back whole ({only.Body.Length} characters); truncating it is the transport edge's job.");
        Assert.Empty(hit.Excerpt.OmittedHeadings);
    }

    /// <summary>
    /// A topic narrows the standard to the section that answers it, chosen the way
    /// <c>Explore</c> chooses a section: rarity-weighted terms, the topic's adjacent pairs
    /// fused into weak phrase matches, and document order for what is emitted. The sections
    /// it judged irrelevant are named, and are not reported as a budget casualty because no
    /// larger budget would bring them back.
    /// </summary>
    [Fact]
    public void ATopicBringsBackTheSectionThatAnswersItAndNamesTheRest()
    {
        DocumentIndex index = OneStandard(CsharpSections());

        DocumentHit hit = index.CodingStandards("csharp", topic: "naming", charBudget: 1000).Single();

        Assert.Equal(["Naming"], hit.Excerpt.Sections.Select(section => section.Heading));
        Assert.Equal(["Overview", "Rules"], hit.Excerpt.OmittedHeadings);
        Assert.False(
            hit.Excerpt.BudgetExhausted,
            "the other sections were dropped for irrelevance, so a larger budget would not bring them back.");
    }

    /// <summary>
    /// A topic no section answers returns no prose rather than the whole standard: the
    /// selection is relevance, and this is the outcome <c>Explore</c> gives a query that
    /// matches nothing.
    /// </summary>
    [Fact]
    public void ATopicThatNoSectionAnswersReturnsNoProse()
    {
        DocumentIndex index = OneStandard(SizedSections(3, 110));

        DocumentHit hit = index.CodingStandards("csharp", topic: "zzz-no-such-topic", charBudget: 60_000).Single();

        Assert.Empty(hit.Excerpt.Sections);
        Assert.False(hit.Excerpt.IsComplete);
    }

    /// <summary>
    /// One budget covers every document the key matched, split between them but never below
    /// the per-document floor: a technology with two standards still returns usable prose
    /// from both rather than slicing each into uselessness.
    /// </summary>
    [Fact]
    public void TwoStandardsForOneTechnologySplitTheBudgetWithoutDroppingBelowTheFloor()
    {
        DocumentIndex index = TwoCsharpStandards();

        IReadOnlyList<DocumentHit> hits = index.CodingStandards("csharp", charBudget: 2000);

        Assert.Equal(2, hits.Count);
        foreach (DocumentHit hit in hits)
        {
            Assert.Equal(2, hit.Excerpt.Sections.Count);
            Assert.Equal(["Section 3"], hit.Excerpt.OmittedHeadings);
        }
    }

    /// <summary>
    /// A budget above the ceiling is the ceiling. The clamp keeps one call from
    /// materialising however much prose a caller asked for, and the caller can still tell
    /// that more exists because the excerpt names what it left out.
    /// </summary>
    [Fact]
    public void ABudgetAboveTheCeilingIsTheCeiling()
    {
        DocumentIndex index = OneStandard(SizedSections(20, 1100));

        DocumentHit atCeiling = index.CodingStandards("csharp", charBudget: 120_000).Single();
        DocumentHit aboveCeiling = index.CodingStandards("csharp", charBudget: 10_000_000).Single();

        Assert.True(atCeiling.Excerpt.BudgetExhausted);
        Assert.Equal(
            atCeiling.Excerpt.Sections.Select(section => section.Heading),
            aboveCeiling.Excerpt.Sections.Select(section => section.Heading));
        Assert.Equal(atCeiling.Excerpt.OmittedHeadings, aboveCeiling.Excerpt.OmittedHeadings);
    }

    /// <summary>
    /// A standard is a formal claim, not a ranked guess: the caller named the technology, so
    /// the hit scores whole. Coding standards declare no <c>code-refs</c> by ontology design,
    /// so there is nothing to join to the code graph.
    /// </summary>
    [Fact]
    public void AStandardHitScoresWholeAndCarriesNoCodeJoins()
    {
        DocumentIndex index = OneStandard(CsharpSections());

        DocumentHit hit = index.CodingStandards("csharp").Single();

        Assert.Equal(1.0, hit.Score);
        Assert.Empty(hit.CodeJoins);
    }

    /// <summary>
    /// The list a caller chooses from: each declared technology once, ordinal-sorted, drawn
    /// only from coding standards. A reference document that declares csharp is not the
    /// standard for it, and a standard that declares nothing names nothing.
    /// </summary>
    [Fact]
    public void DeclaredTechnologiesComeBackOrdinalSorted()
    {
        DocumentIndex index = TwoTechnologies();

        Assert.Equal(["csharp", "test"], index.CodingStandardTechnologies());
    }

    /// <summary>Two standards for one technology are one choice, not two.</summary>
    [Fact]
    public void TwoStandardsForOneTechnologyAreListedOnce()
    {
        DocumentIndex index = TwoCsharpStandards();

        Assert.Equal(["csharp"], index.CodingStandardTechnologies());
    }

    public void Dispose() => _fixture.Dispose();
}
