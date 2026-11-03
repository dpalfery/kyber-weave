using System.Globalization;
using System.Text.RegularExpressions;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Retrieval quality, asserted against the real documentation corpus.
/// </summary>
/// <remarks>
/// <para>
/// Ranking is the part of this tool that decides whether every agent in the repository
/// gets a right answer, and it was the untested part. Unit tests over a synthetic fixture
/// cannot catch the failure that matters, because the failure is a property of the real
/// corpus: on a body of documents all about one system, common vocabulary swamps the
/// discriminating term. Only real documents reproduce that.
/// </para>
/// <para>
/// Cases are written as a plainly-phrased question plus the document that answers it —
/// the form a person actually asks in, not the vocabulary they would need to already
/// know. A ranking change that breaks any of these is a regression, and the point of the
/// suite is that such a change can no longer pass silently.
/// </para>
/// <para>
/// The suite skips rather than fails when it cannot find the repository, so the tool tree
/// stays movable and a partial checkout does not produce noise.
/// </para>
/// </remarks>
public sealed class RetrievalRegressionTests
{
    private const string IncompletePrefix =
        "This answer is incomplete. Call docs_explore again with the same question and cursor";

    private static readonly Regex ConfidenceLine = new(
        @"^confidence: (?<value>0\.\d{2}|1\.00)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Question, expected document id, phrase from the answering section, confidence floor.</summary>
    public static TheoryData<string, string, string, double, string?> AnswerableCases() => new()
    {
        {
            "dashboard dev environment tauri",
            "dash/architecture",
            "The tray is a Tauri 2 app: a Rust shell",
            0.60,
            null
        },
        {
            "what frontmatter keys does a document need",
            "documentation-ontology",
            "Keys are hyphenated and bind",
            0.60,
            null
        },
        {
            "multi-harness agent and skill deployment",
            "squad/architecture",
            "multi-harness governance and deployment engine",
            0.60,
            null
        },
        {
            "parallel code review council specialist lenses",
            "code-review/architecture",
            "Specialist lenses reading the diff in parallel",
            0.60,
            null
        },
        {
            "diagnostic engine rule ids and sarif",
            "ci-pipelines/architecture",
            "comment, a SARIF baseline, and a code-scanning alert all key on",
            0.60,
            null
        },
        {
            "how does retrieval scoring work",
            "docgraph/retrieval",
            "A document scoring below **0.25** is not returned at all",
            0.60,
            null
        },
        {
            "how does the review council choose gates",
            "code-review/architecture",
            "`review.gates` entries take `id`, `run`, `blocking` and `timeout-seconds`",
            0.60,
            "Configuration"
        },
        { "docgraph/architecture", "docgraph/architecture", "rebuilt from the Markdown on demand", 0.60, null },
        { "dash/architecture", "dash/architecture", "The tray is a Tauri 2 app: a Rust shell", 0.60, null },
        { "squad/architecture", "squad/architecture", "multi-harness governance and deployment engine", 0.60, null },
        {
            "code-review/architecture",
            "code-review/architecture",
            "Specialist lenses reading the diff in parallel",
            0.60,
            null
        },
        { "documentation-ontology", "documentation-ontology", "Keys are hyphenated and bind", 0.60, null },
    };

    [Theory]
    [MemberData(nameof(AnswerableCases))]
    public void APlainlyAskedQuestionReturnsOneAnswerBlob(
        string query,
        string expectedId,
        string phrase,
        double floor,
        string? sourceHeading)
    {
        string? root = RepositoryRoot();
        if (root is null) return;

        DocsTools tools = CreateTools(root);
        string response = Explore(tools, query);

        Assert.Contains(phrase, response, StringComparison.Ordinal);
        Assert.DoesNotContain(IncompletePrefix, response, StringComparison.Ordinal);

        double confidence = ParseConfidence(response);
        Assert.True(
            confidence >= floor,
            $"expected confidence >= {floor:0.00}, got {confidence:0.00} for '{query}'; response excerpt: "
            + Truncate(response, 400));

        List<string> sources = SourceLines(response).ToList();
        Assert.Single(sources);
        Assert.Contains(expectedId, sources[0], StringComparison.Ordinal);
        Assert.DoesNotContain("plans/2026-10-30-docs-explore-answer", sources[0], StringComparison.Ordinal);

        if (sourceHeading is not null)
        {
            Assert.Contains(sourceHeading, sources[0], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A question the corpus does not answer must return nothing. Returning the three
    /// nearest documents to an unanswerable question is worse than returning none: the
    /// caller is told to use this tool before grepping, so a confident miss becomes a
    /// confident wrong answer with no signal to fall back.
    /// </summary>
    [Theory]
    [InlineData("how do I make a sandwich")]
    [InlineData("what is the airspeed velocity of an unladen swallow")]
    [InlineData("recipe for sourdough starter")]
    [InlineData("best hiking trails in patagonia")]
    [InlineData("who won the 1998 world cup")]
    [InlineData("how do I train for a marathon")]
    [InlineData("tell me a joke")]
    public void AnUnanswerableQuestionReturnsNothing(string query)
    {
        string? root = RepositoryRoot();
        if (root is null) return;

        DocsTools tools = CreateTools(root);
        string response = Explore(tools, query);

        Assert.Contains("No document", response, StringComparison.Ordinal);
        Assert.DoesNotContain("confidence:", response, StringComparison.Ordinal);
        Assert.Empty(SourceLines(response).ToList());
    }

    private static DocsTools CreateTools(string root) =>
        new(CreateHost(root), new RepositoryDocsAnalysisReader(root));

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

    private static string Explore(DocsTools tools, string query) => tools.Explore(query);

    private static double ParseConfidence(string response)
    {
        Match match = ConfidenceLine.Match(response);
        Assert.True(match.Success, $"expected a confidence line for an answerable query; response was: {Truncate(response, 400)}");
        return double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> SourceLines(string response) =>
        response.Split('\n').Where(line => line.StartsWith("source:", StringComparison.Ordinal));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    /// <summary>
    /// Walks up from the test assembly looking for the repository. Returns null when it
    /// is not there, so the suite skips instead of failing.
    /// </summary>
    private static string? RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KyberWeave.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
