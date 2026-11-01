using System.Text.RegularExpressions;
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
    private const string ReviewTriageAgentPath = "products/kyber-squad/agents/review-triage.md";
    private const string ReviewCouncilArchitecturePath = "docs/code-review/architecture.md";

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
        @"tests/KyberWeave\.Tests/bin/Release/net\d+\.\d+/KyberWeave\.Tests\.dll\s+-class\s+(?:""(?:<[^"">\r\n]+>|[^""\s\r\n]+)""|<[^"">\s\r\n]+>|[^""\s\r\n]+)\s+-reporter\s+quiet";

    private const string DigestTestCountersRegex =
        @"TESTS:\s*total=(?:\d+|N|<[^>\r\n]+>)\s+failed=(?:\d+|N|<[^>\r\n]+>)\s+passed=(?:\d+|N|<[^>\r\n]+>)";

    // Why this exists: when "task end" counted as a full-suite moment, every worker that finished
    // ran the whole suite, and harnesses finish workers together, so several full runs landed on
    // one machine at once. The span stops at a full stop so a task-end clause in the next
    // sentence ("At task end a worker runs only ...") is not mistaken for the reservation.
    private const string TaskEndFullSuiteReservationRegex =
        @"(?i)\b(?:reserve|reserved|reserving)\b[^.\r\n]*\bfull\b[^.\r\n]*\btask (?:end|exit gates?)\b";

    private const string ContiguousSixFieldDigestRegex =
        @"STATUS:[^\r\n]*\r?\n[ \t]*FILES:[^\r\n]*\r?\n[ \t]*TESTS:\s*total=(?:\d+|N|<[^>\r\n]+>)\s+failed=(?:\d+|N|<[^>\r\n]+>)\s+passed=(?:\d+|N|<[^>\r\n]+>)[^\r\n]*\r?\n[ \t]*COMMIT:\s*<commit-sha\s*\|\s*NONE>[^\r\n]*\r?\n[ \t]*REMOTE_HEAD:\s*<remote-head-sha\s*\|\s*UNPUSHED>[^\r\n]*\r?\n[ \t]*TASK_GATE:";

    private static readonly Regex CouncilDiffCleanupRegex = new(
        @"(?im)^(?=[^\r\n]*\bcode-reviewer\b)(?=[^\r\n]*\b(?:remov\w+|clean\w*|delet\w+)\b)(?=[^\r\n]*\b(?:after\s+(?:the\s+)?(?:review\s+)?(?:council|run|completion)|following\s+(?:the\s+)?(?:review\s+)?(?:council|run|completion)|upon completion|post-council|post-run)\b)[^\r\n]+",
        RegexOptions.Compiled);

    private static readonly string[] RequiredDigestFields =
    [
        "STATUS:",
        "FILES:",
        "TESTS:",
        "COMMIT:",
        "REMOTE_HEAD:",
        "TASK_GATE:"
    ];

    internal static bool HasBuildOnceReservationClause(string content)
    {
        string[] lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        foreach (string line in lines)
            if (Regex.IsMatch(line, @"\b(?:reserve|reserved|reserving)\b", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(line, @"\bfull\b", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(line, @"\b(?:task end|council)\b", RegexOptions.IgnoreCase))
                return true;

        return false;
    }

    [Theory]
    [InlineData("reserve full suite for task end", true)]
    [InlineData("full suite runs are reserved for task end", true)]
    [InlineData("reserving full test suite execution for task end or council", true)]
    [InlineData("council meeting and full menu", false)]
    [InlineData("reserve full suite\nfor task end", false)]
    public void BuildOnceReservationHelperValidatesOrderIndependenceAndProximity(string input, bool expected)
    {
        Assert.Equal(expected, HasBuildOnceReservationClause(input));
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyDirectAssemblyTestRunner(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains(DirectAssemblyRunner, content, StringComparison.Ordinal);
        Assert.Matches(DirectAssemblyRegex, content);
        Assert.Contains(QuietReporter, content, StringComparison.Ordinal);
        Assert.Matches(@"(?i)\bpre-?computed\s+test\s+commands?\b", content);
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyBuildOnceTestMany(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)\bbuild\s+once,\s+test\s+many\b", content);
        Assert.Contains(NoBuildFlag, content, StringComparison.Ordinal);
        Assert.True(
            HasBuildOnceReservationClause(content),
            $"{relativePath} must specify reserving full test suite execution for task end or council.");
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyZeroStashRedProofs(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?i)\b(?:forbid(?:s|ding)?\s+(?:`?git\s+stash`?|using\s+git\s+stash)|zero-stash\s+RED\s+proofs?|never\s+use\s+`?git\s+stash`?)\b",
            content);
        Assert.Matches(
            @"(?i)\b(?:clean\s+base\s+checkout|throwaway\s+(?:copy|working\s+tree)|isolated\s+worktree)\b",
            content);
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
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
        Assert.Matches(@"(?i)\binlined\s+entry-?file\s+table\b", content);
        Assert.Contains("acceptance criteria", content, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"(?i)\bpre-?computed\s+test\s+commands?\b", content);
        Assert.Matches(@"(?i)\bout\s+of\s+(?:the\s+)?worker\s+context\b", content);
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyWorktreeAndArtifactsParallelization(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)\b(?:git\s+worktree\s+checkouts?|separate\s+git\s+worktree\s+checkouts?)\b", content);
        Assert.Contains("--artifacts-path .agent_scratchpad/<task-id>/artifacts", content, StringComparison.Ordinal);
        Assert.Contains("--out artifacts/task-gate-<task-id>.json", content, StringComparison.Ordinal);
        Assert.Matches(@"(?i)\bserialize\s+final\s+branch-head\s+integration\b", content);
        Assert.Matches(@"(?i)\bbefore\s+git\s+push\b", content);
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesPreserveAdversarialRedGreenBoundaryAndBundleSameRoleTasks(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?i)\badversarial\s+separation\s+between\s+RED\s+test\s+authorship[^\r\n]*and\s+GREEN\s+implementation\b",
            content);
        Assert.Matches(@"(?i)\b(?:bundle\s+)?same-?role\s+micro-?tasks?\b", content);
        Assert.Matches(@"(?i)\bnever\s+collapse\s+RED\s+and\s+GREEN\b", content);
        Assert.Matches(@"(?i)\bindependent\s+verification\s+(?:pass\s+)?at\s+task\s+completion\b", content);
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyDiscoveryHeaderPrecedesDigest(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("Discovery: codegraph N, docs N, direct reads N", content, StringComparison.Ordinal);
        Assert.Matches(@"(?i)\bprecedes?\s+(?:the\s+)?(?:six|6)-field\s+(?:completion\s+)?digest\b", content);
    }

    [Theory]
    [InlineData(ConductorAgentPath)]
    [InlineData(ConductorExecutionPath)]
    [InlineData(RequirementsDocPath)]
    public void ConductorAndRequirementsExemptCircuitBreakerEscalationFromSixFieldRejection(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*\b(?:exempt\w*|bypass\w*)\b)(?=[^\r\n]*\bSTATUS:\s*ESCALATION\b)[^\r\n]+",
            content);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*\barchitect\s+mediation\b)(?=[^\r\n]*\b(?:without\s+rejection|never\s+reject|without\s+rejecting)\b)[^\r\n]+",
            content);
    }

    [Fact]
    public void ConductorExecutionContractSpecifiesWorktreeCleanupAndConflictHandling()
    {
        string content = ReadRepoFile(ConductorExecutionPath);
        Assert.Matches(@"(?i)\bgit\s+(?:cherry-pick|merge)\s+--abort\b", content);
        Assert.Contains("git worktree remove --force", content, StringComparison.Ordinal);
        Assert.Contains("git worktree prune", content, StringComparison.Ordinal);
        Assert.Matches(
            @"(?i)\b(?:git\s+worktree\s+remove\s+--force[^\r\n]*(?:all\s+exit\s+paths|integration,\s+failure,\s+escalation,\s+or\s+cancellation|all\s+outcomes)|guarantee[^\r\n]*worktree\s+cleanup\s+across\s+all\s+outcomes)\b",
            content);
        Assert.Matches(@"(?i)\bconflict-reconciliation\s+rework\s+dispatch\b", content);
    }

    /// <summary>
    /// Conductor is a non-developer orchestrator; its frontmatter description and body must never define
    /// or emit a TASK_GATE digest. The canonical six-field worker completion digest and full 5-state TASK_GATE
    /// enum are defined in the execution-and-review contract reference.
    /// </summary>
    [Fact]
    public void ExecutionAndReviewReference_DeclaresDeterministicTaskGate_AndConductorOffloadsIt()
    {
        string content = ReadRepoFile(ConductorAgentPath);
        int firstMarker = content.IndexOf("---", StringComparison.Ordinal);
        int secondMarker = content.IndexOf("---", firstMarker + 3, StringComparison.Ordinal);
        Assert.True(firstMarker >= 0 && secondMarker > firstMarker,
            $"{ConductorAgentPath} must contain YAML frontmatter.");
        string frontmatter = content.Substring(firstMarker, secondMarker - firstMarker);
        string body = content.Substring(secondMarker + 3);

        Assert.DoesNotContain("FILES:", frontmatter, StringComparison.Ordinal);
        Assert.DoesNotContain("TESTS:", frontmatter, StringComparison.Ordinal);
        Assert.DoesNotContain("COMMIT:", frontmatter, StringComparison.Ordinal);
        Assert.DoesNotContain("REMOTE_HEAD:", frontmatter, StringComparison.Ordinal);
        Assert.DoesNotContain("TASK_GATE:", frontmatter, StringComparison.Ordinal);

        Assert.DoesNotContain("TASK_GATE:", body, StringComparison.Ordinal);

        string executionContent = ReadRepoFile(ConductorExecutionPath);
        Assert.Matches(ContiguousSixFieldDigestRegex, executionContent);
        Assert.Contains("PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE", executionContent,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConductorAgentPath)]
    [InlineData(ConductorExecutionPath)]
    public void ConductorSurfacesScopeSixFieldRejectionToMigratedSpecialistsDuringPhasedRollout(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*\b(?:phased\s+rollout|migrated\s+(?:specialist\s+)?agents?)\b)(?=[^\r\n]*\bcsharp-dev\b)(?=[^\r\n]*\btest-dev\b)[^\r\n]+",
            content);
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
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    public void SpecialistSurfacesClarifyWorktreeOutputIsolationBypassesArtifactsPath(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*\bworktrees?\b)(?=[^\r\n]*\binherently\s+isolate[^\r\n]*output)[^\r\n]+",
            content);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*(?<![\w-])--artifacts-path\b)(?=[^\r\n]*\b(?:not\s+required|optional|omitted)\b)(?=[^\r\n]*\bworktree\b)[^\r\n]+",
            content);
    }

    [Theory]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestDevSkillPath)]
    public void TestDevSurfacesSpecifyZeroStashRedProofRule(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?i)\b(?:forbid(?:s|ding)?\s+(?:`?git\s+stash`?|using\s+git\s+stash)|zero-stash\s+RED\s+proof(?:s|\s+rule)|never\s+(?:use|run)\s+`?git\s+stash`?)\b",
            content);
        Assert.Matches(
            @"(?i)\b(?:clean\s+base\s+checkout|throwaway\s+(?:copy|working\s+tree)|isolated\s+worktree)\b",
            content);
    }

    [Fact]
    public void TestDevSkillQualifiesDirectAssemblyRunnerWithLanguageSpecificTargeting()
    {
        string content = ReadRepoFile(TestDevSkillPath);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*\b(?:dotnet\s+tests/KyberWeave\.Tests|direct\s+assembly\s+runner)\b)(?=[^\r\n]*\b(?:\.NET\s+solutions?|\.NET\s+projects?|language-specific)\b)[^\r\n]+",
            content);
    }

    [Fact]
    public void TestDevSkillSpecifiesDiscoveryHeaderPrecedingDigestSchema()
    {
        string content = ReadRepoFile(TestDevSkillPath);
        Assert.Contains("Discovery: codegraph N, docs N, direct reads N", content, StringComparison.Ordinal);
        Assert.Matches(
            @"(?i)\b(?:precedes?(?:\s+the)?\s+(?:six|6)-field\s+(?:completion\s+)?digest|Discovery:\s*codegraph[^\r\n]*\r?\n[ \t]*STATUS:)\b",
            content);
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestDevSkillPath)]
    public void SpecialistSurfacesPreserveAdversarialRedGreenAndForbidCollapsedRedGreen(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)\badversarial\s+(?:separation|role\s+boundary)\b", content);
        Assert.Matches(@"(?i)\b(?:bundle\s+)?same-?role\s+(?:micro-?tasks?|task\s+bundling)\b", content);
        Assert.Matches(@"(?i)\bnever\s+collapse\s+RED\s+and\s+GREEN\b", content);
        Assert.Matches(@"(?i)\bindependent\s+verification\b", content);
        Assert.Contains("--artifacts-path", content, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    public void SpecialistPromptsClarifyArtifactAndBaselinePathsBelongInAccompanyingMessageBody(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?i)\b(?:explanatory\s+message\s+body|message\s+body)\s+accompanying\s+(?:the\s+|your\s+)?(?:completion\s+)?digest\b",
            content);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*\bartifacts?\s+paths?\b)(?=[^\r\n]*\b(?:explanatory\s+message\s+body|accompanying\s+(?:the\s+|your\s+)?(?:completion\s+)?digest)\b)[^\r\n]+",
            content);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*\bbaseline\b)(?=[^\r\n]*\b(?:explanatory\s+message\s+body|accompanying\s+(?:the\s+|your\s+)?(?:completion\s+)?digest)\b)[^\r\n]+",
            content);
        Assert.Matches(
            @"(?i)\bpreserv(?:e|ing)\s+(?:the\s+)?(?:rigid\s+)?(?:6|six)-field\s+(?:completion\s+)?digest\s+schema\b",
            content);
    }

    [Fact]
    public void TestDevPromptClarifiesCoverageGapsBelongInAccompanyingMessageBody()
    {
        string content = ReadRepoFile(TestDevAgentPath);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*\bCOVERAGE_GAPS\b)(?=[^\r\n]*\b(?:explanatory\s+message\s+body|accompanying\s+(?:the\s+|your\s+)?(?:completion\s+)?digest)\b)[^\r\n]+",
            content);
        Assert.Matches(
            @"(?i)\bpreserv(?:e|ing)\s+(?:the\s+)?(?:rigid\s+)?(?:6|six)-field\s+(?:completion\s+)?digest\s+schema\b",
            content);
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    public void SpecialistPromptsDoNotContainMalformedArtifactsPathOptionTypo(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.DoesNotMatch(@"(?:\w-+|(?<![-\w])-|---)artifacts-path", content);
    }

    [Fact]
    public void TestCodingStandardDocumentsDirectAssemblyRunnerAndBuildOnceIteration()
    {
        string content = ReadRepoFile(TestCodingStandardPath);
        Assert.Contains(DirectAssemblyRunner, content, StringComparison.Ordinal);
        Assert.Matches(DirectAssemblyRegex, content);
        Assert.Contains(QuietReporter, content, StringComparison.Ordinal);
        Assert.Contains(NoBuildFlag, content, StringComparison.Ordinal);
        Assert.True(
            HasBuildOnceReservationClause(content),
            $"{TestCodingStandardPath} must specify reserving full test suite execution for task end or council.");
    }

    [Theory]
    [InlineData(ConductorExecutionPath)]
    [InlineData(ConductorAgentPath)]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestCodingStandardPath)]
    [InlineData(OnboardingDocPath)]
    [InlineData(RequirementsDocPath)]
    public void TaskEndRunsTouchedTestClassesAndLeavesTheFullSuiteToTheCouncil(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)\bonly the test classes (?:its|your|the) task touched\b", content);
        Assert.DoesNotMatch(TaskEndFullSuiteReservationRegex, content);
    }

    [Theory]
    [InlineData(OnboardingDocPath)]
    [InlineData(RequirementsDocPath)]
    public void DocumentationSpecifiesSixFieldDigestRejectionRule(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?i)\breject[^\r\n]*(?:any\s+required\s+field\s+is\s+missing|missing[^\r\n]*required\s+field)\b",
            content);
        Assert.Matches(
            @"(?i)\breject[^\r\n]*(?:STATUS:\s*FAIL|TASK_GATE:[^\r\n]*FAIL)\b",
            content);
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
        Assert.Contains("--out artifacts/task-gate-<task-id>.json", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave review gates", content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CodeReviewSkillPath)]
    [InlineData(CodeReviewerAgentPath)]
    [InlineData(ReviewLensAgentPath)]
    [InlineData(ReviewTriageAgentPath)]
    [InlineData(ReviewCouncilArchitecturePath)]
    public void CouncilSurfacesSpecifyMaterializedDiffContract(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains(".agent_scratchpad/<task-id>/review-diff.patch", content, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts/review-diff.patch", content, StringComparison.Ordinal);
        Assert.Matches(CouncilDiffCleanupRegex, content);

        if (relativePath is CodeReviewSkillPath or CodeReviewerAgentPath)
            Assert.Matches(
                @"(?im)^(?=[^\r\n]*(?:\b|(?<=[\s`]))\.agent_scratchpad/<task-id>/review-diff\.patch\b)(?=[^\r\n]*\breview-lens\b)(?=[^\r\n]*\breview-triage\b)[^\r\n]+",
                content);

        if (relativePath is ReviewLensAgentPath or ReviewTriageAgentPath)
        {
            Assert.Matches(@"(?i)\b(?:view_file|filesystem\.read)\b", content);
            Assert.Matches(@"(?i)\b(?:never|not|without)\s+(?:execut\w+|run\w*)\s+git\b", content);
        }
        else
        {
            Assert.Matches(@"(?i)\bmaterializ(?:es?|ing)\s+(?:`?git\s+diff`?|(?:the\s+)?(?:unified\s+)?diff)\b",
                content);
            Assert.Matches(@"(?i)\bbefore\s+(?:council\s+|lens\s+)?fan(?:-|\s+)?out\b", content);
        }
    }

    [Fact]
    public void ReviewLensAgentInstructsReadingSuppliedDiffPathFromInvocationPacket()
    {
        string content = ReadRepoFile(ReviewLensAgentPath);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*\bread\s+(?:the\s+)?materialized\s+diff\b)(?=[^\r\n]*\b(?:path\s+supplied\s+in\s+(?:your\s+)?(?:task|invocation)\s+packet|supplied\s+in\s+(?:your\s+)?(?:task|invocation)\s+packet)\b)[^\r\n]+",
            content);
    }

    [Fact]
    public void DocumentationAccuracyLensChartersCrossDocumentContractParity()
    {
        string content = ReadRepoFile(DocumentationAccuracyLensPath);
        Assert.Matches(@"(?i)\b(?:cross-document|prompt-to-prompt)\s+(?:contract\s+)?parity\b", content);
        Assert.Matches(@"(?i)\b(?:invariants,\s+)?schemas,\s+and\s+rejection\s+rules\b", content);
        Assert.Matches(@"(?i)\bagree\s+across\s+modified\s+documents,\s+not\s+only\s+doc-vs-code\b", content);
    }

    [Theory]
    [InlineData(TestAdequacyLensPath)]
    [InlineData(ReviewGuidancePath)]
    public void TestAdequacyAndReviewGuidanceEnforceContractAssertionRigor(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)\bbare\s+single-word\s+`?Assert\.Contains`?\b", content);
        Assert.Matches(@"(?i)\bunguarded\s+boolean\s+`?\|\|`?\s+assertions?\b", content);
        Assert.Matches(@"(?i)\bnon-contiguous\s+`?foreach`?\s+field\s+checks\b", content);
    }

    [Theory]
    [InlineData(CorrectnessLensPath)]
    [InlineData(SupportabilityLensPath)]
    public void CorrectnessAndSupportabilityLensesRequireOperationalLifecycleWalk(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)\boperational\s+lifecycle\s+(?:state-machine\s+)?walk\b", content);
        Assert.Matches(@"(?i)`create`\s*→\s*`execute`\s*→\s*`integrate`\s*→\s*`cleanup`", content);
        Assert.Matches(@"(?i)(?:`?conflict`?\s+and\s+`?rollback`?|conflict/rollback)", content);
        Assert.Matches(@"(?i)\b(?:concurrency\s+primitives?|worktree\s+allocations?|locks?|temp-paths?)\b", content);
    }

    [Theory]
    [InlineData(CorrectnessLensPath)]
    [InlineData(ReviewGuidancePath)]
    public void CorrectnessLensAndReviewGuidanceRequireLiteralPromptParsing(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?i)\b(?:prohibit\s+charitable\s+reading\s+of\s+prompts|prohibit\s+charitable\s+reading\s+of\s+agent\s+prompt\s+instructions)\b",
            content);
        Assert.Matches(
            @"(?i)\b(?:mandate\s+literal\s+parsing\s+of\s+instructions|require\s+literal\s+parsing\s+of\s+prompt\s+instructions)\b",
            content);
        Assert.Matches(@"(?i)\b(?:gate\s+deadlock|self-contradiction|contradiction/self-contradiction)\b", content);
    }

    [Theory]
    [InlineData(CodeReviewSkillPath)]
    [InlineData(CodeReviewerAgentPath)]
    public void CodeReviewSurfacesEnforceRefutationGuardrails(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)\brefutation\s+guardrails\b", content);
        Assert.Contains("R14", content, StringComparison.Ordinal);
        Assert.Contains("R16", content, StringComparison.Ordinal);
        Assert.Matches(@"(?i)\bGround\s+Rule\s+1\b", content);
        Assert.Matches(@"(?i)\b(?:'repo-idiom'|repo-idiom)\b", content);
        Assert.Matches(@"(?i)\bmissing\s+operational\s+lifecycle\s+cleanup\s+paths?\b", content);
    }

    [Fact]
    public void CodeReviewSkillDoesNotContainMalformedScratchpadPathTypo()
    {
        string content = ReadRepoFile(CodeReviewSkillPath);
        Assert.DoesNotContain("path`.agent_scratchpad", content, StringComparison.Ordinal);
        Assert.DoesNotContain("path.agent_scratchpad", content, StringComparison.Ordinal);
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
