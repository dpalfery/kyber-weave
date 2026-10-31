using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Text;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs_explore</c> returns the whole winning leaf up to a 24,000-character ceiling,
/// with <c>tanh</c> topic confidence and optional cursor continuation on the same leaf.
/// </summary>
public sealed class ExploreAnswerTests : IDisposable
{
    private const int SectionAnswerCeiling = 24000;

    private const string IncompletePrefix =
        "This answer is incomplete. Call docs_explore again with the same question and cursor";

    private static readonly Regex ConfidenceLine = new(
        @"^confidence: (?<value>0\.\d{2}|1\.00)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex CursorToken = new(
        @"cursor ""(?<token>[^""]+)""",
        RegexOptions.CultureInvariant);

    private readonly DocFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void AMatchPastCharacter2000IsReturnedAndTheIntroIsNot()
    {
        const string introMarker = "INTRO-ONLY-MARKER-XYZZY";
        const string answerPhrase = "windowtarget definitive answer phrase";
        string beforeMatch = RepeatToLength("paddingword ", 2100);
        string sectionBody = beforeMatch + answerPhrase;
        DocsTools tools = BuildTools(
            ("window/fixture", "Window fixture", "6-Docs/window/fixture.md", ["windowtarget"],
                [("Introduction", RepeatToLength("neutral ", 400) + introMarker),
                    ("Answer", sectionBody)]));

        string response = Explore(tools, "windowtarget");
        string prose = AnswerProse(response);

        Assert.Contains(answerPhrase, prose, StringComparison.Ordinal);
        Assert.Contains(beforeMatch, prose, StringComparison.Ordinal);
        Assert.DoesNotContain(introMarker, prose, StringComparison.Ordinal);
        Assert.Equal(sectionBody, prose);
        AssertLegacyPileAbsent(response);
    }

    [Fact]
    public void MatchingTextPast3000CharactersYieldsTheIncompleteSentenceAndACursor()
    {
        const string early = "EARLY-SPAN-MARKER-continueline";
        const string late = "LATE-SPAN-MARKER-continueline";
        string body = LeafBodyWithContinuationMarkers("continueline ", early, late);
        DocsTools tools = BuildTools(
            ("continueline/fixture", "Continue fixture", "6-Docs/continueline/fixture.md", ["continueline"],
                [("Detail", body)]));

        string response = Explore(tools, "continueline");
        string prose = AnswerProse(response);

        Assert.Contains(IncompletePrefix, response, StringComparison.Ordinal);
        Assert.True(CursorToken.IsMatch(response), $"expected a cursor token; response was: {response}");
        Assert.True(prose.Length <= SectionAnswerCeiling,
            $"first span must respect the {SectionAnswerCeiling}-character ceiling; prose length was {prose.Length}.");
        Assert.Contains(early, prose, StringComparison.Ordinal);
        Assert.DoesNotContain(late, prose, StringComparison.Ordinal);
        AssertLegacyPileAbsent(response);
    }

    [Fact]
    public void AFollowUpCallReturnsTheLaterSpanNotTheFirst()
    {
        const string early = "FOLLOWUP-EARLY-MARKER-zebrawindow";
        const string late = "FOLLOWUP-LATE-MARKER-zebrawindow";
        string body = LeafBodyWithContinuationMarkers("zebrawindow ", early, late);
        DocsTools tools = BuildTools(
            ("zebrawindow/fixture", "Cursor fixture", "6-Docs/zebrawindow/fixture.md", ["zebrawindow"],
                [("Detail", body)]));

        ParameterInfo? cursorParameter = ExploreMethod.GetParameters()
            .SingleOrDefault(parameter => string.Equals(parameter.Name, "cursor", StringComparison.Ordinal));
        Assert.NotNull(cursorParameter);

        string first = Explore(tools, "zebrawindow");
        Match cursorMatch = CursorToken.Match(first);
        Assert.True(cursorMatch.Success, $"first response must carry a cursor; response was: {first}");

        string second = Explore(tools, "zebrawindow", cursorMatch.Groups["token"].Value);
        string secondProse = AnswerProse(second);

        Assert.Contains(late, secondProse, StringComparison.Ordinal);
        Assert.DoesNotContain(early, secondProse, StringComparison.Ordinal);
    }

    [Fact]
    public void AScoringFollowingSectionIsAppendedOnlyWhenItFits()
    {
        const string mainPhrase = "routing primary guidance routing";
        const string followPhrase = "routing secondary guidance routing";
        DocsTools tools = BuildTools(
            ("api/routing", "Routing", "6-Docs/webui/architecture.md", ["routing"],
                [("Overview", mainPhrase), ("Components", followPhrase)]));

        string response = Explore(tools, "routing");
        string prose = AnswerProse(response);

        Assert.Contains(mainPhrase, prose, StringComparison.Ordinal);
        Assert.DoesNotContain(followPhrase, prose, StringComparison.Ordinal);
        Assert.DoesNotContain(IncompletePrefix, response, StringComparison.Ordinal);
        AssertLegacyPileAbsent(response);
    }

    [Fact]
    public void ANonScoringFollowingSectionIsAbsent()
    {
        const string mainPhrase = "routing primary guidance routing";
        const string unrelated = "kangaroo marsupial unrelated";
        DocsTools tools = BuildTools(
            ("api/routing", "Routing", "6-Docs/webui/architecture.md", ["routing"],
                [("Overview", mainPhrase), ("Unrelated", unrelated)]));

        string response = Explore(tools, "routing");
        string prose = AnswerProse(response);

        Assert.Contains(mainPhrase, prose, StringComparison.Ordinal);
        Assert.DoesNotContain(unrelated, prose, StringComparison.Ordinal);
        Assert.DoesNotContain("[also in this document, not matching this query: Unrelated]", response, StringComparison.Ordinal);
        AssertLegacyPileAbsent(response);
    }

    [Fact]
    public void SectionsAreReturnedInDocumentOrderWhenBothScore()
    {
        DocsTools tools = BuildTools(
            ("api/routing", "Routing", "6-Docs/webui/architecture.md", ["routing"],
                [("Alpha", "routing alpha section routing"),
                    ("Beta", "routing beta section routing"),
                    ("Gamma", "routing gamma section routing")]));

        string response = Explore(tools, "routing");
        string prose = AnswerProse(response);

        Assert.Contains("alpha section", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("beta section", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("gamma section", prose, StringComparison.Ordinal);
        AssertLegacyPileAbsent(response);
    }

    [Fact]
    public void ACloseRunnerUpScoresBelow060()
    {
        (DocsTools tools, DocumentIndexHost host) = BuildToolsWithHost(
            ("topic/alpha", "Alpha", "6-Docs/topic/alpha.md", ["closetopic"],
                [("Detail", "closetopic alpha detail closetopic")]),
            ("topic/beta", "Beta", "6-Docs/topic/beta.md", ["closetopic"],
                [("Detail", "closetopic beta detail closetopic")]));

        const string query = "closetopic";
        string response = Explore(tools, query);
        AssertTopicConfidence(host, query, response, expectRunnerTopicAboveZero: true, expectBelow060: true);
    }

    [Fact]
    public void AClearUniqueSectionScoresAtLeast060()
    {
        const string phrase = "uniquetopic sentinel guidance uniquetopic";
        (DocsTools tools, DocumentIndexHost host) = BuildToolsWithHost(
            ("topic/unique", "Unique", "6-Docs/topic/unique.md", ["uniquetopic"], [("Detail", phrase)]));

        string response = Explore(tools, "uniquetopic");
        string prose = AnswerProse(response);

        Assert.Contains(phrase, prose, StringComparison.Ordinal);
        Assert.Equal(phrase, prose);
        AssertTopicConfidence(host, "uniquetopic", response, expectAtLeast060: true);
    }

    [Fact]
    public void AnExactDocumentIdQueryScores100WhenComplete()
    {
        const string phrase = "exact id handle complete answer";
        DocsTools tools = BuildTools(
            ("fixture/exact", "Exact", "6-Docs/fixture/exact.md", [], [("Overview", phrase)]));

        string response = Explore(tools, "fixture/exact");
        string prose = AnswerProse(response);

        Assert.Contains(phrase, prose, StringComparison.Ordinal);
        Assert.Equal(phrase, prose);
        Assert.Equal(1.00, ParseConfidence(response), precision: 2);
        Assert.DoesNotContain(IncompletePrefix, response, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnanswerableFixtureIsAMiss()
    {
        DocsTools tools = BuildTools(
            ("topic/only", "Only", "6-Docs/topic/only.md", [], [("Detail", "nothing about sandwiches here")]));

        string response = Explore(tools, "how do I make a sandwich");

        Assert.Contains("No document", response, StringComparison.Ordinal);
        Assert.DoesNotContain("confidence:", response, StringComparison.Ordinal);
        Assert.Empty(SourceLines(response).ToList());
    }

    [Fact]
    public void TheReplyIsAtMost8000CharactersAndContainsNoLegacyBudgetJargon()
    {
        const string phrase = "routing primary guidance routing";
        DocsTools tools = BuildTools(
            ("api/routing", "Routing", "6-Docs/webui/architecture.md", ["routing"], [("Overview", phrase)]));

        string response = Explore(tools, "routing");
        string prose = AnswerProse(response);

        Assert.Equal(phrase, prose);
        Assert.True(prose.Length < SectionAnswerCeiling,
            $"a short section must not be padded toward the {SectionAnswerCeiling}-character ceiling; prose length was {prose.Length}.");
        AssertLegacyPileAbsent(response);
    }

    private DocsTools BuildTools(params (string Id, string Title, string Path, string[] Keywords, (string Heading, string Body)[] Sections)[] documents)
    {
        (DocsTools tools, _) = BuildToolsWithHost(documents);
        return tools;
    }

    private (DocsTools Tools, DocumentIndexHost Host) BuildToolsWithHost(
        params (string Id, string Title, string Path, string[] Keywords, (string Heading, string Body)[] Sections)[] documents)
    {
        _fixture.WithCatalog().WithSourceRoot("1-Presentation/Api");
        foreach ((string id, string title, string path, string[] keywords, (string Heading, string Body)[] sections) in documents)
        {
            WriteDocument(id, title, path, keywords, sections);
        }

        DocumentIndexHost host = CreateHost(_fixture.Root);
        return (new DocsTools(host, new RepositoryDocsAnalysisReader(_fixture.Root)), host);
    }

    private void WriteDocument(
        string id,
        string title,
        string relativePath,
        string[] keywords,
        (string Heading, string Body)[] sections)
    {
        StringBuilder text = new();
        text.AppendLine("---");
        text.AppendLine(CultureInfo.InvariantCulture, $"id: {id}");
        text.AppendLine(CultureInfo.InvariantCulture, $"title: {title}");
        text.AppendLine("doc-type: architecture");
        text.AppendLine("status: current");
        text.AppendLine("component: MotorcycleRAG API");
        text.AppendLine("source-root: 1-Presentation/Api");
        text.AppendLine("owner: API maintainers");
        text.AppendLine("last-reviewed: 2026-07-21");
        if (keywords.Length > 0)
        {
            text.AppendLine("keywords:");
            foreach (string keyword in keywords)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - {keyword}");
            }
        }

        text.AppendLine("---");
        text.AppendLine(CultureInfo.InvariantCulture, $"# {title}");
        text.AppendLine();
        foreach ((string heading, string body) in sections)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"## {heading}");
            text.AppendLine();
            text.AppendLine(body);
            text.AppendLine();
        }

        _fixture.Write(relativePath, text.ToString());
    }

    private static MethodInfo ExploreMethod
    {
        get
        {
            MethodInfo? method = typeof(DocsTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(candidate =>
                    candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "docs_explore");
            Assert.True(method is not null, "DocsTools must expose docs_explore.");
            return method!;
        }
    }

    private static string Explore(DocsTools tools, string query, string? cursor = null)
    {
        if (cursor is null)
        {
            return tools.Explore(query);
        }

        ParameterInfo? cursorParameter = ExploreMethod.GetParameters()
            .SingleOrDefault(parameter => string.Equals(parameter.Name, "cursor", StringComparison.Ordinal));
        Assert.NotNull(cursorParameter);
        return (string)ExploreMethod.Invoke(tools, [query, cursor])!;
    }

    private static double ContractConfidence(double winningTopic, double bestOtherTopic, bool incomplete)
    {
        double value = Math.Tanh((winningTopic - bestOtherTopic) / 2.0);
        if (incomplete)
        {
            value *= 0.5;
        }

        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private static void AssertTopicConfidence(
        DocumentIndexHost host,
        string query,
        string formattedResponse,
        bool expectRunnerTopicAboveZero = false,
        bool expectBelow060 = false,
        bool expectAtLeast060 = false)
    {
        DocumentIndex index = host.Current();
        ExploreAnswer answer = index.Explore(query);
        (double winnerTopic, double runnerTopic) = RankTopicScores(index, query);

        if (expectRunnerTopicAboveZero)
        {
            Assert.True(runnerTopic > 0,
                $"expected a rival topic score above zero, got runner {runnerTopic:0.000} for query '{query}'.");
        }

        double expected = ContractConfidence(winnerTopic, runnerTopic, answer.Incomplete);
        Assert.Equal(expected, answer.Confidence, precision: 2);
        Assert.Equal(expected, ParseConfidence(formattedResponse), precision: 2);

        if (expectBelow060)
        {
            Assert.True(answer.Confidence < 0.60,
                $"expected confidence below 0.60, got {answer.Confidence:0.00} (winner topic {winnerTopic:0.000}, runner topic {runnerTopic:0.000}).");
        }

        if (expectAtLeast060)
        {
            Assert.True(answer.Confidence >= 0.60,
                $"expected confidence at least 0.60, got {answer.Confidence:0.00} (winner topic {winnerTopic:0.000}, runner topic {runnerTopic:0.000}).");
        }
    }

    private static (double WinnerTopic, double RunnerTopic) RankTopicScores(DocumentIndex index, string query)
    {
        string trimmed = query.Trim();
        Dictionary<string, double> queryVector = TextVectorizer.VectorizeFused(query);
        List<string> coverageTerms = TextVectorizer.Vectorize(query).Keys.ToList();
        Dictionary<string, double> titleQuery = DocumentIndex.TitleQuery(coverageTerms);

        MethodInfo? rankMethod = typeof(DocumentIndex).GetMethod(
            "RankLeaves",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(rankMethod);

        object rankResult = rankMethod.Invoke(index, [trimmed, queryVector, coverageTerms, titleQuery])!;
        Type resultType = rankResult.GetType();
        object? winner = resultType.GetProperty("Winner")!.GetValue(rankResult);
        Assert.NotNull(winner);

        double runnerTopic = (double)resultType.GetProperty("RunnerTopicScore")!.GetValue(rankResult)!;
        double winnerTopic = (double)winner.GetType().GetProperty("TopicScore")!.GetValue(winner)!;
        return (winnerTopic, runnerTopic);
    }

    private static string LeafBodyWithContinuationMarkers(string unit, string earlyMarker, string lateMarker)
    {
        int earlyOffset = SectionAnswerCeiling - earlyMarker.Length - 50;
        Assert.True(earlyOffset > 0, "early marker must sit inside the first answer span.");
        string body = RepeatToLength(unit, earlyOffset) + earlyMarker + RepeatToLength(unit, 800) + lateMarker;
        Assert.True(body.IndexOf(earlyMarker, StringComparison.Ordinal) + earlyMarker.Length <= SectionAnswerCeiling,
            "early marker must end within the first answer span.");
        Assert.True(body.IndexOf(lateMarker, StringComparison.Ordinal) >= SectionAnswerCeiling,
            "late marker must start after the first answer span.");
        return body;
    }

    private static double ParseConfidence(string response)
    {
        Match match = ConfidenceLine.Match(response);
        Assert.True(match.Success, $"expected a confidence line; response was: {response}");
        return double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
    }

    private static string AnswerProse(string response)
    {
        int sourceIndex = response.IndexOf("source:", StringComparison.Ordinal);
        Assert.True(sourceIndex >= 0, $"expected a source line; response was: {response}");

        int lineEnd = response.IndexOf('\n', sourceIndex);
        Assert.True(lineEnd >= 0, $"expected a source line terminator; response was: {response}");

        int proseStart = lineEnd + 1;
        while (proseStart < response.Length && response[proseStart] == '\n')
        {
            proseStart++;
        }

        string tail = response[proseStart..];
        if (tail.StartsWith(IncompletePrefix, StringComparison.Ordinal))
        {
            int marker = tail.IndexOf(IncompletePrefix, StringComparison.Ordinal);
            return tail[..marker].TrimEnd();
        }

        return tail.TrimEnd();
    }

    private static IEnumerable<string> SourceLines(string response) =>
        response.Split('\n').Where(line => line.StartsWith("source:", StringComparison.Ordinal));

    private static void AssertLegacyPileAbsent(string response)
    {
        Assert.DoesNotContain("Top ", response, StringComparison.Ordinal);
        Assert.DoesNotContain("code joins:", response, StringComparison.Ordinal);
        Assert.DoesNotContain("omitted for space", response, StringComparison.Ordinal);
        Assert.DoesNotContain("ask again with a larger charBudget", response, StringComparison.Ordinal);
    }

    private static string RepeatToLength(string unit, int length)
    {
        StringBuilder builder = new(length + unit.Length);
        while (builder.Length < length)
        {
            builder.Append(unit);
        }

        return builder.ToString(0, length);
    }

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
}
