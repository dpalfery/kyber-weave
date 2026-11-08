using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the stop rule at the docs server (D11, D23): when CodeGraph cannot be read, the docs tools
/// say so in a form the orchestrator can route on, and the one diagnostic tool still answers.
/// </summary>
/// <remarks>
/// <para>
/// A run used to continue past a broken CodeGraph, so every code-editing agent downstream worked
/// from joins that were silently absent. The owner reversed that: a required tool that fails stops
/// the run, and a project that wants the old behaviour sets <c>stop-on-tool-failure: false</c>
/// explicitly. The server is where the failure is first observable, so the two outcomes have to be
/// distinguishable there rather than in each agent's prose.
/// </para>
/// <para>
/// The failure is arranged through the resolver the host is constructed with, which is the port the
/// docs tools already consult. That keeps the test on a seam that exists today, so it fails because
/// the behaviour is missing rather than because it does not compile.
/// </para>
/// </remarks>
public sealed class McpToolFailureTests : IDisposable
{
    /// <summary>The token the orchestrator routes on. A whole line, so a passing mention cannot satisfy it.</summary>
    private const string ToolFailure = "STATUS: TOOL_FAILURE";

    /// <summary>How the response must name the tool that failed, so the reader learns which one it was.</summary>
    private const string FailingToolName = "codegraph";

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private string Root => _root.Path;

    /// <summary>
    /// With the repository default (stop), a docs tool answers <c>STATUS: TOOL_FAILURE</c> with the
    /// reason and a fix, so no agent has to judge whether a degraded answer is good enough.
    /// </summary>
    /// <remarks>
    /// D1 and D11: the default is stop, so a repository that says nothing about
    /// <c>stop-on-tool-failure</c> is the strict case. The fix line matters as much as the token: an
    /// agent that stops without naming the repair has to ask a human what to do.
    /// </remarks>
    [Fact]
    public void DocsToolsReportToolFailureWithReasonAndFixUnderTheDefaultStopRule()
    {
        DocsScaffolder.Scaffold(Root);

        string response = Tools(Root).Explore("anything");

        Assert.Contains(ToolFailure, response, StringComparison.Ordinal);
        Assert.Contains(FailingToolName, response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kyber-weave doctor", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// With <c>stop-on-tool-failure: false</c> the same failure is a warning line and the answer
    /// still comes back, so a project that opted out is not stopped by it.
    /// </summary>
    /// <remarks>
    /// D1: <c>false</c> warns and continues. The two settings have to be distinguishable by a reader
    /// of the response, otherwise an operator who set <c>false</c> would still see the run stop —
    /// which is the failure this half of the pair prevents.
    /// </remarks>
    [Fact]
    public void DocsToolsWarnInsteadOfFailingWhenTheRepositoryOptsOutOfTheStopRule()
    {
        DocsScaffolder.Scaffold(Root);
        File.AppendAllText(
            Path.Combine(Root, ".kyber-weave", "kyber-weave.yml"),
            Environment.NewLine + "stop-on-tool-failure: false" + Environment.NewLine);

        string response = Tools(Root).Explore("anything");

        Assert.DoesNotContain(ToolFailure, response, StringComparison.Ordinal);
        Assert.Contains(FailingToolName, response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warning", response, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The revision tool answers even when every other docs tool reports a tool failure, so the
    /// failure can be diagnosed without the reader already knowing which corpus replied.
    /// </summary>
    /// <remarks>
    /// D23: <c>docs_get_source_repository_and_revision</c> always answers. If it reported
    /// <c>STATUS: TOOL_FAILURE</c> too, a caller would have no way to find out which repository the
    /// broken server was bound to, which is the first question after a tool failure.
    /// </remarks>
    [Fact]
    public void TheRevisionToolStillAnswersWhileTheOtherDocsToolsReportToolFailure()
    {
        DocsScaffolder.Scaffold(Root);
        DocsTools tools = Tools(Root);

        string explore = tools.Explore("anything");
        string status = tools.DocsStatus();

        Assert.Contains(ToolFailure, explore, StringComparison.Ordinal);
        Assert.DoesNotContain(ToolFailure, status, StringComparison.Ordinal);
        Assert.StartsWith("provenance:", status, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tools instance over the scaffolded corpus whose CodeGraph resolver reports the index as
    /// unreadable, which is the failure the run used to continue past.
    /// </summary>
    private static DocsTools Tools(string root)
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

    /// <summary>A CodeGraph port that reports no readable index, as a missing or unreadable database does.</summary>
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
