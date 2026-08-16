using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
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
    private const int TopN = 3;

    /// <summary>Question a person would ask → the document id that answers it.</summary>
    public static TheoryData<string, string> Cases() => new()
    {
        // The reported miss: plain wording, no jargon. The answer is a troubleshooting
        // runbook that says "users are silently logged out" almost verbatim, but every
        // requirements document is dense with "user", "session" and "authentication".
        { "why does the user keep getting logged out", "operations/webui-bff-data-protection-troubleshooting" },
        { "users are silently logged out after a deploy", "operations/webui-bff-data-protection-troubleshooting" },

        // The same subject in the vocabulary you would need to already know. Retrieval is
        // only worth having if both forms work, so both are pinned.
        { "rotate data protection keys if blob storage is unavailable", "operations/webui-bff-data-protection-disaster-recovery" },

        // Component-shaped questions, asked the way people ask them.
        { "how is the web ui put together", "webui/architecture" },
        { "how do I run the web ui locally", "webui/onboarding" },
        { "what does the BFF do", "webui-bff/architecture" },
        { "how does the mobile app authenticate", "mobile/architecture" },

        // Identity lookups: a doc-id is an exact handle and must win outright.
        { "webui/architecture", "webui/architecture" },
        { "api/architecture", "api/architecture" },

        // Governance and operations.
        { "what frontmatter keys does a document need", "system/documentation-ontology" },
        { "how do I set up my dev machine", "devops/developer-setup-standard" },
        { "what are the branch protection rules", "operations/github-branch-protection" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void APlainlyAskedQuestionFindsTheDocumentThatAnswersIt(string query, string expectedId)
    {
        string? root = RepositoryRoot();
        if (root is null) return;

        DocumentIndex index = DocumentIndex.Build(
            new DocumentLoader(root).Load(),
            CodeGraphResolverAdapter.ForRepository(root));

        List<string> ids = index.Explore(query, maxDocs: TopN)
            .Select(h => h.Document.Frontmatter.Id ?? h.Document.RelativePath)
            .ToList();

        Assert.Contains(expectedId, ids, StringComparer.Ordinal);
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

        DocumentIndex index = DocumentIndex.Build(
            new DocumentLoader(root).Load(),
            CodeGraphResolverAdapter.ForRepository(root));

        IReadOnlyList<DocumentHit> hits = index.Explore(query, maxDocs: TopN);

        Assert.True(hits.Count == 0,
            "expected no hits, got: " + string.Join(", ",
                hits.Select(h => $"{h.Document.RelativePath} ({h.Score:0.00})")));
    }

    /// <summary>
    /// Walks up from the test assembly looking for the repository. Returns null when it
    /// is not there, so the suite skips instead of failing.
    /// </summary>
    private static string? RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "6-Docs")) &&
                File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        return null;
    }
}
