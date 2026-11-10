using System.Reflection;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

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
///         These contract tests assert both the governed conductor execution contract surfaces and the worktree
///         configuration helper invariants.
///     </para>
/// </remarks>
public sealed class SquadWorktreeMcpContractTests
{
    private const string ConductorAgentPath = "products/kyber-squad/agents/conductor.md";

    /// <summary>
    /// The shared execution contract, which both entry points load. It is a canonical skill rather
    /// than a conductor reference so a fallback target receives it.
    /// </summary>
    private const string ConductorExecutionPath =
        "products/kyber-squad/skills/delivery-execution/SKILL.md";

    private const string ArchitectureDocPath = "docs/kyber-squad/architecture.md";
    private const string McpRunbookDocPath = "docs/docgraph/mcp-runbook.md";

    private const string BugCrusherSkillPath = "products/kyber-squad/skills/bug-crusher/SKILL.md";

    /// <summary>
    ///     The condition that starts the second route: the harness gives the orchestrator no way to run a
    ///     command itself. The nouns are the ones the shipped prose may use for that capability.
    /// </summary>
    private static readonly Regex NoShellCondition = new(
        @"(?i)\b(?:no|without|lacks?|has no|have no)\b[^\r\n]{0,60}" +
        @"\b(?:shell|terminal|subprocess|bash|command execution|command runner)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Handing the step to a worker instead of running it in the orchestrator's own context.</summary>
    private static readonly Regex DispatchRoute = new(
        @"(?i)\bdispatc(?:h|hes|hing|hed)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string ReadRepoFile(string relativePath)
    {
        string fullPath = Path.Combine(KyberWeaveTestPaths.ToolRoot, relativePath);
        Assert.True(File.Exists(fullPath), $"Expected file at '{fullPath}' does not exist.");
        return File.ReadAllText(fullPath);
    }

    /// <summary>
    ///     Conductor surfaces must specify that worktree MCP setup splices <c>--repo-root</c> and <c>--path</c>
    ///     arguments to absolute canonical paths.
    /// </summary>
    [Theory]
    [InlineData(ConductorAgentPath)]
    [InlineData(ConductorExecutionPath)]
    public void ConductorSurfaces_SpecifyWorktreeMcpSetup_SplicesRepoRootAndPathToAbsoluteCanonicalPaths(
        string relativePath)
    {
        string content = ReadRepoFile(relativePath);

        Assert.Matches(
            @"(?i)\bworktree\s+MCP\s+(?:setup|binding|configuration)[^\r\n]*(?:splice|splicing)[^\r\n]*--repo-root\b",
            content);
        Assert.Matches(
            @"(?i)\b--path\s+to\s+absolute\s+canonical\s+paths?\b",
            content);
    }

    /// <summary>
    ///     Conductor surfaces must specify that worktree MCP setup fails closed if a symlink to <c>.codegraph</c>
    ///     is detected.
    /// </summary>
    [Theory]
    [InlineData(ConductorAgentPath)]
    [InlineData(ConductorExecutionPath)]
    public void ConductorSurfaces_SpecifyWorktreeMcpSetup_FailsClosedIfCodegraphSymlinkDetected(string relativePath)
    {
        string content = ReadRepoFile(relativePath);

        Assert.Matches(
            @"(?i)\b(?:fail(?:s|ing)?\s+closed|reject(?:s|ing)?|forbid(?:s|den)?)\b[^\r\n]*(?:symlink[^\r\n]*\.codegraph|\.codegraph\s+symlink)\b",
            content);
    }

    /// <summary>
    ///     The conductor's "never block a run" line and its instruction to run the run-start check only
    ///     agree when the check has a second route: run it in the harness's shell when the orchestrator
    ///     has one, and dispatch a worker to run it when the harness gives the orchestrator none. Both
    ///     entry points state that route, so neither has to choose between blocking the run and skipping
    ///     the check.
    /// </summary>
    [Theory]
    [InlineData(ConductorAgentPath)]
    [InlineData(BugCrusherSkillPath)]
    public void RunStartSurfaces_RouteTheCheckThroughTheShellOrDispatchSeam(string relativePath)
    {
        string content = ReadRepoFile(relativePath);

        Assert.Matches(NoShellCondition, content);
        Assert.Matches(DispatchRoute, content);
    }

    /// <summary>
    ///     Architecture and DocGraph MCP runbook documentation must specify the worktree MCP canonical binding
    ///     protocol and the prohibition against <c>.codegraph</c> symlinks.
    /// </summary>
    [Theory]
    [InlineData(ArchitectureDocPath)]
    [InlineData(McpRunbookDocPath)]
    public void ArchitectureAndRunbook_SpecifyWorktreeMcpCanonicalBindingAndSymlinkBan(string relativePath)
    {
        string content = ReadRepoFile(relativePath);

        Assert.Matches(
            @"(?i)\bworktree\s+MCP\s+(?:canonical\s+binding|protocol|binding)\b",
            content);
        Assert.Matches(
            @"(?i)\b(?:ban|prohibit|reject|forbid)\b[^\r\n]*\.codegraph\s+symlink\w*\b",
            content);
    }

    /// <summary>
    ///     The execution contract states that a new worktree indexes itself, because a worktree does not see
    ///     the canonical checkout's index.
    /// </summary>
    /// <remarks>
    ///     A git worktree has its own working tree and its own <c>.codegraph/</c>; the canonical index is not
    ///     visible from it. A worker that assumed otherwise queried an index describing files it was not
    ///     editing and got confident answers about the wrong tree. <c>doctor --ensure</c> is the step that
    ///     gives the worktree an index of its own, and it is the one command every worker runs first.
    /// </remarks>
    [Fact]
    public void ExecutionContractStatesThatAWorktreeIndexesItself()
    {
        string content = ReadRepoFile(ConductorExecutionPath);

        Assert.Contains("doctor --ensure", content, StringComparison.Ordinal);
        Assert.Matches(@"(?i)\bworktree\b[^\r\n]*\b(?:index\w*|own)\b", content);
    }

    /// <summary>
    ///     The execution contract sends shell roles to <c>codegraph explore</c> in the worktree's own shell,
    ///     rather than to the MCP query bound to the canonical checkout.
    /// </summary>
    /// <remarks>
    ///     D24/D25: the MCP servers stay bound to the canonical checkout on purpose, so their answers
    ///     describe that tree. A role with a shell asks its own worktree instead; a role without one is given
    ///     the worktree's absolute path in its packet and passes it as <c>codegraph_explore</c>'s
    ///     <c>projectPath</c>. This is the rule that keeps a worker from believing a branch it is not on.
    /// </remarks>
    [Fact]
    public void ExecutionContractSendsShellRolesToTheWorktreeIndex()
    {
        string content = ReadRepoFile(ConductorExecutionPath);

        Assert.Contains("codegraph explore", content, StringComparison.Ordinal);
        Assert.Matches(@"(?i)\bcanonical\b[^\r\n]*(?:bound|checkout|path)\b", content);
    }

    /// <summary>
    ///     Shell-less roles ask the worktree's own index through <c>projectPath</c>, and no shipped role or
    ///     skill tells them to read the worktree directly instead.
    /// </summary>
    /// <remarks>
    ///     ADR 0061 decision 7, amended 2026-11-10: the old rule sent shell-less workers to raw file reads
    ///     because the MCP server was bound to the canonical checkout, and agents read it as "do not use
    ///     CodeGraph in a worktree". <c>codegraph_explore</c> takes a <c>projectPath</c> that answers from
    ///     the nearest index at or above it, so the worktree's own index is reachable without a shell.
    /// </remarks>
    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    [InlineData("products/kyber-squad/agents/review-lens.md")]
    [InlineData("products/kyber-squad/agents/review-triage.md")]
    [InlineData("products/kyber-squad/agents/azure-reader.md")]
    [InlineData("products/kyber-squad/agents/research-agent.md")]
    [InlineData("products/kyber-squad/agents/product-owner.md")]
    [InlineData("products/kyber-squad/skills/product-owner/SKILL.md")]
    [InlineData("products/kyber-squad/agents/architect.md")]
    public void ShellLessRolesAskTheWorktreeIndexThroughProjectPath(string relativePath)
    {
        string content = ReadRepoFile(relativePath);

        Assert.Contains("`projectPath`", content, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)\breads? it directly\b", content);
        Assert.DoesNotMatch(@"(?i)read that path directly and count", content);
    }

    /// <summary>
    ///     Verifies that worktree configuration helpers produce absolute paths for <c>--repo-root</c>
    ///     and <c>--path</c>.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_SplicesRepoRootAndPathToAbsoluteCanonicalPaths()
    {
        Type? helperType = Type.GetType("KyberWeave.Core.Squad.Deployment.SquadWorktreeMcp, KyberWeave.Core.Squad")
                           ?? Type.GetType(
                               "KyberWeave.Core.Squad.Deployment.SquadWorktreeMcpConfiguration, KyberWeave.Core.Squad");

        Assert.True(
            helperType is not null,
            "Worktree MCP helper 'KyberWeave.Core.Squad.Deployment.SquadWorktreeMcp' must be implemented in KyberWeave.Core.Squad.");

        MethodInfo? spliceMethod = helperType.GetMethod(
            "SpliceArguments",
            BindingFlags.Public | BindingFlags.Static);

        Assert.True(
            spliceMethod is not null,
            "SquadWorktreeMcp must define a public static SpliceArguments method.");

        string canonicalRoot = Path.GetFullPath("/canonical/repo/root");
        string[] inputArgs = ["--repo-root", ".", "--path", "./subpath", "--other", "val"];

        object? result = spliceMethod.Invoke(null, [inputArgs, canonicalRoot]);
        IReadOnlyList<string>? spliced = result as IReadOnlyList<string>;
        Assert.NotNull(spliced);

        int repoRootIndex = -1;
        int pathIndex = -1;
        for (int i = 0; i < spliced.Count; i++)
            if (spliced[i] == "--repo-root")
                repoRootIndex = i;
            else if (spliced[i] == "--path") pathIndex = i;

        Assert.True(repoRootIndex >= 0 && repoRootIndex + 1 < spliced.Count,
            "--repo-root must be present with a value.");
        Assert.Equal(canonicalRoot, spliced[repoRootIndex + 1]);

        Assert.True(pathIndex >= 0 && pathIndex + 1 < spliced.Count, "--path must be present with a value.");
        Assert.Equal(Path.GetFullPath("./subpath", canonicalRoot), spliced[pathIndex + 1]);
    }

    /// <summary>
    ///     Verifies that worktree configuration helpers reject worktree directories where <c>.codegraph</c>
    ///     is a symbolic link.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_FailsClosedWhenCodegraphSymlinkIsDetected()
    {
        using TempDirectory worktreeDir = new();
        using TempDirectory targetDir = new();

        SquadWorktreeMcp.EnsureNoCodegraphSymlink(worktreeDir.Path);

        string codegraphSymlink = Path.Combine(worktreeDir.Path, ".codegraph");
        Directory.CreateSymbolicLink(codegraphSymlink, targetDir.Path);

        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => SquadWorktreeMcp.EnsureNoCodegraphSymlink(worktreeDir.Path));
        Assert.Contains(".codegraph", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Path traversal arguments such as --path ../../../etc resolving outside the canonical root
    ///     must be rejected by throwing ArgumentException.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_PathTraversalOutsideCanonicalRoot_ThrowsArgumentException()
    {
        string canonicalRoot = Path.GetFullPath("/canonical/repo/root");
        string[] inputArgs = ["--path", "../../../etc"];

        Assert.Throws<ArgumentException>(() => SquadWorktreeMcp.SpliceArguments(inputArgs, canonicalRoot));
    }

    /// <summary>
    ///     A relative canonical root would be resolved against the caller's current directory, which
    ///     may be the worktree rather than the canonical checkout, so it must fail closed.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_WhenCanonicalRootIsRelative_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => SquadWorktreeMcp.SpliceArguments(["--repo-root", "."], "relative/canonical/root"));
    }

    /// <summary>
    ///     A trailing <c>--repo-root</c> without a value must fail closed rather than pass the bare
    ///     flag to the child process.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_WhenRepoRootFlagHasNoValue_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => SquadWorktreeMcp.SpliceArguments(["--repo-root"], Path.GetFullPath("/canonical/repo/root")));
    }

    /// <summary>
    ///     A trailing <c>--path</c> without a value must fail closed rather than pass the bare flag
    ///     to the child process.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_WhenPathFlagHasNoValue_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => SquadWorktreeMcp.SpliceArguments(["--path"], Path.GetFullPath("/canonical/repo/root")));
    }

    /// <summary>
    ///     A null element in the argument list must be rejected as an argument error, not escape as
    ///     a <see cref="NullReferenceException" />.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_WhenArgumentElementIsNull_ThrowsArgumentException()
    {
        string[] inputArgs = ["--repo-root", ".", null!];

        Assert.Throws<ArgumentException>(
            () => SquadWorktreeMcp.SpliceArguments(inputArgs, Path.GetFullPath("/canonical/repo/root")));
    }

    /// <summary>
    ///     A <c>--path</c> that is a symlink to a directory outside the canonical root must be
    ///     rejected: lexical normalization alone does not follow the link, so real I/O would escape.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_WhenPathSymlinkEscapesCanonicalRoot_ThrowsArgumentException()
    {
        using TempDirectory canonicalRoot = new();
        using TempDirectory outsideRoot = new();

        string linkPath = Path.Combine(canonicalRoot.Path, "escape");
        Directory.CreateSymbolicLink(linkPath, outsideRoot.Path);

        Assert.Throws<ArgumentException>(
            () => SquadWorktreeMcp.SpliceArguments(["--path", "escape"], canonicalRoot.Path));
    }

    /// <summary>
    ///     A <c>--repo-root</c> whose next token is another flag must fail closed instead of
    ///     swallowing that flag as its value.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_WhenRepoRootIsFollowedByAnotherFlag_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => SquadWorktreeMcp.SpliceArguments(
                ["--repo-root", "--path", "x"],
                Path.GetFullPath("/canonical/repo/root")));
    }

    /// <summary>
    ///     A <c>--path</c> whose next token is another flag must fail closed instead of resolving
    ///     the flag name as a relative path and consuming the real flag.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_WhenPathIsFollowedByAnotherFlag_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => SquadWorktreeMcp.SpliceArguments(
                ["--path", "--repo-root", "/repo"],
                Path.GetFullPath("/canonical/repo/root")));
    }

    /// <summary>
    ///     An empty <c>--path</c> value must report the missing value, not a traversal, matching the
    ///     message the sibling <c>--repo-root</c> guard produces.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_WhenPathValueIsEmpty_ReportsMissingValue()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => SquadWorktreeMcp.SpliceArguments(
                ["--path", ""],
                Path.GetFullPath("/canonical/repo/root")));

        Assert.Contains("--path requires a non-empty value", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A symlink that cannot be resolved must be reported as an <see cref="ArgumentException" />,
    ///     not leak the platform's link-resolution exception past the helper's fail-closed contract.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_WhenSymlinkCannotBeResolved_ThrowsArgumentException()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Self-referential symlinks are not created on Windows without developer mode.");

        using TempDirectory canonicalRoot = new();
        string loopPath = Path.Combine(canonicalRoot.Path, "loop");
        Directory.CreateSymbolicLink(loopPath, loopPath);

        Assert.Throws<ArgumentException>(
            () => SquadWorktreeMcp.SpliceArguments(["--path", "loop"], canonicalRoot.Path));
    }

    /// <summary>
    ///     The conductor surfaces must require isolated scratch paths for every tracked MCP
    ///     configuration a worktree setup can rewrite, with restoration as the fallback, so a
    ///     machine-specific absolute path is never committed. The runbook carries the per-client list.
    /// </summary>
    [Theory]
    [InlineData(ConductorAgentPath)]
    [InlineData(ConductorExecutionPath)]
    public void ConductorSurfaces_RequireIsolatedPathsForEveryTrackedMcpConfig(string relativePath)
    {
        string content = ReadRepoFile(relativePath);

        Assert.Contains("isolated per-task scratch paths for every tracked MCP configuration", content, StringComparison.Ordinal);
        Assert.Contains("never rewrite a tracked configuration in place", content, StringComparison.Ordinal);
        Assert.Contains("git checkout -- <path>", content, StringComparison.Ordinal);
        Assert.Contains("MCP runbook enumerates the per-client configuration files", content, StringComparison.Ordinal);
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
