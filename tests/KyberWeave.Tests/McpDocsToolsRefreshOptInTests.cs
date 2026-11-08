using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins that the docs server's CodeGraph refresh is opt-in: a <see cref="DocsTools"/> a caller builds
/// without a refresh schedule of its own answers from the documents on disk.
/// </summary>
/// <remarks>
/// <para>
/// The refresh exists so a long session does not answer from an index that has since moved. Giving it
/// to every <see cref="DocsTools"/> that never asked for it widened that decision into a gate on
/// document retrieval itself: against a repository that declares <c>codegraph</c> as a required
/// server, on a machine with no readable index — a CI runner has none, deliberately — every docs tool
/// returned the provenance header and <c>STATUS: TOOL_FAILURE</c> and no answer at all. Nineteen
/// retrieval cases failed at once, including queries the corpus cannot answer, which assert a miss
/// rather than a confidence line and so could not pass either way. The failure was invisible on a
/// developer checkout, which has an index.
/// </para>
/// <para>
/// The repository arranged here is one the refresh WOULD stop: it declares <c>codegraph</c> among its
/// required MCP servers (<see cref="McpDeclarationFixture.RequireCodeGraph"/>) and its resolver reports
/// the index unreadable, which is the condition a runner without an index presents. A tools instance
/// built without a schedule still has to answer, because answering from the documents on disk is what
/// a docs tool is for; the gate belongs to the caller that asked to keep the index fresh.
/// </para>
/// </remarks>
public sealed class McpDocsToolsRefreshOptInTests : IDisposable
{
    /// <summary>The whole line a tool the gate stopped returns in place of its answer.</summary>
    private const string ToolFailure = "STATUS: TOOL_FAILURE";

    /// <summary>A document id the scaffolded corpus carries, so the query has an answer on disk.</summary>
    private const string ScaffoldedDocumentId = "documentation-ontology";

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// With no explicit refresh schedule the tools answer from the documents on disk, even though the
    /// repository requires CodeGraph and the index is unreadable.
    /// </summary>
    /// <remarks>
    /// The refresh is a decision the composition root makes, not one a consumer inherits. A tool that
    /// fails because an index it never asked about is unreadable has turned a retrieval answer into a
    /// tool availability error, and a caller reading it cannot tell a broken corpus from a broken index.
    /// </remarks>
    [Fact]
    public void DocsToolsBuiltWithoutARefreshScheduleAnswerFromTheDocumentsOnDisk()
    {
        DocsScaffolder.Scaffold(_root.Path);
        McpDeclarationFixture.RequireCodeGraph(_root.Path);

        string response = ToolsWithoutARefreshSchedule(_root.Path).Explore(ScaffoldedDocumentId);

        Assert.DoesNotContain(ToolFailure, response, StringComparison.Ordinal);
        Assert.StartsWith("provenance:", response, StringComparison.Ordinal);
        Assert.Contains("confidence:", response, StringComparison.Ordinal);
        Assert.Contains("source:", response, StringComparison.Ordinal);
        Assert.Contains(ScaffoldedDocumentId, response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tools over a scaffolded repository that requires CodeGraph, built the way a caller that
    /// wants only document retrieval builds them: no analysis port, no refresh schedule.
    /// </summary>
    /// <param name="root">A repository root that already carries the scaffolded corpus.</param>
    private static DocsTools ToolsWithoutARefreshSchedule(string root)
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(root);
        DocumentIndexHost host = new(
            root,
            () => new UnreadableCodeGraph(),
            () => new DocumentLoader(root, config.Ontology).Load(),
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);

        return new DocsTools(host);
    }

    /// <summary>A CodeGraph port that reports no readable index, as a runner without one does.</summary>
    private sealed class UnreadableCodeGraph : ICodeGraphResolver
    {
        public bool IsAvailable => false;

        public string? UnavailableReason => "No CodeGraph index at .codegraph/codegraph.db.";

        public string DatabasePath => ".codegraph/codegraph.db";

        public IReadOnlyList<CodeGraphNode> ResolveSymbol(string name) => [];

        public IReadOnlyList<CodeGraphNode> ResolveRoute(string route) => [];

        public bool HasFilesUnder(string relativePathPrefix) => false;

        public IReadOnlyList<string> CandidateNames(string likePattern) => [];

        public IReadOnlyList<string> AllRoutes() => [];
    }
}
