using System.Reflection;
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

    private const string ConductorExecutionPath =
        "products/kyber-squad/agents/conductor/references/execution-and-review.md";

    private const string ArchitectureDocPath = "docs/kyber-squad/architecture.md";
    private const string McpRunbookDocPath = "docs/docgraph/mcp-runbook.md";

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
    ///     Verifies that worktree configuration helpers produce absolute paths for <c>--repo-root</c>
    ///     and <c>--path</c>.
    /// </summary>
    [Fact]
    public void WorktreeMcpHelper_SplicesRepoRootAndPathToAbsoluteCanonicalPaths()
    {
        Type? helperType = Type.GetType("KyberWeave.Core.Squad.Deployment.SquadWorktreeMcp, KyberWeave.Core")
                           ?? Type.GetType(
                               "KyberWeave.Core.Squad.Deployment.SquadWorktreeMcpConfiguration, KyberWeave.Core");

        Assert.True(
            helperType is not null,
            "Worktree MCP helper 'KyberWeave.Core.Squad.Deployment.SquadWorktreeMcp' must be implemented in KyberWeave.Core.");

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
}
