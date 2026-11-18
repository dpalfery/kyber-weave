using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Domain.CodeGraph;
using KyberWeave.Mcp;
using KyberWeave.Tests;
using KyberWeave.Tests.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;
using Xunit.Sdk;
using static KyberWeave.Tests.McpGateToolInvoker;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The merge-ready docs gate against a real git repository: a base ref that resolves or does not, and an archived
/// entry that is a real symbolic link. Git and the link are the subject, so neither is faked; the rest of the gate
/// tool contract runs in memory in <c>McpDocsGateToolsTests</c> in the MCP test project.
/// </summary>
public sealed class McpDocsGateToolsRealGitTests
{
    [Fact]
    public void DocsValidateMergeReadyPassesWhenGitBaseResolvesAndCorpusIsClean()
    {
        using CloseoutGitRepo repo = new();
        DocsTestCommands.Scaffolding.Scaffold(repo.Root, owner: "gate-fixture");
        repo.Commit("initial");

        DocsTools tools = CreateTools(repo.Root, () => FakeCodeGraphResolver.WithSymbols());
        string response = InvokeValidate(tools, true, gitBase: "HEAD");

        Assert.StartsWith("result: pass", ResultLine(response), StringComparison.Ordinal);
    }

    [Fact]
    public void DocsValidateMergeReadyFailsWhenGitBaseDoesNotResolve()
    {
        using CloseoutGitRepo repo = new();
        DocsTestCommands.Scaffolding.Scaffold(repo.Root, owner: "gate-fixture");
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
        DocsTestCommands.Scaffolding.Scaffold(repo.Root, owner: "gate-fixture");
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
        string? captured = null;
        Exception? failure = Record.Exception(() => captured = InvokeValidate(tools, true, gitBase: "HEAD"));
        Assert.Null(failure);
        Assert.NotNull(captured);
        string response = captured;

        Assert.StartsWith("result: fail", ResultLine(response), StringComparison.Ordinal);
        Assert.Contains(ArchiveCloseoutValidator.MissingCloseoutEvidence, response, StringComparison.Ordinal);
        Assert.Contains("symbolic link", response, StringComparison.OrdinalIgnoreCase);
    }

    private static DocsTools CreateTools(string root, Func<ICodeGraphResolver> resolverFactory)
    {
        KyberWeaveConfig config = ConfigFileReader.Load(root);
        DocumentIndexHost host = new(
            root,
            resolverFactory,
            () => RealDocumentLoader.Create(root, config.Ontology).Load(),
            DocsPorts.Files,
            DocsPorts.Paths,
            DocsPorts.Environment,
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);
        return DocsToolsComposition.Create(host);
    }
}
