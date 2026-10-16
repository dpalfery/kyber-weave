using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T3 RED — the <c>agent_coding_standards</c> tool answers a portable agent's question about
/// one stack: it resolves the technology to this repository's standard, names the document,
/// and puts the rule ids in front of the prose so a budget can never cut them away.
/// </summary>
/// <remarks>
/// <para>
/// Every call resolves the method through its <c>[McpServerTool]</c> name rather than calling it
/// directly — the idiom <c>McpDocsProvenanceTests</c> uses for <c>docs_status</c>. The declared
/// name is what a client routes on, so keeping the calls on it means a rename fails here instead
/// of leaving the suite green while the tool agents call has gone.
/// </para>
/// <para>
/// The failure this file guards is a plausible one. An agent that shipped with its own idea of
/// how C# or tests are written asks this tool for the local answer, and a confident response
/// that turns out to be another repository's standard — or one whose rule ids were quietly
/// truncated away — is worse than a plain miss, because nothing in the response looks wrong.
/// </para>
/// </remarks>
public sealed class McpAgentCodingStandardsTests : IDisposable
{
    private const string ToolName = "agent_coding_standards";
    private const string CsharpStandardPath = "6-Docs/standards/csharp/README.md";
    private const string TestStandardPath = "6-Docs/standards/test/README.md";
    private const string RustStandardPath = "6-Docs/standards/rust/README.md";
    private const string PythonStandardPath = "6-Docs/standards/python/README.md";

    /// <summary>
    /// An id the overflow fixture's title states and its body never cites, so the only statement
    /// of it in the document is the frontmatter title the identity block echoes.
    /// </summary>
    private const string TitleOnlyRuleId = "KW-TITLE-999";

    /// <summary>
    /// The header contract every docs tool response leads with — root, a short revision or the
    /// literal <c>unavailable</c>, a dirty marker or <c>unknown</c>, and the live document
    /// count. Repeated rather than shared because the copy in <c>McpDocsProvenanceTests</c> is
    /// private to that class, and a helper hoisted across test files belongs to neither.
    /// </summary>
    private static readonly Regex ProvenancePattern = new(
        "^provenance: root=(?<root>.+?) rev=(?<rev>[0-9a-f]{4,40}|unavailable) "
        + "dirty=(?<dirty>yes|no|unknown) documents=(?<documents>\\d+)$",
        RegexOptions.CultureInvariant);

    private readonly DocFixture _corpus = new();

    public void Dispose()
    {
        _corpus.Dispose();
    }

    /// <summary>
    /// Every response shape — a hit whose body the budget clipped, a hit that fits, a miss, a
    /// usage reply, and a standard with no sections — leads with the provenance line, and the
    /// line is emitted whole whatever the budget was, so the tell is never the part that gets
    /// truncated away.
    /// </summary>
    [Fact]
    public void EveryResponseLeadsWithAWholeProvenanceLineForTheCorpusThatAnswered()
    {
        (DocsTools tools, _) = Corpus(
            (CsharpStandardPath, StandardWithTheRuleIdInItsLastSection()),
            (TestStandardPath, TestStandard()),
            (RustStandardPath, StandardWithNoSections()));

        string[] responses =
        [
            Ask(tools, "csharp", charBudget: 1000),
            Ask(tools, "csharp"),
            Ask(tools, "zzz-no-such-technology-zzz"),
            Ask(tools, "   "),
            Ask(tools, "rust")
        ];

        foreach (string response in responses)
        {
            string firstLine = response.Split('\n')[0].TrimEnd('\r');
            Match match = ProvenancePattern.Match(firstLine);
            Assert.True(
                match.Success,
                "Docs tool responses must lead with the provenance line; "
                + $"first line was: '{firstLine}'.");
            Assert.Equal(Path.GetFullPath(_corpus.Root), Path.GetFullPath(match.Groups["root"].Value));
        }
    }

    /// <summary>
    /// A hit says which technology it answered for and how current that standard is, then
    /// gives the document's identity — including the technology as the corpus declares it,
    /// which is the value a later lookup is defined against.
    /// </summary>
    [Fact]
    public void AHitNamesTheTechnologyItsStatusAndTheStandardsIdentity()
    {
        (DocsTools tools, _) = Corpus((CsharpStandardPath, CsharpStandard()));

        string response = Ask(tools, "csharp");

        Assert.Contains("1 standard for technology 'csharp' (status CURRENT).", response, StringComparison.Ordinal);
        Assert.Contains("C# house rules", response, StringComparison.Ordinal);
        Assert.Contains("path: " + CsharpStandardPath, response, StringComparison.Ordinal);
        Assert.Contains("id: standards/csharp", response, StringComparison.Ordinal);
        Assert.Contains("doc-type: CODING-STANDARD", response, StringComparison.Ordinal);
        Assert.Contains("status: CURRENT", response, StringComparison.Ordinal);
        Assert.Contains("technology: csharp", response, StringComparison.Ordinal);
        Assert.Contains("[complete document]", response, StringComparison.Ordinal);

        // The blocks come in this order: what was answered, which document answered it, the
        // ids to work from, then the prose. The rule-id index sits ahead of the sections so
        // that clipping the prose can never clip the ids (see the budget test below).
        int count = response.IndexOf("1 standard for technology", StringComparison.Ordinal);
        int identity = response.IndexOf("path: ", StringComparison.Ordinal);
        int ruleIds = response.IndexOf("rule ids:", StringComparison.Ordinal);
        int sections = response.IndexOf("\n## ", StringComparison.Ordinal);
        int complete = response.IndexOf("[complete document]", StringComparison.Ordinal);
        Assert.True(
            count < identity && identity < ruleIds && ruleIds < sections && sections < complete,
            $"The hit must read count, identity, rule ids, sections, completeness; response was: {response}");
    }

    /// <summary>
    /// The identity block echoes what the corpus declares, not the spelling the caller typed.
    /// A caller that writes the Config Reg property name gets the standard for the technology
    /// it names, and is told the technology under the name the repository declares.
    /// </summary>
    [Fact]
    public void TheTechnologyLineEchoesTheValueTheCorpusDeclaresRatherThanTheKeyTheCallerTyped()
    {
        (DocsTools tools, _) = Corpus((CsharpStandardPath, Standard(
            "CSharp",
            "standards/csharp",
            "C# house rules",
            "current",
            Sections(("Overview", Prose("csharp", 40))))));

        string response = Ask(tools, "csharp-coding-standard");

        Assert.Contains("technology: CSharp", response, StringComparison.Ordinal);
        Assert.DoesNotContain("technology: csharp", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule-id index is emitted before the sections, so a body clipped by the budget still
    /// cannot lose it: the caller's follow-up work hangs off the ids, and an id list that
    /// depends on how much prose fitted is an id list nobody can rely on.
    /// </summary>
    [Fact]
    public void TheRuleIdIndexPrecedesTheSectionsSoABudgetCanNeverDropIt()
    {
        (DocsTools tools, _) = Corpus((CsharpStandardPath, StandardWithTheRuleIdInItsLastSection()));

        string response = Ask(tools, "csharp", charBudget: 1000);

        Assert.Contains("rule ids: KW-DOC-SPEC-007", response, StringComparison.Ordinal);
        Assert.Contains(
            "[omitted for space, ask again with a larger charBudget: Third]",
            response,
            StringComparison.Ordinal);

        int indexLine = response.IndexOf("rule ids:", StringComparison.Ordinal);
        int firstSection = response.IndexOf("\n## ", StringComparison.Ordinal);
        Assert.True(indexLine >= 0, $"The response must carry a rule-id index; response was: {response}");
        Assert.True(
            firstSection > indexLine,
            $"The rule-id index must be emitted before the sections; response was: {response}");
    }

    /// <summary>
    /// The index is extracted from the document, so the same id cited twice is one entry and
    /// the order is the document's — the order the standard reads in, not a sort a reader
    /// cannot predict.
    /// </summary>
    [Fact]
    public void TheRuleIdIndexDedupesAndKeepsTheOrderTheDocumentCitesThem()
    {
        (DocsTools tools, _) = Corpus((CsharpStandardPath, Standard(
            "csharp",
            "standards/csharp",
            "C# house rules",
            "current",
            Sections(("Rules", "Cite KW-ZETA-001 first, KW-ALPHA-002 second, then KW-ZETA-001 again.")))));

        string response = Ask(tools, "csharp");

        Assert.Equal("rule ids: KW-ZETA-001 · KW-ALPHA-002", RuleIdLine(response));
    }

    /// <summary>
    /// Every id the response prints is one the index accounts for — listed, or counted in its
    /// remainder — including an id the frontmatter title states. The identity block echoes the
    /// title, so an id there is printed to the caller like any citation in the prose, and an
    /// index that leaves it out tells a caller the standard does not state an id it just read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordering pinned here is that the ids the title states follow the ids the sections
    /// state: the index reads the standard's body in document order, and the title's metadata
    /// ids come after it. The alternative — scanning the title first, since the title precedes
    /// the body — would let one id stated in a title displace a section id from the fifty the
    /// index lists, so the window the cap covers would depend on frontmatter rather than on the
    /// body the cap exists to summarize. Appending also leaves the cap fixtures' listing windows
    /// untouched: an id the title repeats keeps the position its first citation gave it, and a
    /// title-only id that falls past the cap is counted in the remainder and withheld from the
    /// identity block rather than printed (see the cap fixtures above).
    /// </para>
    /// <para>
    /// The remainder is asserted absent rather than merely consistent. Three ids against a cap
    /// of fifty means nothing is withheld, so an id missing from the index cannot be accounted
    /// for by counting it instead: the response would print an id it never claims to state.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRuleIdIndexAlsoAccountsForAnIdTheStandardsTitleStates()
    {
        (DocsTools tools, _) = Corpus((CsharpStandardPath, StandardWithTheRuleIdInItsTitle()));

        string response = Ask(tools, "csharp");

        // The identity block echoes the title whole, so this id is printed to the caller and the
        // index below has to account for it.
        Assert.Contains("### C# house rules (KW-TITLE-001)", response, StringComparison.Ordinal);

        // The ids the sections state keep the document order the index is defined against, and
        // the id the title states is appended after them.
        Assert.Equal("rule ids: KW-ZETA-001 · KW-ALPHA-002 · KW-TITLE-001", RuleIdLine(response));
        Assert.DoesNotContain("more rule ids", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A long index stops at fifty and says how many it dropped. Answering with the first fifty
    /// and no note would read as "this standard states fifty ids".
    /// </summary>
    [Fact]
    public void AnIndexOfMoreThanFiftyRuleIdsStopsAtFiftyAndSaysHowManyItDropped()
    {
        (DocsTools tools, _) = Corpus((CsharpStandardPath, Standard(
            "csharp",
            "standards/csharp",
            "C# house rules",
            "current",
            Sections(("Rules", SixtyRuleIds())))));

        string response = Ask(tools, "csharp");

        List<string> capped = new();
        for (int index = 1; index <= 50; index++)
        {
            capped.Add("KW-CAP-" + index.ToString("D3", CultureInfo.InvariantCulture));
        }

        Assert.Contains("rule ids: " + string.Join(" · ", capped), response, StringComparison.Ordinal);
        Assert.Contains("… 10 more", response, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-CAP-051", response, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-CAP-060", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// An id whose only statement is the standard's title falls under the same cap as the ids the
    /// prose cites: it is counted in the remainder rather than listed, and the identity block that
    /// echoes the title withholds it there. Reading the ids off the document's sections alone
    /// would leave the remainder one short of the ids the same response is accounting for, and
    /// print the missing one in full a few lines below the count that dropped it.
    /// </summary>
    /// <remarks>
    /// Where the id falls is what makes this observable, and it is the position the append order
    /// gives it: the title is read after the sections, so a title-only id is the last distinct id
    /// the standard states — past the fifty the index lists, which is where the cap cuts. Neither
    /// cap fixture beside this one reaches that case. One puts no id in the title at all, and the
    /// other repeats a section id there, which the window already covers at the position its first
    /// citation gave it. The id is asserted absent from the whole response rather than from the
    /// title line alone, because the identity block is not the only place the title is echoed.
    /// </remarks>
    [Fact]
    public void ATitleOnlyRuleIdPastTheCapIsCountedInTheRemainderAndWithheldFromTheTitleLine()
    {
        (DocsTools tools, _) = Corpus(
            (CsharpStandardPath, StandardWhoseTitleStatesAnIdItsBodyNeverCites()));

        string response = Ask(tools, "csharp");

        List<string> cited = new();
        for (int index = 1; index <= 60; index++) cited.Add(RuleId(index));
        List<string> listed = cited.Take(50).ToList();

        // The listing window is the ids the sections cite, in document order: the title-only id is
        // the sixty-first distinct id, so it sits past the cap rather than displacing a section id.
        Assert.Equal("rule ids: " + string.Join(" · ", listed), RuleIdLine(response));
        Assert.Contains("rule ids: " + RuleId(1), response, StringComparison.Ordinal);

        // Sixty ids the sections cite plus the one the title states is sixty-one distinct ids
        // against a cap of fifty, so the remainder has to be eleven. An index built from the
        // sections alone would say ten — under-reporting by exactly the id withheld below.
        int distinct = cited.Count + 1;
        Assert.Contains(
            "… " + (distinct - 50).ToString(CultureInfo.InvariantCulture) + " more",
            response,
            StringComparison.Ordinal);

        // The identity block echoes the frontmatter title, which is document text under the same
        // cap, so the id it states is replaced where it stood.
        string title = response.Split('\n')
            .Single(line => line.StartsWith("### ", StringComparison.Ordinal))
            .TrimEnd('\r');
        Assert.Equal("### C# house rules ([rule id withheld])", title);

        // The invariant over the whole response rather than one site at a time: the id the
        // remainder counts is printed nowhere.
        Assert.DoesNotContain(TitleOnlyRuleId, response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cap the response states is a cap it keeps everywhere in the response, not only in
    /// the sections it rewrote: an id the index declined to name is printed nowhere, including
    /// the document text the response echoes around those sections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The response repeats text taken from the document in three places, and only one of them
    /// was ever rewritten — the sections themselves. The identity block echoes the frontmatter
    /// title, and the marker naming what the topic did not match is built from the document's
    /// headings. A standard stating more ids than the index lists could therefore say "… N more
    /// rule ids … are not listed above" and then print one of them a few lines further down: a
    /// claim true of the index and false of the response.
    /// </para>
    /// <para>
    /// One response exercises all three. The topic matches the middle section, which is the only
    /// one returned and the only one whose citations are replaced, while the sections it did not
    /// match are named by their headings and the title is echoed whole.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWithheldRuleIdIsNeverPrintedWhereverTheResponseEchoesTheDocument()
    {
        (DocsTools tools, _) = Corpus(
            (CsharpStandardPath, StandardWithTheOverflowWhereTheResponseEchoesTheDocument()));

        string response = Ask(tools, "csharp", "quantumzebra", charBudget: 1000);

        List<string> cited = new();
        for (int index = 1; index <= 60; index++) cited.Add(RuleId(index));
        List<string> listed = cited.Take(50).ToList();
        List<string> withheld = cited.Skip(50).ToList();

        // The index names the first fifty in the order the document cites them, and counts the
        // rest rather than naming them.
        Assert.Equal("rule ids: " + string.Join(" · ", listed), RuleIdLine(response));
        Assert.Contains("… 10 more", response, StringComparison.Ordinal);

        // In the one section it does return, the response replaces each withheld citation where
        // the id stood — in the heading, and in the prose.
        Assert.Contains("## Quantum checks ([rule id withheld])", response, StringComparison.Ordinal);
        Assert.Contains(
            "The check raises [rule id withheld] when it fails, [rule id withheld] when it cannot run, "
            + "and [rule id withheld] once the check lands.",
            response,
            StringComparison.Ordinal);

        // The two places that echo the document instead of the index must not undo that. The
        // marker is built from the headings of the sections the topic did not match, so an id in
        // the heading of a dropped section would come back verbatim.
        string unmatched = response.Split('\n').Single(line =>
            line.StartsWith("[also in this document, not matching this topic:", StringComparison.Ordinal));
        string title = response.Split('\n').Single(line => line.StartsWith("### ", StringComparison.Ordinal));
        Assert.DoesNotContain(RuleId(54), unmatched, StringComparison.Ordinal);
        Assert.DoesNotContain(RuleId(60), title, StringComparison.Ordinal);

        // The invariant over the whole response rather than one site at a time.
        foreach (string id in withheld)
        {
            Assert.DoesNotContain(id, response, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The fourth echo site, and the one no returned section carries: a topic that matches
    /// nothing names every heading the document holds, and that list is read straight from the
    /// document <em>because</em> the excerpt is empty — there are no sections for the excerpt's
    /// elision to reach, so a heading citing an id past the cap would come back verbatim.
    /// </summary>
    /// <remarks>
    /// The test above exercises three echo sites through one call and cannot see this one: its
    /// topic matches a section, so the empty-excerpt branch never runs. Here the topic matches
    /// none, and the headings the response names include four that cite withheld ids — so
    /// dropping the elision from that one list prints an id the index above it declined to name,
    /// on the same response that counts it as omitted.
    /// </remarks>
    [Fact]
    public void AWithheldRuleIdIsNotPrintedInTheHeadingsNamedWhenTheTopicMatchesNoSection()
    {
        const string unmatched = "zzz-no-such-topic-zzz";
        (DocsTools tools, _) = Corpus(
            (CsharpStandardPath, StandardWithTheOverflowWhereTheResponseEchoesTheDocument()));

        string response = Ask(tools, "csharp", unmatched);

        List<string> cited = new();
        for (int index = 1; index <= 60; index++) cited.Add(RuleId(index));
        List<string> withheld = cited.Skip(50).ToList();

        // The call really did take the empty-excerpt path: the topic is named as a miss, which
        // is the branch that builds its heading list from the document rather than the excerpt.
        List<string> notices = new();
        foreach (string line in response.Split('\n'))
        {
            if (line.Contains("no section matches the topic", StringComparison.Ordinal))
                notices.Add(line.TrimEnd('\r'));
        }

        string notice = Assert.Single(notices);
        Assert.Contains("'" + unmatched + "'", notice, StringComparison.Ordinal);

        // The headings are named, and one of them per withheld citation is replaced where the id
        // stood — so the assertion below is not vacuous: these are the headings this call echoes.
        string marker = response.Split('\n').Single(line =>
            line.StartsWith("[also in this document, not matching this topic:", StringComparison.Ordinal));
        Assert.Equal(
            "[also in this document, not matching this topic: Rules · Quantum checks ([rule id withheld])"
            + " · Deferred checks ([rule id withheld]) · Appendix checks ([rule id withheld])"
            + " · Legacy checks ([rule id withheld])]",
            marker);

        // The identity block echoes the frontmatter title, which cites the last withheld id.
        string title = response.Split('\n').Single(line => line.StartsWith("### ", StringComparison.Ordinal));
        foreach (string id in withheld)
        {
            Assert.DoesNotContain(id, title, StringComparison.Ordinal);
        }

        // The invariant over the whole response rather than one site at a time.
        foreach (string id in withheld)
        {
            Assert.DoesNotContain(id, response, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A standard that states no ids says so. An empty index line, or an index of ids the tool
    /// inferred from somewhere else, would both claim something the document never said.
    /// </summary>
    [Fact]
    public void AStandardThatStatesNoRuleIdsSaysSoRatherThanShippingAnEmptyIndex()
    {
        (DocsTools tools, _) = Corpus((TestStandardPath, TestStandard()));

        string response = Ask(tools, "test");

        Assert.Equal("rule ids: (this standard states no rule ids)", RuleIdLine(response));
    }

    /// <summary>
    /// A draft standard is still this repository's answer for its technology, so it is
    /// returned — with the status named in a warning, because a caller about to follow it has
    /// to know it is not the settled one.
    /// </summary>
    [Fact]
    public void AStandardWhoseStatusIsNotCurrentStillAnswersAndCarriesAWarningNamingTheStatus()
    {
        (DocsTools tools, _) = Corpus((PythonStandardPath, Standard(
            "python",
            "standards/python",
            "Python house rules",
            "draft",
            Sections(("Overview", Prose("python", 40))))));

        string response = Ask(tools, "python");

        Assert.Contains("path: " + PythonStandardPath, response, StringComparison.Ordinal);
        Assert.Contains("(status DRAFT)", response, StringComparison.OrdinalIgnoreCase);

        List<string> warnings = new();
        foreach (string line in response.Split('\n'))
        {
            if (line.Contains("warning", StringComparison.OrdinalIgnoreCase)) warnings.Add(line);
        }

        Assert.True(
            warnings.Count > 0,
            $"A standard that is not current must carry a warning line; response was: {response}");
        Assert.Contains(warnings, line => line.Contains("DRAFT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A standard awaiting review is still this repository's answer for its technology, and the
    /// status it is named by is the ontology's spelling — the one its frontmatter declares,
    /// validation reports, and the tool's own doc-type helper already hyphenates for the same
    /// reason. A status rendered as <c>NEEDSREVIEW</c> names a state no document, rule id, or
    /// configuration key uses, so a caller told that has nothing to look it up against.
    /// </summary>
    [Fact]
    public void AStandardAwaitingReviewStillAnswersAndNamesItsStatusInTheOntologysSpelling()
    {
        (DocsTools tools, _) = Corpus((PythonStandardPath, Standard(
            "python",
            "standards/python",
            "Python house rules",
            "needs-review",
            Sections(("Rules", "Every finding this standard raises cites KW-DOC-SPEC-007 in the report. "
                               + Prose("python", 40))))));

        string response = Ask(tools, "python");

        // Still an answer rather than a suppression: the path and the rule-id index are what a
        // caller acts on, so withholding the standard would leave the stack with no local
        // answer at all.
        Assert.Contains("path: " + PythonStandardPath, response, StringComparison.Ordinal);
        Assert.Contains("rule ids: KW-DOC-SPEC-007", response, StringComparison.Ordinal);

        Assert.Contains("(status NEEDS-REVIEW)", response, StringComparison.OrdinalIgnoreCase);

        List<string> warnings = new();
        foreach (string line in response.Split('\n'))
        {
            if (line.Contains("warning", StringComparison.OrdinalIgnoreCase)) warnings.Add(line);
        }

        Assert.True(
            warnings.Count > 0,
            $"A standard that is not current must carry a warning line; response was: {response}");
        Assert.Contains(warnings, line => line.Contains("NEEDS-REVIEW", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("NEEDSREVIEW", response, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One section is capped, so a single oversized document cannot crowd the caller's budget
    /// out of the response.
    /// </summary>
    [Fact]
    public void ASectionOverTheCapIsCutAtSixThousandCharacters()
    {
        string oversized = string.Join(
            " ",
            Enumerable.Range(0, 2000).Select(index => "w" + index.ToString(CultureInfo.InvariantCulture)));
        (DocsTools tools, _) = Corpus((CsharpStandardPath, Standard(
            "csharp",
            "standards/csharp",
            "C# house rules",
            "current",
            Sections(("Rules", oversized)))));

        string response = Ask(tools, "csharp", charBudget: 120_000);

        Assert.Contains(oversized[..6000], response, StringComparison.Ordinal);
        Assert.Contains("truncated at 6000 characters", response, StringComparison.Ordinal);
        Assert.DoesNotContain("w1999", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A section dropped for lack of budget is named, and asking again with a larger budget is
    /// worth doing — which is what the response has to say, and only here. A section dropped
    /// for irrelevance will not come back at any budget, so a reader told to ask again would
    /// spend a call learning nothing.
    /// </summary>
    [Fact]
    public void ASectionDroppedForSpaceNamesWhatDidNotFitAndSuggestsABiggerBudget()
    {
        (DocsTools tools, _) = Corpus((CsharpStandardPath, StandardWithTheRuleIdInItsLastSection()));

        string response = Ask(tools, "csharp", charBudget: 1000);

        Assert.Contains(
            "[omitted for space, ask again with a larger charBudget: Third]",
            response,
            StringComparison.Ordinal);
        Assert.DoesNotContain("not matching this topic", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other cause, told apart by its wording: the section exists in the document but does
    /// not answer the topic the caller named, so it is named as present-and-off-topic rather
    /// than as priced out.
    /// </summary>
    [Fact]
    public void ASectionDroppedForIrrelevanceIsNamedWithoutSuggestingABiggerBudget()
    {
        (DocsTools tools, _) = Corpus((CsharpStandardPath, Standard(
            "csharp",
            "standards/csharp",
            "C# house rules",
            "current",
            Sections(
                ("Naming", Prose("naming", 60)),
                ("Appendix", "Filler prose that covers the addendum. Filler prose.")))));

        string response = Ask(tools, "csharp", "naming", 120_000);

        Assert.Contains(
            "[also in this document, not matching this topic: Appendix]",
            response,
            StringComparison.Ordinal);
        Assert.DoesNotContain("omitted for space", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other cause of an empty excerpt, told apart from the first: the <c>##</c> headings
    /// are there and none of them answers the topic, so the response says the topic matched
    /// nothing and then names the headings it did not match. Saying the body carries no
    /// headings would send a caller away from a standard sitting right there, and returning the
    /// whole document instead would answer a narrower question with everything.
    /// </summary>
    [Fact]
    public void ATopicThatMatchesNoSectionIsNamedAsATopicMissRatherThanAsAStandardWithNoHeadings()
    {
        string unmatched = "zebra-quantum-flux";
        (DocsTools tools, _) = Corpus((CsharpStandardPath, Standard(
            "csharp",
            "standards/csharp",
            "C# house rules",
            "current",
            Sections(
                ("Naming", Prose("convention", 60)),
                ("Error handling", Prose("diagnostic", 60))))));

        string response = Ask(tools, "csharp", unmatched, 120_000);

        Assert.Contains("path: " + CsharpStandardPath, response, StringComparison.Ordinal);
        Assert.DoesNotContain("carries no ## headings", response, StringComparison.Ordinal);

        List<string> notices = new();
        foreach (string line in response.Split('\n'))
        {
            if (line.Contains("no section matches the topic", StringComparison.Ordinal))
                notices.Add(line.TrimEnd('\r'));
        }

        string notice = Assert.Single(notices);
        Assert.Contains("'" + unmatched + "'", notice, StringComparison.Ordinal);

        // The headings are named with the marker an off-topic section already gets, so the
        // caller learns what the document does hold instead of being sent back to Read.
        Assert.Contains(
            "[also in this document, not matching this topic: Naming · Error handling]",
            response,
            StringComparison.Ordinal);

        // Naming the headings is not returning them: a topic that matched nothing must not fall
        // back to the whole standard's prose.
        Assert.DoesNotContain("convention", response, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic", response, StringComparison.Ordinal);

        Assert.True(
            response.IndexOf("no section matches the topic", StringComparison.Ordinal)
            < response.IndexOf("[also in this document", StringComparison.Ordinal),
            $"The topic miss must be stated before the headings it names; response was: {response}");
    }

    /// <summary>
    /// The budget is clamped, so a value outside the range behaves as the bound it is clamped
    /// to: a caller cannot ask for an unbounded response, and cannot ask for one so small that
    /// the tool gives up instead of returning the standard's first sections.
    /// </summary>
    [Fact]
    public void ABudgetOutsideTheClampBehavesAsTheBoundItIsClampedTo()
    {
        (DocsTools tools, _) = Corpus((CsharpStandardPath, StandardWithTheRuleIdInItsLastSection()));

        string atFloor = Ask(tools, "csharp", charBudget: 1000);
        string belowFloor = Ask(tools, "csharp", charBudget: 1);
        string atCeiling = Ask(tools, "csharp", charBudget: 120_000);
        string aboveCeiling = Ask(tools, "csharp", charBudget: 10_000_000);

        Assert.Equal(atFloor, belowFloor);
        Assert.Equal(atCeiling, aboveCeiling);

        // Not vacuous: the two bounds have to differ, or the equalities above prove only that
        // the budget is ignored.
        Assert.NotEqual(atFloor, atCeiling);
        Assert.Contains("[omitted for space", atFloor, StringComparison.Ordinal);
        Assert.Contains("[complete document]", atCeiling, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown technology is an explicit miss: the subject is not documented here, the
    /// caller learns how many documents were considered, and the corpus offers the names it
    /// does declare so the next call can name a real one. Answering with another technology's
    /// standard would be the same miss with no way to detect it.
    /// </summary>
    [Fact]
    public void AnUnknownTechnologyIsAnExplicitMissThatNamesWhatTheCorpusDeclares()
    {
        (DocsTools tools, DocumentIndexHost host) = Corpus(
            (CsharpStandardPath, CsharpStandard()),
            (TestStandardPath, TestStandard()));
        int considered = host.Current().DocumentCount;

        string response = Ask(tools, "rust");

        Assert.Contains("rust", response, StringComparison.Ordinal);
        Assert.Contains("no coding standard", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("csharp, test", response, StringComparison.Ordinal);
        Assert.DoesNotContain(CsharpStandardPath, response, StringComparison.Ordinal);
        Assert.DoesNotContain("rule ids:", response, StringComparison.Ordinal);

        // The count is asserted in the body rather than against the whole response: the
        // provenance line carries a document count of its own, so a whole-response match would
        // pass without the miss saying anything.
        Assert.Matches(
            @"\b" + considered.ToString(CultureInfo.InvariantCulture) + @"\b",
            Body(response));
    }

    /// <summary>
    /// No technology is a caller error, not a lookup: the response says what to pass, naming
    /// both forms a caller may have seen — the bare technology and the Config Reg property
    /// name the repository instructions use.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void AnEmptyOrWhitespaceTechnologyIsAUsageResponseNamingBothAcceptedKeyForms(string technology)
    {
        (DocsTools tools, _) = Corpus((CsharpStandardPath, CsharpStandard()));

        string response = Ask(tools, technology);

        Assert.Contains("technology", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("csharp-coding-standard", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path: ", response, StringComparison.Ordinal);
        Assert.DoesNotContain("rule ids:", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A standard written with prose and no <c>##</c> sections is said to be exactly that,
    /// naming its path. The alternative — an identity block and then nothing — reads as an
    /// empty document rather than as a standard this tool cannot excerpt.
    /// </summary>
    [Fact]
    public void AStandardWithNoSectionsSaysSoInsteadOfReturningSomethingThatLooksEmpty()
    {
        (DocsTools tools, _) = Corpus((RustStandardPath, StandardWithNoSections()));

        string response = Ask(tools, "rust");

        Assert.Contains("path: " + RustStandardPath, response, StringComparison.Ordinal);
        Assert.Contains("no sections", response, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The genuine no-headings case, kept distinguishable from a topic that matched nothing: a
    /// standard whose body is prose alone really does carry nothing to excerpt, so the response
    /// says exactly that rather than blaming the topic — and the topic has to be supplied for
    /// the test to mean it, because with no topic there is no blame to misplace. The two
    /// branches share an empty excerpt, and telling them apart is the whole contract change here.
    /// </summary>
    [Fact]
    public void AStandardWithNoHeadingsStillSaysItCarriesNoHeadingsRatherThanBlamingATopic()
    {
        (DocsTools tools, _) = Corpus((RustStandardPath, StandardWithNoSections()));

        string response = Ask(tools, "rust", "error handling");

        Assert.Contains("path: " + RustStandardPath, response, StringComparison.Ordinal);
        Assert.Contains("carries no ## headings", response, StringComparison.Ordinal);
        Assert.DoesNotContain("no section matches the topic", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// Calls the tool by reflection, in the order its contract declares its parameters:
    /// technology, then the optional topic, then the optional budget.
    /// </summary>
    private static string Ask(
        DocsTools tools,
        string technology,
        string? topic = null,
        int charBudget = DocumentIndex.DefaultCharBudget)
    {
        MethodInfo tool = FindMcpTool();
        return (string)tool.Invoke(tools, [technology, topic, charBudget])!;
    }

    private static MethodInfo FindMcpTool()
    {
        MethodInfo? method = typeof(DocsTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
                candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == ToolName);
        Assert.True(method is not null, $"DocsTools must expose an MCP tool named '{ToolName}'.");
        return method;
    }

    /// <summary>The one line that leads with <c>rule ids:</c>, so an index emitted twice fails here.</summary>
    private static string RuleIdLine(string response)
    {
        List<string> lines = new();
        foreach (string line in response.Split('\n'))
        {
            if (line.StartsWith("rule ids:", StringComparison.Ordinal)) lines.Add(line.TrimEnd('\r'));
        }

        return Assert.Single(lines);
    }

    /// <summary>Everything after the provenance line, which is where the tool's own claims live.</summary>
    private static string Body(string response)
    {
        return string.Join('\n', response.Split('\n').Skip(1));
    }

    /// <summary>
    /// Writes the given documents into a corpus of its own — the fixture catalog plus these —
    /// and builds the tool surface over it, returning the host so a test can ask the corpus how
    /// many documents were considered.
    /// </summary>
    private (DocsTools Tools, DocumentIndexHost Host) Corpus(params (string RelativePath, string Text)[] documents)
    {
        _corpus.WithCatalog();
        foreach ((string relativePath, string text) in documents)
        {
            _corpus.Write(relativePath, text);
        }

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(_corpus.Root);
        DocumentIndexHost host = new(
            _corpus.Root,
            () => CodeGraphResolverAdapter.ForRepository(_corpus.Root),
            () => new DocumentLoader(_corpus.Root, config.Ontology).Load(),
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);
        return (new DocsTools(host), host);
    }

    /// <summary>
    /// The csharp standard shaped like a real one: a short overview, a naming section, and a
    /// rules section whose prose cites the rule id an agent has to follow.
    /// </summary>
    private static string CsharpStandard()
    {
        return Standard(
            "csharp",
            "standards/csharp",
            "C# house rules",
            "current",
            Sections(
                ("Overview", Prose("csharp", 40)),
                ("Naming", Prose("naming", 100)),
                ("Rules", "Every finding this standard raises cites KW-DOC-SPEC-007 in the report. "
                          + Prose("csharp", 40))));
    }

    /// <summary>
    /// Three sections of even size whose third is the only one citing a rule id, so a budget
    /// that drops that section still cannot drop the id from the index.
    /// </summary>
    private static string StandardWithTheRuleIdInItsLastSection()
    {
        return Standard(
            "csharp",
            "standards/csharp",
            "C# house rules",
            "current",
            Sections(
                ("First", Prose("rules", 100)),
                ("Second", Prose("rules", 100)),
                ("Third", "Every finding this standard raises cites KW-DOC-SPEC-007 in the report. "
                          + Prose("rules", 100))));
    }

    /// <summary>
    /// A standard whose frontmatter title states an id of its own, with two sections stating
    /// ids the title does not, so the index built from the body alone is visibly missing one id
    /// the response prints. Three ids against a cap of fifty, so nothing is withheld and the
    /// index line is the whole of what the response says it states.
    /// </summary>
    private static string StandardWithTheRuleIdInItsTitle()
    {
        return Standard(
            "csharp",
            "standards/csharp",
            "C# house rules (KW-TITLE-001)",
            "current",
            Sections(
                ("Naming", "A review comment cites KW-ZETA-001 where the name breaks the rule. "
                           + Prose("naming", 40)),
                ("Rules", "Every finding this standard raises cites KW-ALPHA-002 in the report. "
                          + Prose("csharp", 40))));
    }

    /// <summary>
    /// A standard whose last distinct id is stated by its frontmatter title alone: sixty ids the
    /// body cites in one section, and a title stating a sixty-first the body never repeats. The
    /// title's id therefore sorts last, past the fifty the index lists — the one position where a
    /// title-only id is counted in the remainder instead of being named.
    /// </summary>
    private static string StandardWhoseTitleStatesAnIdItsBodyNeverCites()
    {
        return Standard(
            "csharp",
            "standards/csharp",
            "C# house rules (" + TitleOnlyRuleId + ")",
            "current",
            Sections(("Rules", SixtyRuleIds())));
    }

    /// <summary>The test standard: prose that cites no rule id at all.</summary>
    private static string TestStandard()
    {
        return Standard(
            "test",
            "standards/test",
            "Test house rules",
            "current",
            Sections(
                ("Overview", Prose("test", 40)),
                ("Naming", "A test name states the guarantee it pins. " + Prose("test", 40))));
    }

    /// <summary>A standard whose body is its title alone, so it has no <c>##</c> sections.</summary>
    private static string StandardWithNoSections()
    {
        return Standard(
            "rust",
            "standards/rust",
            "Rust house rules",
            "current",
            string.Empty);
    }

    private static string Sections(params (string Heading, string Body)[] sections)
    {
        return string.Join("\n", sections.Select(section => $"## {section.Heading}\n\n{section.Body}\n"));
    }

    private static string Prose(string subject, int words)
    {
        return string.Join(' ', Enumerable.Repeat(subject, words));
    }

    /// <summary>The nth id the cap fixtures state, so a fixture and its assertions agree.</summary>
    private static string RuleId(int index) =>
        "KW-CAP-" + index.ToString("D3", CultureInfo.InvariantCulture);

    /// <summary>
    /// One citation of each id from <paramref name="first"/> to <paramref name="last"/>, in
    /// order, so the index built from them is the order the document reads in.
    /// </summary>
    private static string RuleCitations(int first, int last)
    {
        List<string> citations = new();
        for (int index = first; index <= last; index++)
        {
            citations.Add("The check raises " + RuleId(index) + " when it fails.");
        }

        return string.Join(" ", citations);
    }

    /// <summary>Sixty distinct ids in one paragraph, more than the index's hard cap of fifty.</summary>
    private static string SixtyRuleIds() => RuleCitations(1, 60);

    /// <summary>
    /// A standard whose index overflows the cap, with the overflow placed where the response
    /// echoes the document rather than the index: the frontmatter title, the heading of the one
    /// section a topic matches, and the headings of the sections that topic leaves out.
    /// </summary>
    private static string StandardWithTheOverflowWhereTheResponseEchoesTheDocument()
    {
        return Standard(
            "csharp",
            "standards/csharp",
            "C# house rules (" + RuleId(60) + ")",
            "current",
            Sections(
                ("Rules", RuleCitations(1, 50) + " " + Prose("rules", 20)),
                ("Quantum checks (" + RuleId(51) + ")",
                    "The check raises " + RuleId(51) + " when it fails, " + RuleId(52)
                    + " when it cannot run, and " + RuleId(53) + " once the check lands. "
                    + Prose("quantumzebra", 30)),
                ("Deferred checks (" + RuleId(54) + ")",
                    "The check raises " + RuleId(55) + " once the deferred work lands. "
                    + Prose("deferred", 20)),
                ("Appendix checks (" + RuleId(56) + ")",
                    "The check raises " + RuleId(57) + " in the appendix. " + Prose("appendix", 20)),
                ("Legacy checks (" + RuleId(58) + ")",
                    "The check raises " + RuleId(59) + " and " + RuleId(60) + " in the legacy path. "
                    + Prose("legacy", 20))));
    }

    /// <summary>
    /// A coding-standard document. <c>doc-type</c> is what marks it a standard and
    /// <c>technology</c> is the key a caller looks it up by, so neither is ever omitted.
    /// </summary>
    private static string Standard(
        string technology,
        string id,
        string title,
        string status,
        string body)
    {
        return $"""
                ---
                id: {id}
                title: {title}
                doc-type: coding-standard
                status: {status}
                technology: {technology}
                owner: Maintainers
                last-reviewed: 2026-07-21
                ---

                # {title}
                """ + "\n\n" + body;
    }
}
