using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
///     Contract tests for the worktree MCP canonical binding and subagent exposure protocol (T1).
/// </summary>
/// <remarks>
///     <para>
///         When concurrent tasks execute in isolated git worktrees, MCP servers such as <c>kyber-weave-mcp</c>
///         and <c>codegraph</c> must be bound to the canonical repository root rather than the ephemeral worktree.
///         Worktree MCP setup logic must splice <c>--repo-root</c> and <c>--path</c> arguments to absolute canonical
///         paths, and fail closed if a symbolic link to <c>.codegraph</c> is detected.
///     </para>
///     <para>
///         These tests assert the worktree configuration helper's behaviour and the commands and failure-mode
///         tokens the conductor surfaces name. The prose rules (canonical splicing, the symlink ban, the run-start
///         route, the worktree's own index, isolated config paths) are graded from the prose rule catalog.
///     </para>
/// </remarks>
public sealed class SquadWorktreeMcpContractTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string ConductorAgentPath = "products/kyber-squad/agents/conductor.md";

    /// <summary>
    /// The shared execution contract, which both entry points load. It is a canonical skill rather
    /// than a conductor reference so a fallback target receives it.
    /// </summary>
    private const string ConductorExecutionPath =
        "products/kyber-squad/skills/delivery-execution/SKILL.md";

    private const string ArchitectureDocPath = "docs/kyber-squad/architecture.md";
    private const string McpRunbookDocPath = "docs/docgraph/mcp-runbook.md";

    private static string ReadRepoFile(string relativePath)
    {
        Assert.True(Corpus.Exists(relativePath), $"Expected file at '{relativePath}' does not exist.");
        return Corpus.ReadText(relativePath);
    }

    /// <summary>
    ///     The execution contract names <c>doctor --ensure</c>, the command that gives a new worktree its own
    ///     index, because a worktree does not see the canonical checkout's index.
    /// </summary>
    /// <remarks>
    ///     A git worktree has its own working tree and its own <c>.codegraph/</c>; the canonical index is not
    ///     visible from it. A worker that assumed otherwise queried an index describing files it was not
    ///     editing and got confident answers about the wrong tree. <c>doctor --ensure</c> is the step that
    ///     gives the worktree an index of its own, and it is the one command every worker runs first.
    /// </remarks>
    [Fact]
    public void ExecutionContractNamesTheWorktreeIndexCommand()
    {
        Assert.Contains("doctor --ensure", ReadRepoFile(ConductorExecutionPath), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The execution contract names <c>codegraph explore</c>, the command shell roles run in the worktree's
    ///     own shell rather than the MCP query bound to the canonical checkout.
    /// </summary>
    /// <remarks>
    ///     D24/D25: the MCP servers stay bound to the canonical checkout on purpose, so their answers
    ///     describe that tree. A role with a shell asks its own worktree instead; a role without one is given
    ///     the worktree's absolute path in its packet and passes it as <c>codegraph_explore</c>'s
    ///     <c>projectPath</c>. This is the rule that keeps a worker from believing a branch it is not on.
    /// </remarks>
    [Fact]
    public void ExecutionContractNamesTheShellIndexCommand()
    {
        Assert.Contains("codegraph explore", ReadRepoFile(ConductorExecutionPath), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every surface that tells a shell-less role to ask the worktree's index through <c>projectPath</c>
    ///     names the index failure modes that send it to a direct read.
    /// </summary>
    /// <remarks>
    ///     ADR 0061 decision 7, amended 2026-11-10: the old rule sent shell-less workers to raw file reads
    ///     because the MCP server was bound to the canonical checkout, and agents read it as "do not use
    ///     CodeGraph in a worktree". <c>codegraph_explore</c> takes a <c>projectPath</c> that answers from
    ///     the nearest index at or above it, so the worktree's own index is reachable without a shell. That
    ///     rule, and its retired opposite, are graded from the prose rule catalog.
    /// </remarks>
    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    [InlineData("products/kyber-squad/agents/review-lens.md")]
    [InlineData("products/kyber-squad/agents/review-triage.md")]
    [InlineData("products/kyber-squad/agents/research-agent.md")]
    [InlineData("products/kyber-squad/agents/product-owner.md")]
    [InlineData("products/kyber-squad/skills/product-owner/SKILL.md")]
    [InlineData("products/kyber-squad/agents/architect.md")]
    [InlineData(ArchitectureDocPath)]
    [InlineData(McpRunbookDocPath)]
    [InlineData("docs/kyber-squad/onboarding.md")]
    public void ShellLessRoleSurfacesNameTheIndexFailureModes(string relativePath)
    {
        string content = ReadRepoFile(relativePath);

        Assert.Contains("not-indexed", content, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"(?i)`(?:projectPath|cli): (?:error|not-indexed)`", content);
        Assert.Contains("`projectPath: wrong-branch <got> vs <wanted>`", content, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The conductor surfaces name the restore command for a tracked MCP configuration a worktree
    ///     setup rewrote. The isolated-scratch-path rule itself is graded from the prose rule catalog;
    ///     the runbook carries the per-client list.
    /// </summary>
    [Theory]
    [InlineData(ConductorAgentPath)]
    [InlineData(ConductorExecutionPath)]
    public void ConductorSurfaces_NameTheTrackedMcpConfigRestoreCommand(string relativePath)
    {
        Assert.Contains("git checkout -- <path>", ReadRepoFile(relativePath), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The MCP runbook must enumerate every project-scope MCP configuration a worktree setup can
    ///     rewrite, so the conductor's generic requirement has a concrete list behind it.
    /// </summary>
    [Fact]
    public void McpRunbook_EnumeratesTrackedMcpConfigsRestoredAfterWorktreeSetup()
    {
        string content = ReadRepoFile(McpRunbookDocPath);

        foreach (string config in new[]
                 {
                     ".mcp.json",
                     ".factory/mcp.json",
                     ".cursor/mcp.json",
                     ".vscode/mcp.json",
                     ".agents/mcp_config.json",
                     "opencode.json",
                     "zcode.json",
                     ".zcode/config.json",
                     "kilo.jsonc"
                 })
            Assert.Contains(config, content, StringComparison.Ordinal);
    }
}
