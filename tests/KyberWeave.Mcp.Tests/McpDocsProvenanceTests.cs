using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Mcp.Tests;

/// <summary>
/// T1/T2 RED — every docs tool response discloses the corpus it answered from, and
/// <c>docs_get_source_repository_and_revision</c> exposes the same provenance block on demand.
/// </summary>
/// <remarks>
/// Regression for issue #162: the four docs tools led with hit counts only. A same-named
/// server bound to another checkout answered with the same confident prose, so a stale
/// corpus could outrank the checkout actually being edited with no visible tell. The header
/// contract is the tell: root, revision/dirty, and the live document count.
/// </remarks>
public sealed class McpDocsProvenanceTests
{
    /// <summary>
    /// The header contract from the plan: exactly one line, root first, then the branch and the
    /// git directory it was read from, a short SHA or the literal <c>unavailable</c>, a dirty
    /// marker or <c>unknown</c>, and the live document count.
    /// </summary>
    /// <remarks>
    /// The tokens after the root are named rather than assumed adjacent to it: the header gained
    /// <c>branch=</c> and <c>repo=</c>, and a pattern that expected <c>rev=</c> straight after the
    /// root swallowed both into the root capture. A branch name cannot contain a space, so
    /// <c>\S+</c> is exact there; a repository path can, so <c>repo=</c> runs to the next token.
    /// </remarks>
    private static readonly Regex ProvenancePattern = new(
        @"^provenance: root=(?<root>.+?) branch=\S+ repo=.+? rev=(?<rev>[0-9a-f]{4,40}|unavailable) "
        + @"dirty=(?<dirty>yes|no|unknown) documents=(?<documents>\d+)$",
        RegexOptions.CultureInvariant);

    private readonly MemoryDocsHost _host = new();

    [Fact]
    public async Task EachDocsToolLeadsWithProvenanceForItsOwnRepositoryRoot()
    {
        (string rootA, _) = await CreateTwoCorporaAsync();
        DocsTools tools = CreateTools(rootA);
        int expectedDocuments = CreateHost(rootA).Current().DocumentCount;

        await foreach (string response in AllResponses(tools))
        {
            ProvenanceLine line = ParseProvenance(response);
            Assert.Equal(Path.GetFullPath(rootA), Path.GetFullPath(line.Root));
            Assert.Equal(expectedDocuments, line.Documents);
        }
    }

    [Fact]
    public async Task MissAndEmptyResponsesStillLeadWithProvenance()
    {
        (string rootA, _) = await CreateTwoCorporaAsync();
        DocsTools tools = CreateTools(rootA);

        string miss = await tools.ExploreAsync("zzz-no-such-subject-zzz");
        string symbolMiss = await tools.ForSymbolAsync("NoSuchSymbolXyz");
        string glossaryMiss = await tools.GlossaryAsync("no-such-term-xyz");
        string analysis = await tools.AnalysisCandidatesAsync(kind: null, cursor: null, limit: 20, charBudget: 4000);
        string standardsMiss = await AgentCodingStandardsMiss(tools);

        foreach (string response in new[] { miss, symbolMiss, glossaryMiss, analysis, standardsMiss })
        {
            ProvenanceLine line = ParseProvenance(response);
            Assert.Equal(Path.GetFullPath(rootA), Path.GetFullPath(line.Root));
        }
    }

    [Fact]
    public async Task ProvenanceDocumentCountsDifferBetweenTwoCorpora()
    {
        (string rootA, string rootB) = await CreateTwoCorporaAsync();
        int countA = CreateHost(rootA).Current().DocumentCount;
        int countB = CreateHost(rootB).Current().DocumentCount;

        Assert.NotEqual(countA, countB);

        ProvenanceLine lineA = ParseProvenance(await CreateTools(rootA).ExploreAsync("documentation"));
        ProvenanceLine lineB = ParseProvenance(await CreateTools(rootB).ExploreAsync("documentation"));

        Assert.Equal(countA, lineA.Documents);
        Assert.Equal(countB, lineB.Documents);
    }

    [Fact]
    public async Task NeitherRootsProvenanceAppearsInTheOtherRootsResponses()
    {
        (string rootA, string rootB) = await CreateTwoCorporaAsync();
        string responseA = await CreateTools(rootA).ExploreAsync("documentation");
        string responseB = await CreateTools(rootB).ExploreAsync("documentation");

        Assert.Equal(Path.GetFullPath(rootA), Path.GetFullPath(ParseProvenance(responseA).Root));
        Assert.Equal(Path.GetFullPath(rootB), Path.GetFullPath(ParseProvenance(responseB).Root));
        Assert.DoesNotContain(Path.GetFullPath(rootB), responseA, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFullPath(rootA), responseB, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixturesWithoutGitRenderUnavailableRevisionAndUnknownDirty()
    {
        (string rootA, string rootB) = await CreateTwoCorporaAsync();

        ProvenanceLine lineA = ParseProvenance(await CreateTools(rootA).ExploreAsync("documentation"));
        ProvenanceLine lineB = ParseProvenance(await CreateTools(rootB).ExploreAsync("documentation"));

        Assert.Equal("unavailable", lineA.Revision);
        Assert.Equal("unknown", lineA.Dirty);
        Assert.Equal("unavailable", lineB.Revision);
        Assert.Equal("unknown", lineB.Dirty);
    }

    [Fact]
    public void DocsStatusIsAPublicReadOnlyClosedWorldToolWithOnlyTheOptionalProjectPath()
    {
        MethodInfo status = FindMcpTool("docs_get_source_repository_and_revision");
        McpServerToolAttribute attribute = status.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal(typeof(Task<string>), status.ReturnType);
        Assert.Equal(["projectPath", "cancellationToken"], status.GetParameters().Select(parameter => parameter.Name));
        Assert.True(attribute.ReadOnly, "docs_get_source_repository_and_revision must declare ReadOnly so clients see readOnlyHint.");
        Assert.False(attribute.OpenWorld, "docs_get_source_repository_and_revision reads a local corpus and must not claim an open world.");
    }

    [Fact]
    public async Task DocsStatusReturnsExactlyTheProvenanceLineTheDocsToolsLeadWith()
    {
        (string rootA, _) = await CreateTwoCorporaAsync();
        DocsTools tools = CreateTools(rootA);
        MethodInfo status = FindMcpTool("docs_get_source_repository_and_revision");

        string expected = ParseProvenance(await tools.ExploreAsync("documentation")).Raw;
        Task<string> invoked = (Task<string>)status.Invoke(tools, [null, CancellationToken.None])!;
        string actual = await invoked;

        Assert.Equal(expected, actual.TrimEnd('\n', '\r'));
    }

    /// <summary>
    /// Two initialized roots whose corpora differ by exactly one document, so a header that
    /// did not actually reflect the live index could not report the same count for both.
    /// </summary>
    private async Task<(string RootA, string RootB)> CreateTwoCorporaAsync()
    {
        string rootA = Path.Combine(_host.Files.Root, "repo-a");
        string rootB = Path.Combine(_host.Files.Root, "repo-b");
        _host.Files.CreateDirectory(rootA);
        _host.Files.CreateDirectory(rootB);
        IDocsScaffoldingService scaffolding = _host.Services().GetRequiredService<IDocsScaffoldingService>();
        await scaffolding.ScaffoldAsync(rootA, owner: "alpha-team");
        await scaffolding.ScaffoldAsync(rootB, owner: "beta-team");
        _host.WriteText(
            rootB,
            "docs/second-only.md",
            "# second-only-marker\n\nThis repository owns a marker no other corpus has.\n");
        return (rootA, rootB);
    }

    private static async IAsyncEnumerable<string> AllResponses(DocsTools tools)
    {
        yield return await tools.ExploreAsync("documentation");
        yield return await tools.ForSymbolAsync("NoSuchSymbolXyz");
        yield return await tools.GlossaryAsync("documentation");
        yield return await tools.AnalysisCandidatesAsync(kind: null, cursor: null, limit: 20, charBudget: 4000);
        // Direct, like the four calls above. Reflection exists in this file for the tests whose
        // subject is a tool's declared metadata — docs_get_source_repository_and_revision's shape — and this one's subject
        // is the response, which a call returns without asking the metadata a question.
        yield return await AgentCodingStandardsMiss(tools);
    }

    /// <summary>
    /// The standard lookup's miss for a technology no corpus declares. Called directly rather
    /// than through the <c>[McpServerTool]</c> metadata <see cref="FindMcpTool"/> reads: the
    /// optional arguments a reflective call has to pass by hand are the ones a direct call
    /// already applies, so the metadata lookup would add an indirection that asserts nothing.
    /// </summary>
    private static Task<string> AgentCodingStandardsMiss(DocsTools tools) =>
        tools.AgentCodingStandardsAsync("zzz-no-such-technology-zzz");

    private DocsTools CreateTools(string root)
    {
        IServiceProvider services = _host.Services();
        return DocsToolsComposition.Create(
            CreateHost(root),
            DocsToolsComposition.AnalysisReader(services, root),
            services: services);
    }

    private DocumentIndexHost CreateHost(string root)
    {
        KyberWeaveConfig config = _host.Services().GetRequiredService<IKyberWeaveConfigRepository>().Load(root);
        return new DocumentIndexHost(
            root,
            () => FakeCodeGraphResolver.WithSymbols(),
            () => new DocumentLoader(_host.Store, root, _host.Paths, _host.Environment, config.Ontology).Load(),
            _host.Store,
            _host.Paths,
            _host.Environment,
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);
    }

    private static MethodInfo FindMcpTool(string toolName)
    {
        MethodInfo? method = typeof(DocsTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
                candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        Assert.True(method is not null, $"DocsTools must expose an MCP tool named '{toolName}'.");
        return method!;
    }

    private static ProvenanceLine ParseProvenance(string response)
    {
        string firstLine = response.Split('\n')[0].TrimEnd('\r');
        Match match = ProvenancePattern.Match(firstLine);
        Assert.True(
            match.Success,
            "Docs tool responses must lead with the provenance line; "
            + $"first line was: '{firstLine}'.");
        return new ProvenanceLine(
            firstLine,
            match.Groups["root"].Value,
            match.Groups["rev"].Value,
            match.Groups["dirty"].Value,
            int.Parse(match.Groups["documents"].Value, CultureInfo.InvariantCulture));
    }

    private sealed record ProvenanceLine(
        string Raw, string Root, string Revision, string Dirty, int Documents);
}
