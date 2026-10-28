using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
///     T1 RED — <c>docs_validate</c> and <c>docs_drift</c> expose the same gates as the CLI
///     through read-only MCP tools rooted at the server's corpus, with provenance and a verdict
///     line the architect can trust before planning or merge-ready work.
/// </summary>
public sealed class McpDocsGateToolsTests : IDisposable
{
    private const string ValidateToolName = "docs_validate";
    private const string DriftToolName = "docs_drift";

    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly Regex ProvenancePattern = new(
        @"^provenance: root=(?<root>.+?) rev=(?<rev>[0-9a-f]{4,40}|unavailable) "
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

    private readonly TempDirectory _parent = new();

    public void Dispose()
    {
        _parent.Dispose();
    }

    [Theory]
    [InlineData(ValidateToolName)]
    [InlineData(DriftToolName)]
    public void GateToolResolvesByMcpNameAsReadOnlyClosedWorldWithoutARepositoryPath(string toolName)
    {
        MethodInfo method = FindMcpTool(toolName);
        McpServerToolAttribute attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal(typeof(string), method.ReturnType);
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
    public void DocsValidateFailsOnUngovernedMarkdownWithSpec001AndFixtureProvenance()
    {
        string root = CreateScaffoldedRoot();
        File.WriteAllText(
            Path.Combine(root, "docs", "ungoverned.md"),
            "# No frontmatter\n\nThis file is not governed.\n");

        DocsTools tools = CreateTools(root, () => FakeCodeGraphResolver.WithSymbols());
        string response = InvokeValidate(tools, false, charBudget: DocumentIndex.DefaultCharBudget);

        ProvenanceLine provenance = ParseProvenance(response);
        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(provenance.Root));
        Assert.StartsWith("result: fail", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains(DocSpecValidator.MissingFrontmatter, response, StringComparison.Ordinal);
    }

    [Fact]
    public void DocsValidatePassesConformantCorpusWhenMergeReadyIsFalse()
    {
        string root = CreateScaffoldedRoot();
        AddActivePlan(root);

        DocsTools tools = CreateTools(root, () => FakeCodeGraphResolver.WithSymbols());
        string response = InvokeValidate(tools, false, charBudget: DocumentIndex.DefaultCharBudget);

        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(ParseProvenance(response).Root));
        Assert.StartsWith("result: pass", ResultLine(response), StringComparison.Ordinal);
        Assert.DoesNotContain(OpenWorkValidator.OpenWork, response, StringComparison.Ordinal);
    }

    [Fact]
    public void DocsValidateReportsLifecycle003OnlyWhenMergeReadyIsTrue()
    {
        string root = CreateScaffoldedRoot();
        AddActivePlan(root);

        DocsTools tools = CreateTools(root, () => FakeCodeGraphResolver.WithSymbols());
        string withoutMergeReady = InvokeValidate(tools, false, charBudget: DocumentIndex.DefaultCharBudget);
        string withMergeReady = InvokeValidate(tools, true, charBudget: DocumentIndex.DefaultCharBudget);

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
    public void DocsDriftFailsOnUnresolvedCodeRefWithPathAndHint()
    {
        string root = CreateScaffoldedRoot();
        const string relativePath = "docs/reference/drift-target.md";
        WriteDriftDocument(root, relativePath, "MissingSymbolForDriftGate", null);

        DocsTools tools = CreateTools(root, () => FakeCodeGraphResolver.WithSymbols());
        string response = InvokeDrift(tools, DocumentIndex.DefaultCharBudget);

        Assert.StartsWith("result: fail", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains(DocDriftLinter.UnresolvedCodeRef, response, StringComparison.Ordinal);
        Assert.Contains(relativePath, response, StringComparison.Ordinal);
        Assert.Contains("hint:", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocsDriftFailsWhenResolverIsUnavailableRatherThanPassing()
    {
        string root = CreateScaffoldedRoot();
        WriteDriftDocument(root, "docs/reference/drift-target.md", "AnySymbol", null);

        DocsTools tools = CreateTools(root, () => new UnavailableCodeGraphResolver());
        string response = InvokeDrift(tools, DocumentIndex.DefaultCharBudget);

        Assert.StartsWith("result: fail", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains("Drift cannot be verified", response, StringComparison.Ordinal);
        Assert.DoesNotContain("result: pass", ResultLine(response), StringComparison.Ordinal);
    }

    [Fact]
    public void DocsDriftPassesWithWarning003StillPresent()
    {
        string root = CreateScaffoldedRoot();
        const string sourceRoot = "1-Presentation/Api";
        Directory.CreateDirectory(Path.Combine(root, sourceRoot.Replace('/', Path.DirectorySeparatorChar)));
        WriteDriftDocument(
            root,
            "docs/reference/warning-only.md",
            "IndexedSymbol",
            sourceRoot);

        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols(
            ("IndexedSymbol", CodeGraphPortTestsNode("IndexedSymbol", "2-Application/Elsewhere/Svc.cs", 1)));

        DocsTools tools = CreateTools(root, () => resolver);
        string response = InvokeDrift(tools, DocumentIndex.DefaultCharBudget);

        Assert.StartsWith("result: pass", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains("KW-DOC-DRIFT-003", response, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedGateResponseLeadsWithProvenanceAndResultThenErrorsBeforeWarnings()
    {
        string root = CreateScaffoldedRoot();
        for (int index = 0; index < 6; index++)
            WriteDriftDocument(
                root,
                $"docs/reference/unresolved-{index}.md",
                $"MissingSymbol{index}",
                null);

        const string warningPath = "docs/reference/source-root-warning.md";
        const string warningSourceRoot = "3-Domain/Unindexed";
        Directory.CreateDirectory(Path.Combine(root, warningSourceRoot.Replace('/', Path.DirectorySeparatorChar)));
        WriteDriftDocument(root, warningPath, "KnownSymbol", warningSourceRoot);

        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithSymbols(
            ("KnownSymbol", CodeGraphPortTestsNode("KnownSymbol", "src/KnownSymbol.cs", 1)));

        DocsTools tools = CreateTools(root, () => resolver);
        string response = InvokeDrift(tools, 1500);

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
        string toolchain = File.ReadAllText(Path.Combine(ProductRoot, "toolchain.yml"));
        string architect = File.ReadAllText(Path.Combine(ProductRoot, "agents", "architect.md"));
        string planAuthoring = File.ReadAllText(
            Path.Combine(ProductRoot, "agents", "architect", "references", "plan-authoring.md"));

        foreach (string text in new[] { instructions, toolchain, architect, planAuthoring })
        {
            Assert.Contains(ValidateToolName, text, StringComparison.Ordinal);
            Assert.Contains(DriftToolName, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SharedArchitectProfileWithholdsProcessExecuteWithoutAskOrAllow()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadCapabilityProfile architect = source.CapabilityProfiles.Profiles["architect"];

        Assert.True(
            architect.Permissions.TryGetValue("process.execute", out SquadPermissionDecision decision),
            "The shared architect profile must declare process.execute.");
        Assert.Equal(SquadPermissionDecision.Deny, decision);
        Assert.NotEqual(SquadPermissionDecision.Ask, decision);
        Assert.NotEqual(SquadPermissionDecision.Allow, decision);
    }

    [Fact]
    public void DocsValidateMergeReadyPassesWhenGitBaseResolvesAndCorpusIsClean()
    {
        using CloseoutGitRepo repo = new();
        DocsScaffolder.Scaffold(repo.Root, owner: "gate-fixture");
        repo.Commit("initial");

        DocsTools tools = CreateTools(repo.Root, () => FakeCodeGraphResolver.WithSymbols());
        string response = InvokeValidate(tools, true, gitBase: "HEAD");

        Assert.StartsWith("result: pass", ResultLine(response), StringComparison.Ordinal);
    }

    [Fact]
    public void DocsValidateMergeReadyFailsWhenGitBaseDoesNotResolve()
    {
        using CloseoutGitRepo repo = new();
        DocsScaffolder.Scaffold(repo.Root, owner: "gate-fixture");
        repo.Commit("initial");

        DocsTools tools = CreateTools(repo.Root, () => FakeCodeGraphResolver.WithSymbols());
        string response = InvokeValidate(tools, true, gitBase: "origin/this-ref-is-not-present");

        Assert.StartsWith("result: fail", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains("scope is unknown (C0)", response, StringComparison.Ordinal);
    }

    [Fact]
    public void DocsValidateMergeReadyFailsClosedWhenArchivedEntryIsSymlink()
    {
        if (!CanCreateSymbolicLinks())
            throw SkipException.ForSkip("This host cannot create symbolic links.");

        using CloseoutGitRepo repo = new();
        using TempDirectory outside = new();
        DocsScaffolder.Scaffold(repo.Root, owner: "gate-fixture");
        repo.Commit("initial");

        string target = Path.Combine(outside.Path, "target.md");
        File.WriteAllText(
            target,
            """
            ---
            id: archive/linked-target
            title: Linked target
            doc-type: plan
            status: superseded
            owner: gate-fixture
            last-reviewed: 2026-10-28
            ---

            # Linked target
            """);

        const string linkRelative = "docs/archive/plans/linked-plan.md";
        string linkPath = Path.Combine(repo.Root, linkRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.CreateSymbolicLink(linkPath, target);

        DocsTools tools = CreateTools(repo.Root, () => FakeCodeGraphResolver.WithSymbols());
        Exception? failure = Record.Exception(() => InvokeValidate(tools, true, gitBase: "HEAD"));
        Assert.Null(failure);
        string response = InvokeValidate(tools, true, gitBase: "HEAD");

        Assert.StartsWith("result: fail", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains("symbolic link", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MinimumCharBudgetStillShowsFirstFindingRuleIdAndOmittedCount()
    {
        string root = CreateScaffoldedRoot();
        const string relativePath = "docs/reference/budget-floor.md";
        string longSymbol = new('M', 900);
        WriteDriftDocument(root, relativePath, longSymbol, null);
        WriteDriftDocument(root, "docs/reference/budget-floor-second.md", "SecondMissingSymbol", null);

        DocsTools tools = CreateTools(root, () => FakeCodeGraphResolver.WithSymbols());
        string response = InvokeDrift(tools, charBudget: 1000);

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

    private string CreateScaffoldedRoot()
    {
        string root = Path.Combine(_parent.Path, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DocsScaffolder.Scaffold(root, owner: "gate-fixture");
        IsolateFromParentGit(root);
        return root;
    }

    private static void AddActivePlan(string root)
    {
        const string planPath = "docs/plans/2026-10-28-active-plan.md";
        File.WriteAllText(
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
        string index = File.ReadAllText(indexPath);
        if (!index.Contains("2026-10-28-active-plan.md", StringComparison.Ordinal))
            File.WriteAllText(
                indexPath,
                index.TrimEnd() + "\n\n- [Active plan for merge-ready gate](2026-10-28-active-plan.md)\n");
    }

    private static void WriteDriftDocument(
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
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void IsolateFromParentGit(string root)
    {
        File.WriteAllText(
            Path.Combine(root, ".git"),
            $"gitdir: {Path.Combine(root, ".git-missing")}{Environment.NewLine}");
    }

    private static DocsTools CreateTools(string root, Func<ICodeGraphResolver> resolverFactory)
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(root);
        DocumentIndexHost host = new(
            root,
            resolverFactory,
            () => new DocumentLoader(root, config.Ontology).Load(),
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);
        return new DocsTools(host);
    }

    private static string InvokeValidate(
        DocsTools tools,
        bool mergeReady,
        string? gitBase = null,
        int charBudget = DocumentIndex.DefaultCharBudget)
    {
        MethodInfo method = FindMcpTool(ValidateToolName);
        return InvokeByParameterNames(
            tools,
            method,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mergeReady"] = mergeReady,
                ["base"] = gitBase,
                ["charBudget"] = charBudget
            });
    }

    private static string InvokeDrift(DocsTools tools, int charBudget = DocumentIndex.DefaultCharBudget)
    {
        MethodInfo method = FindMcpTool(DriftToolName);
        return InvokeByParameterNames(
            tools,
            method,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["charBudget"] = charBudget });
    }

    private static string InvokeByParameterNames(
        DocsTools tools,
        MethodInfo method,
        IReadOnlyDictionary<string, object?> arguments)
    {
        object?[] args = method.GetParameters().Select(parameter =>
        {
            if (arguments.TryGetValue(parameter.Name!, out object? value))
                return value ?? (parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null);

            if (parameter.HasDefaultValue) return parameter.DefaultValue;

            throw new InvalidOperationException(
                $"Missing argument '{parameter.Name}' for MCP tool '{method.GetCustomAttribute<McpServerToolAttribute>()?.Name}'.");
        }).ToArray();

        return (string)method.Invoke(tools, args)!;
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
            "Gate tool responses must lead with the provenance line; "
            + $"first line was: '{firstLine}'.");
        return new ProvenanceLine(
            firstLine,
            match.Groups["root"].Value,
            match.Groups["rev"].Value,
            match.Groups["dirty"].Value,
            int.Parse(match.Groups["documents"].Value, CultureInfo.InvariantCulture));
    }

    private static string ResultLine(string response)
    {
        string[] lines = response.Split('\n');
        Assert.True(lines.Length >= 2, "Gate tool responses must include a result line after provenance.");
        return lines[1].TrimEnd('\r');
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

    private static bool CanCreateSymbolicLinks()
    {
        string probeDirectory = Path.Combine(Path.GetTempPath(), "kw-mcp-symlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDirectory);
        try
        {
            string target = Path.Combine(probeDirectory, "target.txt");
            File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(Path.Combine(probeDirectory, "link.txt"), target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            if (Directory.Exists(probeDirectory))
                Directory.Delete(probeDirectory, true);
        }
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
