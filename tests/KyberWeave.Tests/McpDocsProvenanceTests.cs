using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T1/T2 RED — every docs tool response discloses the corpus it answered from, and
/// <c>docs_status</c> exposes the same provenance block on demand.
/// </summary>
/// <remarks>
/// Regression for issue #162: the four docs tools led with hit counts only. A same-named
/// server bound to another checkout answered with the same confident prose, so a stale
/// corpus could outrank the checkout actually being edited with no visible tell. The header
/// contract is the tell: root, revision/dirty, and the live document count.
/// </remarks>
public sealed class McpDocsProvenanceTests : IDisposable
{
    /// <summary>
    /// The header contract from the plan: exactly one line, root first, with a short SHA or
    /// the literal <c>unavailable</c>, and a dirty marker or <c>unknown</c>.
    /// </summary>
    private static readonly Regex ProvenancePattern = new(
        @"^provenance: root=(?<root>.+?) rev=(?<rev>[0-9a-f]{4,40}|unavailable) "
        + @"dirty=(?<dirty>yes|no|unknown) documents=(?<documents>\d+)$",
        RegexOptions.CultureInvariant);

    private readonly TempDirectory _parent = new();

    [Fact]
    public void EachDocsToolLeadsWithProvenanceForItsOwnRepositoryRoot()
    {
        (string rootA, _) = CreateTwoCorpora();
        DocsTools tools = CreateTools(rootA);
        int expectedDocuments = CreateHost(rootA).Current().DocumentCount;

        foreach (string response in AllResponses(tools))
        {
            ProvenanceLine line = ParseProvenance(response);
            Assert.Equal(Path.GetFullPath(rootA), Path.GetFullPath(line.Root));
            Assert.Equal(expectedDocuments, line.Documents);
        }
    }

    [Fact]
    public void MissAndEmptyResponsesStillLeadWithProvenance()
    {
        (string rootA, _) = CreateTwoCorpora();
        DocsTools tools = CreateTools(rootA);

        string miss = tools.Explore(
            "zzz-no-such-subject-zzz", maxDocs: 5, charBudget: DocumentIndex.DefaultCharBudget);
        string symbolMiss = tools.ForSymbol("NoSuchSymbolXyz");
        string glossaryMiss = tools.Glossary("no-such-term-xyz");
        string analysis = tools.AnalysisCandidates(kind: null, cursor: null, limit: 20, charBudget: 4000);

        foreach (string response in new[] { miss, symbolMiss, glossaryMiss, analysis })
        {
            ProvenanceLine line = ParseProvenance(response);
            Assert.Equal(Path.GetFullPath(rootA), Path.GetFullPath(line.Root));
        }
    }

    [Fact]
    public void ProvenanceDocumentCountsDifferBetweenTwoCorpora()
    {
        (string rootA, string rootB) = CreateTwoCorpora();
        int countA = CreateHost(rootA).Current().DocumentCount;
        int countB = CreateHost(rootB).Current().DocumentCount;

        Assert.NotEqual(countA, countB);

        ProvenanceLine lineA = ParseProvenance(
            CreateTools(rootA).Explore("documentation", maxDocs: 1, charBudget: 1000));
        ProvenanceLine lineB = ParseProvenance(
            CreateTools(rootB).Explore("documentation", maxDocs: 1, charBudget: 1000));

        Assert.Equal(countA, lineA.Documents);
        Assert.Equal(countB, lineB.Documents);
    }

    [Fact]
    public void NeitherRootsProvenanceAppearsInTheOtherRootsResponses()
    {
        (string rootA, string rootB) = CreateTwoCorpora();
        string responseA = CreateTools(rootA).Explore("documentation", maxDocs: 1, charBudget: 1000);
        string responseB = CreateTools(rootB).Explore("documentation", maxDocs: 1, charBudget: 1000);

        Assert.Equal(Path.GetFullPath(rootA), Path.GetFullPath(ParseProvenance(responseA).Root));
        Assert.Equal(Path.GetFullPath(rootB), Path.GetFullPath(ParseProvenance(responseB).Root));
        Assert.DoesNotContain(Path.GetFullPath(rootB), responseA, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFullPath(rootA), responseB, StringComparison.Ordinal);
    }

    [Fact]
    public void FixturesWithoutGitRenderUnavailableRevisionAndUnknownDirty()
    {
        (string rootA, string rootB) = CreateTwoCorpora();

        ProvenanceLine lineA = ParseProvenance(
            CreateTools(rootA).Explore("documentation", maxDocs: 1, charBudget: 1000));
        ProvenanceLine lineB = ParseProvenance(
            CreateTools(rootB).Explore("documentation", maxDocs: 1, charBudget: 1000));

        Assert.Equal("unavailable", lineA.Revision);
        Assert.Equal("unknown", lineA.Dirty);
        Assert.Equal("unavailable", lineB.Revision);
        Assert.Equal("unknown", lineB.Dirty);
    }

    [Fact]
    public void DocsStatusIsAPublicReadOnlyClosedWorldToolWithNoParameters()
    {
        MethodInfo status = FindMcpTool("docs_status");
        McpServerToolAttribute attribute = status.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal(typeof(string), status.ReturnType);
        Assert.Empty(status.GetParameters());
        Assert.True(attribute.ReadOnly, "docs_status must declare ReadOnly so clients see readOnlyHint.");
        Assert.False(attribute.OpenWorld, "docs_status reads a local corpus and must not claim an open world.");
    }

    [Fact]
    public void DocsStatusReturnsExactlyTheProvenanceLineTheDocsToolsLeadWith()
    {
        (string rootA, _) = CreateTwoCorpora();
        DocsTools tools = CreateTools(rootA);
        MethodInfo status = FindMcpTool("docs_status");

        string expected = ParseProvenance(
            tools.Explore("documentation", maxDocs: 1, charBudget: 1000)).Raw;
        string actual = (string)status.Invoke(tools, null)!;

        Assert.Equal(expected, actual.TrimEnd('\n', '\r'));
    }

    public void Dispose() => _parent.Dispose();

    /// <summary>
    /// Two initialized roots whose corpora differ by exactly one document, so a header that
    /// did not actually reflect the live index could not report the same count for both.
    /// </summary>
    private (string RootA, string RootB) CreateTwoCorpora()
    {
        string rootA = Path.Combine(_parent.Path, "repo-a");
        string rootB = Path.Combine(_parent.Path, "repo-b");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        DocsScaffolder.Scaffold(rootA, owner: "alpha-team");
        DocsScaffolder.Scaffold(rootB, owner: "beta-team");
        // Stop git from walking up into a parent checkout when TMPDIR sits inside one:
        // an invalid local gitdir keeps rev=unavailable / dirty=unknown deterministic.
        IsolateFromParentGit(rootA);
        IsolateFromParentGit(rootB);
        File.WriteAllText(
            Path.Combine(rootB, "docs", "second-only.md"),
            "# second-only-marker\n\nThis repository owns a marker no other corpus has.\n");
        return (rootA, rootB);
    }

    private static IEnumerable<string> AllResponses(DocsTools tools)
    {
        yield return tools.Explore("documentation", maxDocs: 1, charBudget: 1000);
        yield return tools.ForSymbol("NoSuchSymbolXyz");
        yield return tools.Glossary("documentation");
        yield return tools.AnalysisCandidates(kind: null, cursor: null, limit: 20, charBudget: 4000);
    }

    private static void IsolateFromParentGit(string root) =>
        File.WriteAllText(
            Path.Combine(root, ".git"),
            $"gitdir: {Path.Combine(root, ".git-missing")}{Environment.NewLine}");

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
