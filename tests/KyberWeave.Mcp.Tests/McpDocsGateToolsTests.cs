using KyberWeave.Testing.Squad;
using KyberWeave.Contracts.Services;
using KyberWeave.Tests;
using Microsoft.Extensions.DependencyInjection;
using static KyberWeave.Tests.McpGateToolInvoker;

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Domain.CodeGraph;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Mcp.Tests;

/// <summary>
///     T1 RED — <c>docs_validate_governed_document_schema</c> and <c>docs_detect_drift_between_documents_and_code</c> expose the same gates as the CLI
///     through read-only MCP tools rooted at the server's corpus, with provenance and a verdict
///     line the architect can trust before planning or merge-ready work.
/// </summary>
public sealed class McpDocsGateToolsTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly string ProductRoot =
        Path.Combine(Corpus.Root, "products", "kyber-squad");

    /// <summary>
    /// The header contract a gate tool leads with: root, the branch and git directory it was read
    /// from, a short revision or <c>unavailable</c>, a dirty marker or <c>unknown</c>, and the live
    /// document count.
    /// </summary>
    /// <remarks>
    /// The tokens are named rather than assumed adjacent to the root: <c>branch=</c> and
    /// <c>repo=</c> sit between the root and <c>rev=</c>, and a pattern that expected them next to
    /// each other swallowed both into the root capture.
    /// </remarks>
    private static readonly Regex ProvenancePattern = new(
        @"^provenance: root=(?<root>.+?) branch=\S+ repo=.+? rev=(?<rev>[0-9a-f]{4,40}|unavailable) "
        + @"dirty=(?<dirty>yes|no|unknown) documents=(?<documents>\d+)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ResultLinePattern = new(
        @"^result: (pass|fail)\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex OmittedFindingsPattern = new(
        @"\d+ findings? omitted",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] RepositoryPathParameterNames =
    [
        "path",
        "repositoryPath",
        "repoPath",
        "repoRoot",
        "repositoryRoot",
        "root"
    ];

    private readonly MemoryDocsHost _host = new();

    [Theory]
    [InlineData(ValidateToolName)]
    [InlineData(DriftToolName)]
    public void GateToolResolvesByMcpNameAsReadOnlyClosedWorldWithoutARepositoryPath(string toolName)
    {
        MethodInfo method = FindMcpTool(toolName);
        McpServerToolAttribute attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal(typeof(Task<string>), method.ReturnType);
        Assert.True(attribute.ReadOnly, $"{toolName} must declare ReadOnly so clients see readOnlyHint.");
        Assert.False(attribute.OpenWorld, $"{toolName} reads a local corpus and must not claim an open world.");

        foreach (ParameterInfo parameter in method.GetParameters())
            Assert.DoesNotContain(
                parameter.Name!,
                RepositoryPathParameterNames,
                StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocsValidateDefaultsMergeReadyFalseAndCharBudget12000()
    {
        MethodInfo method = FindMcpTool(ValidateToolName);

        ParameterInfo? mergeReady = method.GetParameters().SingleOrDefault(p =>
            string.Equals(p.Name, "mergeReady", StringComparison.Ordinal));
        ParameterInfo? charBudget = method.GetParameters().SingleOrDefault(p =>
            string.Equals(p.Name, "charBudget", StringComparison.Ordinal));

        Assert.NotNull(mergeReady);
        Assert.NotNull(charBudget);
        Assert.True(mergeReady!.HasDefaultValue);
        Assert.False((bool)mergeReady.DefaultValue!);
        Assert.True(charBudget!.HasDefaultValue);
        Assert.Equal(DocumentIndex.DefaultCharBudget, charBudget.DefaultValue);
    }

    [Fact]
    public void DocsDriftDefaultsCharBudget12000()
    {
        MethodInfo method = FindMcpTool(DriftToolName);

        ParameterInfo? charBudget = method.GetParameters().SingleOrDefault(p =>
            string.Equals(p.Name, "charBudget", StringComparison.Ordinal));

        Assert.NotNull(charBudget);
        Assert.True(charBudget!.HasDefaultValue);
        Assert.Equal(DocumentIndex.DefaultCharBudget, charBudget.DefaultValue);
    }

    [Fact]
    public async Task DocsValidateFailsOnUngovernedMarkdownWithSpec001AndFixtureProvenance()
    {
        string root = await CreateScaffoldedRootAsync();
        _host.Files.WriteAllText(
            Path.Combine(root, "docs", "ungoverned.md"),
            "# No frontmatter\n\nThis file is not governed.\n");

        DocsTools tools = CreateTools(root, () => FakeCodeGraphResolver.WithSymbols());
        string response = await InvokeValidateAsync(tools, false, charBudget: DocumentIndex.DefaultCharBudget);

        ProvenanceLine provenance = ParseProvenance(response);
        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(provenance.Root));
        Assert.StartsWith("result: fail", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains(DocSpecValidator.MissingFrontmatter, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsValidatePassesConformantCorpusWhenMergeReadyIsFalse()
    {
        string root = await CreateScaffoldedRootAsync();
        AddActivePlan(root);

        DocsTools tools = CreateTools(root, () => FakeCodeGraphResolver.WithSymbols());
        string response = await InvokeValidateAsync(tools, false, charBudget: DocumentIndex.DefaultCharBudget);

        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(ParseProvenance(response).Root));
        Assert.StartsWith("result: pass", ResultLine(response), StringComparison.Ordinal);
        Assert.DoesNotContain(OpenWorkValidator.OpenWork, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsValidateReportsLifecycle003OnlyWhenMergeReadyIsTrue()
    {
        string root = await CreateScaffoldedRootAsync();
        AddActivePlan(root);

        DocsTools tools = CreateTools(root, () => FakeCodeGraphResolver.WithSymbols());
        string withoutMergeReady = await InvokeValidateAsync(tools, false, charBudget: DocumentIndex.DefaultCharBudget);
        string withMergeReady = await InvokeValidateAsync(tools, true, charBudget: DocumentIndex.DefaultCharBudget);

        Assert.DoesNotContain(OpenWorkValidator.OpenWork, withoutMergeReady, StringComparison.Ordinal);
        Assert.StartsWith("result: pass", ResultLine(withoutMergeReady), StringComparison.Ordinal);

        Assert.StartsWith("result: fail", ResultLine(withMergeReady), StringComparison.Ordinal);
        Assert.Contains(OpenWorkValidator.OpenWork, withMergeReady, StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountDistinctRuleCodes(withMergeReady, OpenWorkValidator.OpenWork));
        Assert.DoesNotContain(DocSpecValidator.MissingFrontmatter, withMergeReady, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsDriftFailsOnUnresolvedCodeRefWithPathAndHint()
    {
        string root = await CreateScaffoldedRootAsync();
        const string relativePath = "docs/reference/drift-target.md";
        WriteDriftDocument(root, relativePath, "MissingSymbolForDriftGate", null);

        DocsTools tools = CreateTools(root, () => FakeCodeGraphResolver.WithSymbols());
        string response = await InvokeDriftAsync(tools, DocumentIndex.DefaultCharBudget);

        Assert.StartsWith("result: fail", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains(DocDriftLinter.UnresolvedCodeRef, response, StringComparison.Ordinal);
        Assert.Contains(relativePath, response, StringComparison.Ordinal);
        Assert.Contains("hint:", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DocsDriftFailsWhenResolverIsUnavailableRatherThanPassing()
    {
        string root = await CreateScaffoldedRootAsync();
        WriteDriftDocument(root, "docs/reference/drift-target.md", "AnySymbol", null);

        DocsTools tools = CreateTools(root, () => new UnavailableCodeGraphResolver());
        string response = await InvokeDriftAsync(tools, DocumentIndex.DefaultCharBudget);

        Assert.StartsWith("result: fail", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains("Drift cannot be verified", response, StringComparison.Ordinal);
        Assert.DoesNotContain("result: pass", ResultLine(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsDriftPassesWithWarning003StillPresent()
    {
        string root = await CreateScaffoldedRootAsync();
        const string sourceRoot = "1-Presentation/Api";
        _host.Files.CreateDirectory(Path.Combine(root, sourceRoot.Replace('/', Path.DirectorySeparatorChar)));
        WriteDriftDocument(
            root,
            "docs/reference/warning-only.md",
            "IndexedSymbol",
            sourceRoot);

        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols(
            ("IndexedSymbol", CodeGraphPortTestsNode("IndexedSymbol", "2-Application/Elsewhere/Svc.cs", 1)));

        DocsTools tools = CreateTools(root, () => resolver);
        string response = await InvokeDriftAsync(tools, DocumentIndex.DefaultCharBudget);

        Assert.StartsWith("result: pass", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains("KW-DOC-DRIFT-003", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedGateResponseLeadsWithProvenanceAndResultThenErrorsBeforeWarnings()
    {
        string root = await CreateScaffoldedRootAsync();
        for (int index = 0; index < 6; index++)
            WriteDriftDocument(
                root,
                $"docs/reference/unresolved-{index}.md",
                $"MissingSymbol{index}",
                null);

        const string warningPath = "docs/reference/source-root-warning.md";
        const string warningSourceRoot = "3-Domain/Unindexed";
        _host.Files.CreateDirectory(Path.Combine(root, warningSourceRoot.Replace('/', Path.DirectorySeparatorChar)));
        WriteDriftDocument(root, warningPath, "KnownSymbol", warningSourceRoot);

        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols(
            ("KnownSymbol", CodeGraphPortTestsNode("KnownSymbol", "src/KnownSymbol.cs", 1)));

        DocsTools tools = CreateTools(root, () => resolver);
        string response = await InvokeDriftAsync(tools, 1500);

        ParseProvenance(response);
        string resultLine = ResultLine(response);
        Assert.Matches(ResultLinePattern, resultLine);
        Assert.Matches(OmittedFindingsPattern, response);

        int lastErrorIndex = LastFindingIndex(response, DocDriftLinter.UnresolvedCodeRef);
        int firstWarningIndex = FirstFindingIndex(response, "KW-DOC-DRIFT-003");
        Assert.True(lastErrorIndex >= 0, "Expected at least one error-severity drift finding.");
        Assert.True(firstWarningIndex >= 0, "Expected the warning-only drift finding.");
        Assert.True(lastErrorIndex < firstWarningIndex, "Error-severity findings must precede warnings.");
    }

    [Fact]
    public void GateToolingInstructionsAndRosterNameDocsValidateAndDocsDrift()
    {
        string instructions = DocsServerInstructions.Text;
        string toolchain = Corpus.ReadText("products/kyber-squad/toolchain.yml");
        string architect = Corpus.ReadText("products/kyber-squad/agents/architect.md");
        string planAuthoring = Corpus.ReadText("products/kyber-squad/agents/architect/references/plan-authoring.md");

        foreach (string text in new[] { instructions, toolchain, architect, planAuthoring })
        {
            Assert.Contains(ValidateToolName, text, StringComparison.Ordinal);
            Assert.Contains(DriftToolName, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SharedArchitectProfileGrantsProcessExecute()
    {
        SquadSource source = RealSquadServices.Load(ProductRoot);
        SquadCapabilityProfile architect = source.CapabilityProfiles.Profiles["architect"];

        Assert.True(
            architect.Permissions.TryGetValue("process.execute", out SquadPermissionDecision decision),
            "The shared architect profile must declare process.execute.");
        // The shell lets the architect run the CLI gates and the CodeGraph CLI in a worktree.
        // The lattice cannot scope commands; the agent body's command list and the Arbiter
        // classification (not pinned here) are the controls. ask would narrow to deny on
        // some targets.
        Assert.Equal(SquadPermissionDecision.Allow, decision);
    }

    [Fact]
    public async Task MinimumCharBudgetStillShowsFirstFindingRuleIdAndOmittedCount()
    {
        string root = await CreateScaffoldedRootAsync();
        const string relativePath = "docs/reference/budget-floor.md";
        string longSymbol = new('M', 900);
        WriteDriftDocument(root, relativePath, longSymbol, null);
        WriteDriftDocument(root, "docs/reference/budget-floor-second.md", "SecondMissingSymbol", null);

        DocsTools tools = CreateTools(root, () => FakeCodeGraphResolver.WithSymbols());
        string response = await InvokeDriftAsync(tools, charBudget: 1000);

        ParseProvenance(response);
        Assert.StartsWith("result: fail", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains(DocDriftLinter.UnresolvedCodeRef, response, StringComparison.Ordinal);
        Assert.Matches(OmittedFindingsPattern, response);

        int resultLineIndex = Array.FindIndex(
            response.Split('\n'),
            line => line.StartsWith("result:", StringComparison.Ordinal));
        string[] lines = response.Split('\n');
        Assert.True(resultLineIndex >= 0);
        Assert.True(resultLineIndex + 1 < lines.Length);
        Assert.StartsWith(
            DocDriftLinter.UnresolvedCodeRef,
            lines[resultLineIndex + 1].TrimStart(),
            StringComparison.Ordinal);
    }

    private async Task<string> CreateScaffoldedRootAsync()
    {
        string root = _host.Repo;
        await _host.Services().GetRequiredService<IDocsScaffoldingService>().ScaffoldAsync(root, owner: "gate-fixture");
        return root;
    }

    private void AddActivePlan(string root)
    {
        const string planPath = "docs/plans/2026-10-28-active-plan.md";
        _host.Files.WriteAllText(
            Path.Combine(root, planPath),
            """
            ---
            id: plans/2026-10-28-active
            title: Active plan for merge-ready gate
            doc-type: plan
            status: draft
            owner: gate-fixture
            component: Example
            last-reviewed: 2026-10-28
            ---

            # Active plan

            Still open in the plans folder.
            """);

        string indexPath = Path.Combine(root, "docs", "plans", "README.md");
        string index = _host.Files.ReadAllText(indexPath);
        if (!index.Contains("2026-10-28-active-plan.md", StringComparison.Ordinal))
            _host.Files.WriteAllText(
                indexPath,
                index.TrimEnd() + "\n\n- [Active plan for merge-ready gate](2026-10-28-active-plan.md)\n");
    }

    private void WriteDriftDocument(
        string root,
        string relativePath,
        string symbol,
        string? sourceRoot)
    {
        string sourceRootLine = sourceRoot is null
            ? string.Empty
            : $"source-root: {sourceRoot}\n";
        string content =
            $"""
             ---
             id: reference/drift-{symbol}
             title: Drift document
             doc-type: reference
             status: current
             owner: gate-fixture
             last-reviewed: 2026-10-28
             {sourceRootLine}code-refs:
               - {symbol}
             ---

             # Drift document

             References {symbol}.
             """;
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _host.Files.CreateDirectory(Path.GetDirectoryName(full)!);
        _host.Files.WriteAllText(full, content);
    }

    private DocsTools CreateTools(string root, Func<ICodeGraphResolver> resolverFactory)
    {
        IServiceProvider services = _host.Services();
        KyberWeaveConfig config = services.GetRequiredService<IKyberWeaveConfigRepository>().Load(root);
        DocumentIndexHost host = new(
            root,
            resolverFactory,
            () => new DocumentLoader(_host.Store, root, _host.Paths, _host.Environment, config.Ontology).Load(),
            _host.Store,
            _host.Paths,
            _host.Environment,
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);
        return DocsToolsComposition.Create(host, services: services);
    }

    private static ProvenanceLine ParseProvenance(string response)
    {
        string firstLine = response.Split('\n')[0].TrimEnd('\r');
        Match match = ProvenancePattern.Match(firstLine);
        Assert.True(
            match.Success,
            "Gate tool responses must lead with the provenance line; "
            + $"first line was: '{firstLine}'.");
        return new ProvenanceLine(
            firstLine,
            match.Groups["root"].Value,
            match.Groups["rev"].Value,
            match.Groups["dirty"].Value,
            int.Parse(match.Groups["documents"].Value, CultureInfo.InvariantCulture));
    }

    private static int CountDistinctRuleCodes(string response, string code)
    {
        return response.Split('\n').Count(line => line.Contains(code, StringComparison.Ordinal));
    }

    private static int LastFindingIndex(string response, string code)
    {
        string[] lines = response.Split('\n');
        int last = -1;
        for (int index = 0; index < lines.Length; index++)
            if (lines[index].Contains(code, StringComparison.Ordinal))
                last = index;

        return last;
    }

    private static int FirstFindingIndex(string response, string code)
    {
        string[] lines = response.Split('\n');
        for (int index = 0; index < lines.Length; index++)
            if (lines[index].Contains(code, StringComparison.Ordinal))
                return index;

        return -1;
    }

    private static CodeGraphNode CodeGraphPortTestsNode(string name, string filePath, int line)
    {
        return new CodeGraphNode(name, "class", name, name, filePath, "csharp", line);
    }

    private sealed record ProvenanceLine(
        string Raw,
        string Root,
        string Revision,
        string Dirty,
        int Documents);

    private sealed class UnavailableCodeGraphResolver : ICodeGraphResolver
    {
        public bool IsAvailable => false;
        public string? UnavailableReason => "No CodeGraph index at .codegraph/codegraph.db.";
        public string DatabasePath => ".codegraph/codegraph.db";

        public IReadOnlyList<CodeGraphNode> ResolveSymbol(string name)
        {
            return [];
        }

        public IReadOnlyList<CodeGraphNode> ResolveRoute(string route)
        {
            return [];
        }

        public bool HasFilesUnder(string relativePathPrefix)
        {
            return false;
        }

        public IReadOnlyList<string> CandidateNames(string like)
        {
            return [];
        }

        public IReadOnlyList<string> AllRoutes()
        {
            return [];
        }
    }
}
