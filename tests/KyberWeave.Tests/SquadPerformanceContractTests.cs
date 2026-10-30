using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Regression contract tests for Kyber-Squad performance and parallelization invariants across
///     canonical agent instructions, conductor references, test-dev skills, and test coding standards.
/// </summary>
public sealed class SquadPerformanceContractTests
{
    private const string ConductorAgentPath = "products/kyber-squad/agents/conductor.md";

    private const string ConductorExecutionPath =
        "products/kyber-squad/agents/conductor/references/execution-and-review.md";

    private const string CSharpDevAgentPath = "products/kyber-squad/agents/csharp-dev.md";
    private const string TestDevAgentPath = "products/kyber-squad/agents/test-dev.md";
    private const string TestDevSkillPath = "products/kyber-squad/skills/test-dev/SKILL.md";
    private const string TestCodingStandardPath = "docs/standards/test/README.md";

    private const string DirectAssemblyRunner =
        "dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class";

    private const string DirectAssemblyDll = "tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class";
    private const string QuietReporter = "-reporter quiet";
    private const string NoBuildFlag = "--no-build";

    private static readonly string[] RequiredDigestFields =
    [
        "STATUS:",
        "FILES:",
        "TESTS:",
        "COMMIT:",
        "REMOTE_HEAD:",
        "TASK_GATE:"
    ];

    private static readonly string[] RequiredDigestTestCounters =
    [
        "total=",
        "failed=",
        "passed="
    ];

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyDirectAssemblyTestRunner(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains(DirectAssemblyRunner, content, StringComparison.Ordinal);
        Assert.Contains(QuietReporter, content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("pre-computed", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("precomputed", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify pre-computed test commands.");
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyBuildOnceTestMany(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("build once", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(NoBuildFlag, content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("council", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("full suite", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("task end", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must reserve full test suite for task end or final council.");
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyZeroStashRedProofs(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("git stash", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("forbid", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("never", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("zero-stash", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("do not stash", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must forbid git stash for RED proofs.");
        Assert.True(
            content.Contains("clean base", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("throwaway", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("worktree", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must mandate running RED proofs against clean base checkout, throwaway copy, or isolated worktree.");
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyExplicitSixFieldWorkerDigestSchema(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        foreach (string field in RequiredDigestFields) Assert.Contains(field, content, StringComparison.Ordinal);

        foreach (string counter in RequiredDigestTestCounters)
            Assert.Contains(counter, content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyInlinedTaskPacketContents(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.True(
            content.Contains("entry-file", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("entry file", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify inlining entry-file table in task packets.");
        Assert.Contains("acceptance criteria", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("pre-computed", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("precomputed", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify pre-computed test commands in task packets.");
        Assert.True(
            content.Contains("out of worker context", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("out of the worker context", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must keep plan/spec files out of worker context.");
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyWorktreeAndArtifactsParallelization(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("worktree", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("--artifacts-path", StringComparison.Ordinal) ||
            content.Contains("artifacts/task-gate-", StringComparison.Ordinal),
            $"{relativePath} must specify per-task artifact paths (--artifacts-path or artifacts/task-gate-).");
        Assert.True(
            content.Contains("branch-head", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("integration", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must serialize final branch-head integration.");
        Assert.Contains("git push", content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyAdversarialRedGreenSeparationAndSameRoleBundling(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("adversarial", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("same-role", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("same role", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify same-role micro-task bundling.");
        Assert.Contains("RED", content, StringComparison.Ordinal);
        Assert.Contains("GREEN", content, StringComparison.Ordinal);
        Assert.Contains("verification", content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestDevSkillPath)]
    public void WorkerSurfacesSpecifyExplicitSixFieldCompletionDigestSchema(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        foreach (string field in RequiredDigestFields) Assert.Contains(field, content, StringComparison.Ordinal);

        foreach (string counter in RequiredDigestTestCounters)
            Assert.Contains(counter, content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestDevSkillPath)]
    public void WorkerSurfacesSpecifyBuildOnceTestManyWithDirectAssemblyRunner(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains(DirectAssemblyDll, content, StringComparison.Ordinal);
        Assert.Contains(QuietReporter, content, StringComparison.Ordinal);
        Assert.Contains(NoBuildFlag, content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestDevSkillPath)]
    public void TestDevSurfacesSpecifyZeroStashRedProofRule(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("git stash", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("forbid", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("never", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("zero-stash", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("do not stash", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must forbid git stash for RED proofs.");
        Assert.True(
            content.Contains("clean base", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("throwaway", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("worktree", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify clean base checkout, throwaway copy, or isolated worktree.");
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestDevSkillPath)]
    public void WorkerSurfacesSpecifyAdversarialRoleBoundaryAndArtifactIsolation(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("adversarial", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("same-role", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("same role", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify same-role task bundling.");
        Assert.Contains("RED", content, StringComparison.Ordinal);
        Assert.Contains("GREEN", content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("artifacts/task-gate-", StringComparison.Ordinal) ||
            content.Contains("--artifacts-path", StringComparison.Ordinal),
            $"{relativePath} must specify per-task artifact isolation.");
    }

    [Fact]
    public void TestCodingStandardDocumentsDirectAssemblyRunnerAndBuildOnceIteration()
    {
        string content = ReadRepoFile(TestCodingStandardPath);
        Assert.Contains(DirectAssemblyRunner, content, StringComparison.Ordinal);
        Assert.Contains(QuietReporter, content, StringComparison.Ordinal);
        Assert.Contains(NoBuildFlag, content, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath)
    {
        string fullPath = Path.Combine(KyberWeaveTestPaths.ToolRoot, relativePath);
        Assert.True(File.Exists(fullPath), $"Expected file does not exist: {relativePath}");
        return File.ReadAllText(fullPath);
    }
}
