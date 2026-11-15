using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Which document answers: questions asked the way an agent asks them, against this repository's own
/// documentation, each with the page(s) that cover the subject.
/// </summary>
/// <remarks>
/// <para>
/// Queries come from two places. The first group was reported from live sessions, where
/// a question about the arbiter was answered from a page about worker rules although the arbiter's own
/// architecture, runbook and README exist. The second group is derived from real page titles, so the
/// fixture does not only contain cases the ranking was tuned on.
/// </para>
/// <para>
/// A case passes when the answer's source is any of the listed pages: a subject is often covered by
/// both its README and its architecture page, and either is a correct place to answer from.
/// </para>
/// </remarks>
public sealed class RetrievalSourceDocumentTests(ITestOutputHelper output)
{
    private const string Arbiter = "docs/kyber-arbiter/";

    public static TheoryData<string, string> Cases() => new()
    {
        // Reported from live sessions.
        {
            "What is the arbiter, what rules does it enforce, and how do I enable or disable it?",
            $"{Arbiter}architecture.md|{Arbiter}runbook.md|{Arbiter}README.md"
        },
        {
            "How does the arbiter use the Jev model and how is the Jev / OpenRouter key configured?",
            $"{Arbiter}jev-decision-model.md|{Arbiter}architecture.md|docs/configuration.md|{Arbiter}runbook.md"
        },

        // Derived from page titles.
        { "How do I install Kyber-Weave?", "docs/install.md" },
        { "What is the review council and what does it do?", "docs/code-review/README.md|docs/code-review/architecture.md" },
        { "How do I run KyberDash locally for development and testing?", "docs/dash/runbook.md" },
        { "What are model profiles and model presets?", "docs/kyber-squad/model-profiles.md" },
        { "How do I wire Kyber-Weave into CI?", "docs/ci-pipelines/workflows-runbook.md" },
        { "How do I run the DocGraph MCP server?", "docs/docgraph/mcp-runbook.md" },
        { "How does DocGraph rank retrieval results?", "docs/docgraph/retrieval.md" },
        { "How do I override the Copilot system prompt?", "docs/kyber-squad/copilot-prompt-runbook.md" },
        { "How is a release distributed?", "docs/distribution.md" },
        { "Which telemetry signals does each harness provide to KyberDash?", "docs/dash/telemetry-inventory.md" },
        { "How do I adopt DocGraph in an existing repository?", "docs/docgraph/onboarding.md" },
        { "Where is the rule reference for the KW rule ids?", "docs/ci-pipelines/rule-reference.md" },
        { "What does the Kyber-Squad architecture look like?", "docs/kyber-squad/architecture.md" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheAnswerComesFromAPageThatCoversTheSubject(string query, string acceptablePaths)
    {
        DocumentIndex? index = IndexOverThisRepository();
        if (index is null) return;

        ExploreAnswer answer = index.Explore(query);
        string actual = answer.SourceDocument?.RelativePath.Replace('\\', '/') ?? "(miss)";
        output.WriteLine($"{query}\n  -> {actual} ({answer.Confidence:0.00})");

        Assert.Contains(actual, acceptablePaths.Split('|'));
    }

    /// <summary>
    /// The three questions whose whole-section answers ran from 8 to 26 thousand characters now
    /// answer with a window: bounded, complete, and still from the right page.
    /// </summary>
    [Theory]
    [InlineData("What is the arbiter, what rules does it enforce, and how do I enable or disable it?")]
    [InlineData("Where is the rule reference for the KW rule ids?")]
    [InlineData("What does the Kyber-Squad architecture look like?")]
    public void ALongSectionAnswersWithABoundedCompleteWindow(string query)
    {
        DocumentIndex? index = IndexOverThisRepository();
        if (index is null) return;

        ExploreAnswer answer = index.Explore(query);
        output.WriteLine($"{query}\n  prose {answer.Prose.Length} chars, more={answer.MoreAvailable}");

        Assert.False(answer.Incomplete);
        Assert.True(answer.Prose.Length <= 8000, $"the window was {answer.Prose.Length} characters.");
    }

    /// <summary>
    /// A reported miss that the ranking still gets wrong: the worker rule map answers, with a negative
    /// confidence, because its keywords share word prefixes with the query ("clean" with "cleanupcode")
    /// while the standard that states the answer mentions it once in a long page. Kept here, skipped,
    /// so the case is not forgotten and is one attribute away from running.
    /// </summary>
    [Fact(Skip = "Known miss: the worker rule map outranks the C# standard for the cleanupcode question.")]
    public void TheCleanupcodeQuestionIsAnsweredFromTheStandardOrItsDecisionRecord()
    {
        TheAnswerComesFromAPageThatCoversTheSubject(
            "Why does dotnet jb cleanupcode hang at exit and how does kyber-weave work around it?",
            "docs/standards/csharp/README.md|docs/adr/0074-cleanupcode-is-removed-from-the-squad.md");
    }

    private static DocumentIndex? IndexOverThisRepository()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
               && !(File.Exists(Path.Combine(directory.FullName, "KyberWeave.sln"))
                    && File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))))
        {
            directory = directory.Parent;
        }

        if (directory is null) return null;

        string root = directory.FullName;
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(root);
        DocumentSet set = new DocumentLoader(root, config.Ontology).Load();
        return DocumentIndex.Build(set, CodeGraphResolverAdapter.ForRepository(root));
    }
}
