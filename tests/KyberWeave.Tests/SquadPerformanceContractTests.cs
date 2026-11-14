using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Regression contract tests for Kyber-Squad performance and parallelization invariants across
///     canonical agent instructions, the shared execution skill both entry points load, test-dev
///     skills, and test coding standards: the command tokens, flags, paths and digest templates. The
///     rules those surfaces state in prose are checked by
///     <c>tests/KyberWeave.Tests/Fixtures/prose-rules/catalog.yml</c>.
/// </summary>
public sealed class SquadPerformanceContractTests
{
    private const string ConductorAgentPath = "products/kyber-squad/agents/conductor.md";

    /// <summary>
    ///     The shared worker execution contract: one canonical skill that the conductor and the
    ///     bug-crusher workflow both load. It used to be a conductor reference, so the conductor's
    ///     surfaces below are pinned against the skill wherever the rule belongs to the contract
    ///     rather than to the conductor's own body.
    /// </summary>
    private const string SharedExecutionSkillPath =
        "products/kyber-squad/skills/delivery-execution/SKILL.md";

    private const string CSharpDevAgentPath = "products/kyber-squad/agents/csharp-dev.md";
    private const string TestDevAgentPath = "products/kyber-squad/agents/test-dev.md";

    /// <summary>
    ///     The skill a worker loads when it finishes. Report formats — the completion digest and its
    ///     fields — belong here rather than in each agent body (the rule map's "Report formats" row),
    ///     so the pins below read this file instead of the per-agent copies.
    /// </summary>
    private const string TaskGateSkillPath = "products/kyber-squad/skills/task-gate/SKILL.md";

    private const string TestCodingStandardPath = "docs/standards/test/README.md";
    private const string ArchitectureDocPath = "docs/kyber-squad/architecture.md";
    private const string OnboardingDocPath = "docs/kyber-squad/onboarding.md";
    private const string RequirementsDocPath = "docs/kyber-squad/requirements.md";

    private const string CodeReviewSkillPath = "products/kyber-squad/skills/code-review/SKILL.md";
    private const string CodeReviewerAgentPath = "products/kyber-squad/agents/code-reviewer.md";
    private const string ReviewLensAgentPath = "products/kyber-squad/agents/review-lens.md";
    private const string ReviewTriageAgentPath = "products/kyber-squad/agents/review-triage.md";
    private const string ReviewCouncilArchitecturePath = "docs/code-review/architecture.md";

    private const string DirectAssemblyRunner =
        "dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class";

    /// <summary>
    ///     Retired. The quiet reporter prints the same one banner line for a passing run and for a
    ///     filter that matched nothing, so a worker can neither check the total nor fill in its
    ///     <c>TESTS:</c> digest field from the output.
    /// </summary>
    private const string QuietReporter = "-reporter quiet";

    private const string NoBuildFlag = "--no-build";

    /// <summary>
    ///     The class filter has to show the namespace: xUnit matches <c>-class</c> against the
    ///     fully qualified name, so a short class name runs zero tests and still exits 0.
    /// </summary>
    private const string DirectAssemblyRegex =
        @"tests/KyberWeave\.Tests/bin/Release/net\d+\.\d+/KyberWeave\.Tests\.dll\s+-class\s+""<[Nn]amespace>\.<[^"">\r\n]+>""";

    private const string DigestTestCountersRegex =
        @"TESTS:\s*total=(?:\d+|N|<[^>\r\n]+>)\s+failed=(?:\d+|N|<[^>\r\n]+>)\s+passed=(?:\d+|N|<[^>\r\n]+>)";

    private const string ContiguousSixFieldDigestRegex =
        @"STATUS:[^\r\n]*\r?\n[ \t]*FILES:[^\r\n]*\r?\n[ \t]*TESTS:\s*total=(?:\d+|N|<[^>\r\n]+>)\s+failed=(?:\d+|N|<[^>\r\n]+>)\s+passed=(?:\d+|N|<[^>\r\n]+>)[^\r\n]*\r?\n[ \t]*COMMIT:\s*<commit-sha\s*\|\s*NONE>[^\r\n]*\r?\n[ \t]*REMOTE_HEAD:\s*<remote-head-sha\s*\|\s*UNPUSHED>[^\r\n]*\r?\n[ \t]*TASK_GATE:";

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
    [InlineData(SharedExecutionSkillPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyDirectAssemblyTestRunner(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        AssertNamesTheHostTestStandardForTheRunner(content, relativePath);
        Assert.DoesNotContain(QuietReporter, content, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Asserts the exact runner is reachable from a portable file, without the portable file
    ///     naming this repository's own assembly path.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>products/kyber-squad/</c> ships to every consumer, so a line naming
    ///         <c>tests/KyberWeave.Tests/bin/…</c> teaches every downstream repository a fact about
    ///         this one, and that is what slice 7's D5 forbids. The rule the earlier pin enforced is
    ///         unchanged — the worker is still told the exact runner, and still told the class must be
    ///         fully qualified — and only its home moved: the host's test standard carries the
    ///         concrete path in its <c>## Completion</c> section, and the portable file points there.
    ///     </para>
    ///     <para>
    ///         Both halves are asserted, because either alone is satisfiable by a wrong tree: a pointer
    ///         with no literal behind it sends the worker nowhere, and a literal with no pointer is
    ///         never reached. That the portable file names the test standard as the runner's home is
    ///         catalog rule <c>runner-in-test-standard</c>; this helper checks the section pointer.
    ///     </para>
    /// </remarks>
    private static void AssertNamesTheHostTestStandardForTheRunner(string portableContent, string relativePath)
    {
        Assert.True(
            portableContent.Contains("## Completion", StringComparison.Ordinal),
            $"{relativePath} must point at the standard's '## Completion' section, which is where the "
            + "inner-loop commands live.");

        string standard = ReadRepoFile(TestCodingStandardPath);
        Assert.Contains(DirectAssemblyRunner, standard, StringComparison.Ordinal);
        Assert.Matches(DirectAssemblyRegex, standard);
    }

    [Theory]
    [InlineData(SharedExecutionSkillPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesRunTestsWithTheNoBuildFlag(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains(NoBuildFlag, content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SharedExecutionSkillPath)]
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
    [InlineData(SharedExecutionSkillPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyTheArtifactsPathAndTaskGateOutput(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("--artifacts-path .agent_scratchpad/<task-id>/artifacts", content, StringComparison.Ordinal);
        Assert.Contains("--out .agent_scratchpad/<task-id>/task-gate.json", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SharedExecutionSkillPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyTheDiscoveryHeader(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("Discovery: codegraph N, docs N, direct reads N", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ConductorExecutionContractNamesTheWorktreeCleanupAndConflictAbortCommands()
    {
        string content = ReadRepoFile(SharedExecutionSkillPath);
        Assert.Matches(@"(?i)\bgit\s+(?:cherry-pick|merge)\s+--abort\b", content);
        Assert.Contains("git worktree remove --force", content, StringComparison.Ordinal);
        Assert.Contains("git worktree prune", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Conductor is a non-developer orchestrator; its frontmatter description and body must never define
    /// or emit a TASK_GATE digest. The canonical six-field worker completion digest and full 5-state TASK_GATE
    /// enum are defined in the shared execution skill the conductor loads.
    /// </summary>
    [Fact]
    public void SharedExecutionSkill_DeclaresDeterministicTaskGate_AndConductorOffloadsIt()
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

        string executionContent = ReadRepoFile(SharedExecutionSkillPath);
        Assert.Matches(ContiguousSixFieldDigestRegex, executionContent);
        Assert.Contains("PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE", executionContent,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     One home for the report format: the skill a worker loads when it finishes carries the six
    ///     fields, the exact <c>COMMIT:</c> and <c>REMOTE_HEAD:</c> spellings and the contiguous
    ///     template. Pinning it in each agent body is what let three copies drift apart.
    /// </summary>
    [Fact]
    public void TheTaskGateSkillSpecifiesTheExplicitSixFieldCompletionDigestSchema()
    {
        string content = ReadRepoFile(TaskGateSkillPath);
        foreach (string field in RequiredDigestFields) Assert.Contains(field, content, StringComparison.Ordinal);

        Assert.Contains("COMMIT: <commit-sha | NONE>", content, StringComparison.Ordinal);
        Assert.Contains("REMOTE_HEAD: <remote-head-sha | UNPUSHED>", content, StringComparison.Ordinal);
        Assert.Matches(DigestTestCountersRegex, content);
        Assert.Matches(ContiguousSixFieldDigestRegex, content);
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    public void SpecialistSurfacesNameTheArtifactsPathFlag(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("--artifacts-path", content, StringComparison.Ordinal);
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
    public void TestCodingStandardDocumentsDirectAssemblyRunnerAndNoBuildFlag()
    {
        string content = ReadRepoFile(TestCodingStandardPath);
        Assert.Contains(DirectAssemblyRunner, content, StringComparison.Ordinal);
        Assert.Matches(DirectAssemblyRegex, content);
        Assert.DoesNotContain(QuietReporter, content, StringComparison.Ordinal);
        Assert.Contains(NoBuildFlag, content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SharedExecutionSkillPath)]
    [InlineData(ConductorAgentPath)]
    [InlineData(TestCodingStandardPath)]
    [InlineData(OnboardingDocPath)]
    [InlineData(RequirementsDocPath)]
    public void TestRunSurfacesNameTheZeroTestsDigestValue(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("TESTS: total=0", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SharedExecutionSkillPath)]
    [InlineData(ConductorAgentPath)]
    [InlineData(ArchitectureDocPath)]
    public void SurfacesConsistentlySpecifyArtifactIsolationFlags(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("dotnet build --artifacts-path .agent_scratchpad/<task-id>/artifacts", content,
            StringComparison.Ordinal);
        Assert.Contains("--out .agent_scratchpad/<task-id>/task-gate.json", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave review gates", content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CodeReviewSkillPath)]
    [InlineData(CodeReviewerAgentPath)]
    [InlineData(ReviewLensAgentPath)]
    [InlineData(ReviewTriageAgentPath)]
    [InlineData(ReviewCouncilArchitecturePath)]
    public void CouncilSurfacesSpecifyTheGateRunsEvidenceContract(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("evidence.md", content, StringComparison.Ordinal);
        Assert.DoesNotContain(".agent_scratchpad/<task-id>/review-diff.patch", content, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts/review-diff.patch", content, StringComparison.Ordinal);

        if (relativePath is ReviewLensAgentPath or ReviewTriageAgentPath)
            Assert.Matches(@"(?i)\b(?:view_file|filesystem\.read)\b", content);
    }

    [Theory]
    [InlineData(CodeReviewSkillPath)]
    [InlineData(CodeReviewerAgentPath)]
    public void CodeReviewSurfacesCiteTheRefutationGuardrailRules(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("R14", content, StringComparison.Ordinal);
        Assert.Contains("R16", content, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeReviewSkillDoesNotContainMalformedScratchpadPathTypo()
    {
        string content = ReadRepoFile(CodeReviewSkillPath);
        Assert.DoesNotContain("path`.agent_scratchpad", content, StringComparison.Ordinal);
        Assert.DoesNotContain("path.agent_scratchpad", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The conductor pastes each prompt unchanged and fills two slots and nothing else; that rule is
    /// catalog rule <c>paste-prompts-unchanged</c>. This case keeps both slot tokens spelled.
    /// </summary>
    [Fact]
    public void SharedExecutionSkillNamesBothPromptSlots()
    {
        string content = ReadRepoFile(SharedExecutionSkillPath);

        Assert.Contains("{worktree}", content, StringComparison.Ordinal);
        Assert.Contains("{prior_results}", content, StringComparison.Ordinal);
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
