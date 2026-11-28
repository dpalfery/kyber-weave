using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using KyberWeave.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Mcp.Tests;

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
/// <para>
/// Each test's repository declares <c>codegraph</c> among its required MCP servers, because D23
/// applies the refresh — and with it the failure pinned here — only where CodeGraph is declared. The
/// scaffold's own declaration names <c>kw-docs</c> alone, so
/// <see cref="McpDeclarationFixture"/> adds the entry the scaffold leaves commented out.
/// </para>
/// </remarks>
public sealed class McpToolFailureTests
{
    /// <summary>The token the orchestrator routes on. A whole line, so a passing mention cannot satisfy it.</summary>
    private const string ToolFailure = "STATUS: TOOL_FAILURE";

    /// <summary>How the response must name the tool that failed, so the reader learns which one it was.</summary>
    private const string FailingToolName = "codegraph";

    private readonly MemoryDocsHost _host = new();

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
    public async Task DocsToolsReportToolFailureWithReasonAndFixUnderTheDefaultStopRule()
    {
        _host.Services().GetRequiredService<IDocsScaffoldingService>().Scaffold(_host.Repo);
        MemoryMcpDeclaration.RequireCodeGraph(_host.Files, _host.Repo);

        DocsTools tools = Tools(out CodeGraphFreshness freshness);
        using (freshness)
        {
            string response = await tools.ExploreAsync("anything");

            Assert.Contains(ToolFailure, response, StringComparison.Ordinal);
            Assert.Contains(FailingToolName, response, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("kyber-weave doctor", response, StringComparison.Ordinal);
        }
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
    public async Task DocsToolsWarnInsteadOfFailingWhenTheRepositoryOptsOutOfTheStopRule()
    {
        _host.Services().GetRequiredService<IDocsScaffoldingService>().Scaffold(_host.Repo);
        MemoryMcpDeclaration.RequireCodeGraph(_host.Files, _host.Repo);
        string configPath = _host.InRepo(".kyber-weave/kyber-weave.yml");
        _host.Files.WriteAllText(
            configPath,
            _host.Files.ReadAllText(configPath) + "\nstop-on-tool-failure: false\n");

        DocsTools tools = Tools(out CodeGraphFreshness freshness);
        using (freshness)
        {
            string response = await tools.ExploreAsync("anything");

            Assert.DoesNotContain(ToolFailure, response, StringComparison.Ordinal);
            Assert.Contains(FailingToolName, response, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("warning", response, StringComparison.OrdinalIgnoreCase);
        }
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
    public async Task TheRevisionToolStillAnswersWhileTheOtherDocsToolsReportToolFailure()
    {
        _host.Services().GetRequiredService<IDocsScaffoldingService>().Scaffold(_host.Repo);
        MemoryMcpDeclaration.RequireCodeGraph(_host.Files, _host.Repo);
        DocsTools tools = Tools(out CodeGraphFreshness freshness);
        using (freshness)
        {
            string explore = await tools.ExploreAsync("anything");
            string status = await tools.DocsStatusAsync();

            Assert.Contains(ToolFailure, explore, StringComparison.Ordinal);
            Assert.DoesNotContain(ToolFailure, status, StringComparison.Ordinal);
            Assert.StartsWith("provenance:", status, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A tools instance over the scaffolded corpus whose CodeGraph resolver reports the index as
    /// unreadable, which is the failure the run used to continue past.
    /// </summary>
    /// <remarks>
    /// The refresh schedule is asked for explicitly. It is opt-in: tools built without one answer
    /// from the documents on disk, so a test that pins the gate has to be the caller that requests it.
    /// </remarks>
    private DocsTools Tools(out CodeGraphFreshness ownedFreshness)
    {
        IServiceProvider services = _host.Services();
        string root = _host.Repo;
        KyberWeaveConfig config = services.GetRequiredService<IKyberWeaveConfigRepository>().Load(root);
        DocumentIndexHost host = new(
            root,
            () => new UnavailableCodeGraphResolver(),
            () => new DocumentLoader(_host.Store, root, _host.Paths, _host.Environment, config.Ontology).Load(),
            _host.Store,
            _host.Paths,
            _host.Environment,
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);

        CodeGraphFreshness freshness = new(
            root,
            true,
            services.GetRequiredService<ICodeGraphProvisioner>(),
            services.GetRequiredService<IKyberWeaveConfigRepository>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IFileSystem>(),
            services.GetRequiredService<IPathResolver>(),
            services.GetRequiredService<IProcessEnvironment>(),
            services.GetRequiredService<IProcessRunner>());
        ownedFreshness = freshness;

        return DocsToolsComposition.Create(host, null, freshness);
    }
}
