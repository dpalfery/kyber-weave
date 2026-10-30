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
    private const string ArchitectureDocPath = "docs/kyber-squad/architecture.md";
    private const string OnboardingDocPath = "docs/kyber-squad/onboarding.md";
    private const string RequirementsDocPath = "docs/kyber-squad/requirements.md";

    private const string CodeReviewSkillPath = "products/kyber-squad/skills/code-review/SKILL.md";
    private const string CodeReviewerAgentPath = "products/kyber-squad/agents/code-reviewer.md";
    private const string ReviewLensAgentPath = "products/kyber-squad/agents/review-lens.md";

    private const string DocumentationAccuracyLensPath =
        "products/kyber-squad/skills/code-review/references/lenses/documentation-accuracy.md";

    private const string TestAdequacyLensPath =
        "products/kyber-squad/skills/code-review/references/lenses/test-adequacy.md";

    private const string ReviewGuidancePath = "REVIEW.md";

    private const string CorrectnessLensPath =
        "products/kyber-squad/skills/code-review/references/lenses/correctness.md";

    private const string SupportabilityLensPath =
        "products/kyber-squad/skills/code-review/references/lenses/supportability.md";

    private const string DirectAssemblyRunner =
        "dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class";

    private const string DirectAssemblyDll = "tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class";
    private const string QuietReporter = "-reporter quiet";
    private const string NoBuildFlag = "--no-build";

    private const string DirectAssemblyRegex =
        @"tests/KyberWeave\.Tests/bin/Release/net\d+\.\d+/KyberWeave\.Tests\.dll\s+-class\s+""?<[^""\r\n]+>?";

    private const string DigestTestCountersRegex =
        @"TESTS:\s*total=[^\r\n]+failed=[^\r\n]+passed=";

    private const string ContiguousSixFieldDigestRegex =
        @"STATUS:[^\r\n]*\r?\n[ \t]*FILES:[^\r\n]*\r?\n[ \t]*TESTS:\s*total=[^\r\n]+failed=[^\r\n]+passed=[^\r\n]*\r?\n[ \t]*COMMIT:\s*<commit-sha\s*\|\s*NONE>[^\r\n]*\r?\n[ \t]*REMOTE_HEAD:\s*<remote-head-sha\s*\|\s*UNPUSHED>[^\r\n]*\r?\n[ \t]*TASK_GATE:";

    private const string BuildOnceReservationRegex =
        @"(?i)\b(?:reserve|reserving|reserved)\b[^\r\n]{0,80}\bfull\b[^\r\n]{0,80}\b(?:task end|council)\b";

    private static readonly string[] RequiredDigestFields =
    [
        "STATUS:",
        "FILES:",
        "TESTS:",
        "COMMIT:",
        "REMOTE_HEAD:",
        "TASK_GATE:"
    ];

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyDirectAssemblyTestRunner(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains(DirectAssemblyRunner, content, StringComparison.Ordinal);
        Assert.Matches(DirectAssemblyRegex, content);
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
        Assert.Matches(BuildOnceReservationRegex, content);
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

        Assert.Contains("COMMIT: <commit-sha | NONE>", content, StringComparison.Ordinal);
        Assert.Contains("REMOTE_HEAD: <remote-head-sha | UNPUSHED>", content, StringComparison.Ordinal);
        Assert.Matches(DigestTestCountersRegex, content);
        Assert.Matches(ContiguousSixFieldDigestRegex, content);
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
    public void ConductorSurfacesPreserveAdversarialRedGreenBoundaryAndBundleSameRoleTasks(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("adversarial", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test-dev", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("same-role", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("same role", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify same-role micro-task bundling.");
        Assert.Contains("independent verification", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("never collapse", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("never collapsing", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify never collapsing RED and GREEN.");
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyDiscoveryHeaderPrecedesDigest(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("Discovery: codegraph N, docs N, direct reads N", content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("precede", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("precedes", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("followed by", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must clarify that Discovery header precedes the 6-field digest.");
    }

    [Fact]
    public void ConductorExecutionContractSpecifiesWorktreeCleanupAndConflictHandling()
    {
        string content = ReadRepoFile(ConductorExecutionPath);
        Assert.Contains("git worktree remove", content, StringComparison.Ordinal);
        Assert.Contains("git worktree prune", content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("conflict", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("rework", StringComparison.OrdinalIgnoreCase),
            $"{ConductorExecutionPath} must specify conflict-reconciliation rework dispatch.");
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestDevSkillPath)]
    public void WorkerSurfacesSpecifyExplicitSixFieldCompletionDigestSchema(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        foreach (string field in RequiredDigestFields) Assert.Contains(field, content, StringComparison.Ordinal);

        Assert.Contains("COMMIT: <commit-sha | NONE>", content, StringComparison.Ordinal);
        Assert.Contains("REMOTE_HEAD: <remote-head-sha | UNPUSHED>", content, StringComparison.Ordinal);
        Assert.Matches(DigestTestCountersRegex, content);
        Assert.Matches(ContiguousSixFieldDigestRegex, content);
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestDevSkillPath)]
    public void WorkerSurfacesSpecifyBuildOnceTestManyWithDirectAssemblyRunner(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains(DirectAssemblyDll, content, StringComparison.Ordinal);
        Assert.Matches(DirectAssemblyRegex, content);
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
    public void SpecialistSurfacesPreserveAdversarialRedGreenAndForbidCollapsedRedGreen(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("adversarial", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test-dev", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("same-role", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("same role", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify same-role task bundling.");
        Assert.Contains("independent verification", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("never collapse", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("never collapsing", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify never collapsing RED and GREEN.");
        Assert.True(
            content.Contains("artifacts/task-gate-", StringComparison.Ordinal) ||
            content.Contains("--artifacts-path", StringComparison.Ordinal),
            $"{relativePath} must specify per-task artifact isolation.");
    }

    [Fact]
    public void CSharpDevAgentPreservesGreenVerificationAgainstIndependentRedContract()
    {
        string content = ReadRepoFile(CSharpDevAgentPath);
        Assert.DoesNotContain("never verify implementation without independent RED test verification", content,
            StringComparison.Ordinal);
        Assert.Contains("verify GREEN against the independent RED contract authored by test-dev", content,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestCodingStandardDocumentsDirectAssemblyRunnerAndBuildOnceIteration()
    {
        string content = ReadRepoFile(TestCodingStandardPath);
        Assert.Contains(DirectAssemblyRunner, content, StringComparison.Ordinal);
        Assert.Matches(DirectAssemblyRegex, content);
        Assert.Contains(QuietReporter, content, StringComparison.Ordinal);
        Assert.Contains(NoBuildFlag, content, StringComparison.Ordinal);
        Assert.Matches(BuildOnceReservationRegex, content);
    }

    [Theory]
    [InlineData(OnboardingDocPath)]
    [InlineData(RequirementsDocPath)]
    public void DocumentationSpecifiesSixFieldDigestRejectionRule(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("STATUS: FAIL", content, StringComparison.Ordinal);
        Assert.Contains("TASK_GATE:", content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("reject", StringComparison.OrdinalIgnoreCase) &&
            (content.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("required field", StringComparison.OrdinalIgnoreCase)),
            $"{relativePath} must specify that conductor rejects digests when any required field is missing.");
        Assert.True(
            content.Contains("STATUS: FAIL", StringComparison.Ordinal) &&
            content.Contains("TASK_GATE:", StringComparison.Ordinal),
            $"{relativePath} must specify that conductor rejects digests when STATUS: FAIL is reported or TASK_GATE: is missing or FAIL.");
    }

    [Fact]
    public void ArchitectureDocumentationRendererTablePreservesAgentNamePrecision()
    {
        string content = ReadRepoFile(ArchitectureDocPath);
        Assert.Contains(".opencode/agents/<name>.md", content, StringComparison.Ordinal);
        Assert.Contains(".kilo/agents/<name>.md", content, StringComparison.Ordinal);
        Assert.DoesNotContain(".opencode/agents/*.md", content, StringComparison.Ordinal);
        Assert.DoesNotContain(".kilo/agents/*.md", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    [InlineData(ArchitectureDocPath)]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestDevSkillPath)]
    public void SurfacesConsistentlySpecifyArtifactIsolationFlags(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("dotnet build --artifacts-path .agent_scratchpad/<task-id>/artifacts", content,
            StringComparison.Ordinal);
        Assert.True(
            content.Contains("artifacts/task-gate-<task-id>.json", StringComparison.Ordinal) ||
            content.Contains("--out artifacts/task-gate-<task-id>.json", StringComparison.Ordinal),
            $"{relativePath} must specify review-gates output path artifacts/task-gate-<task-id>.json.");
        Assert.True(
            content.Contains("review gates", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("kyber-weave review gates", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must explicitly distinguish review gates invocation.");
    }

    [Theory]
    [InlineData(CodeReviewSkillPath)]
    [InlineData(CodeReviewerAgentPath)]
    [InlineData(ReviewLensAgentPath)]
    public void CouncilSurfacesSpecifyMaterializedDiffContract(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("artifacts/review-diff.patch", content, StringComparison.Ordinal);
        if (relativePath == ReviewLensAgentPath)
        {
            Assert.True(
                content.Contains("view_file", StringComparison.Ordinal) ||
                content.Contains("filesystem.read: allow", StringComparison.Ordinal) ||
                content.Contains("filesystem.read", StringComparison.Ordinal),
                $"{relativePath} must specify reading diff via view_file / filesystem.read: allow.");
            Assert.True(
                content.Contains("without executing git", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("no git", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("never execute git", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("do not execute git", StringComparison.OrdinalIgnoreCase),
                $"{relativePath} must forbid executing git commands.");
        }
        else
        {
            Assert.True(
                content.Contains("materialize", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("materializing", StringComparison.OrdinalIgnoreCase),
                $"{relativePath} must specify materializing unified diff.");
            Assert.True(
                content.Contains("fan-out", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("fanout", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("fan out", StringComparison.OrdinalIgnoreCase),
                $"{relativePath} must specify materialization before lens fan-out.");
            Assert.Contains("review-lens", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DocumentationAccuracyLensChartersCrossDocumentContractParity()
    {
        string content = ReadRepoFile(DocumentationAccuracyLensPath);
        Assert.True(
            content.Contains("cross-document", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("prompt-to-prompt", StringComparison.OrdinalIgnoreCase),
            $"{DocumentationAccuracyLensPath} must charter cross-document / prompt-to-prompt parity.");
        Assert.Contains("parity", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("schema", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("rejection", StringComparison.OrdinalIgnoreCase),
            $"{DocumentationAccuracyLensPath} must charter parity across schemas and rejection rules.");
        Assert.True(
            content.Contains("agree across modified documents", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("agree across modified", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("not only doc-vs-code", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("not only code", StringComparison.OrdinalIgnoreCase),
            $"{DocumentationAccuracyLensPath} must mandate agreement across modified documents, not only doc-vs-code.");
    }

    [Theory]
    [InlineData(TestAdequacyLensPath)]
    [InlineData(ReviewGuidancePath)]
    public void TestAdequacyAndReviewGuidanceEnforceContractAssertionRigor(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("Assert.Contains", content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("single-word", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("single word", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("bare", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must flag bare single-word Assert.Contains checks.");
        Assert.True(
            content.Contains("||", StringComparison.Ordinal) &&
            (content.Contains("unrelated text", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("unguarded", StringComparison.OrdinalIgnoreCase)),
            $"{relativePath} must flag unguarded boolean || assertions.");
        Assert.True(
            content.Contains("foreach", StringComparison.OrdinalIgnoreCase) &&
            (content.Contains("non-contiguous", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("contiguous", StringComparison.OrdinalIgnoreCase)),
            $"{relativePath} must flag non-contiguous foreach field checks over multi-line schemas.");
    }

    [Theory]
    [InlineData(CorrectnessLensPath)]
    [InlineData(SupportabilityLensPath)]
    public void CorrectnessAndSupportabilityLensesRequireOperationalLifecycleWalk(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("lifecycle", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("create", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("cleanup", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must specify walking the lifecycle from create to cleanup.");
        Assert.True(
            content.Contains("rollback", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("conflict", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must include conflict/rollback in the lifecycle walk.");
        Assert.True(
            content.Contains("worktree", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("concurrency", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("lock", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("temp-path", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must apply lifecycle walk to concurrency, worktree, lock, or temp-path primitives.");
    }

    [Theory]
    [InlineData(CorrectnessLensPath)]
    [InlineData(ReviewGuidancePath)]
    public void CorrectnessLensAndReviewGuidanceRequireLiteralPromptParsing(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.True(
            content.Contains("charitable reading", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("charitable", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must prohibit charitable reading of agent prompt instructions.");
        Assert.True(
            content.Contains("literal parsing", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("literal", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must mandate literal parsing of prompts.");
        Assert.True(
            content.Contains("deadlock", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("self-contradiction", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("contradiction", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must flag gate deadlocks or self-contradictions in prompts.");
    }

    [Theory]
    [InlineData(CodeReviewSkillPath)]
    [InlineData(CodeReviewerAgentPath)]
    public void CodeReviewSurfacesEnforceRefutationGuardrails(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("refut", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("R14", content, StringComparison.Ordinal);
        Assert.Contains("R16", content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("Ground Rule 1", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("concrete failure", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must reference Ground Rule 1 / concrete failure guardrail.");
        Assert.True(
            content.Contains("repo-idiom", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("idiom", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must forbid dismissing findings under repo-idiom excuses.");
        Assert.True(
            content.Contains("cleanup", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("lifecycle", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must protect missing lifecycle cleanup findings from refutation dismissal.");
    }

    private static string ReadRepoFile(string relativePath)
    {
        string fullPath = Path.Combine(KyberWeaveTestPaths.ToolRoot, relativePath);
        try
        {
            return File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Fail($"Failed to read expected repository file '{relativePath}' at '{fullPath}': {ex.Message}");
            return string.Empty;
        }
    }
}
