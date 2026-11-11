using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Regression contract tests for Kyber-Squad performance and parallelization invariants across
///     canonical agent instructions, the shared execution skill both entry points load, test-dev
///     skills, and test coding standards.
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
    private const string TestDevSkillPath = "products/kyber-squad/skills/test-dev/SKILL.md";

    /// <summary>
    ///     The skill a worker loads when it finishes. Report formats — the completion digest and its
    ///     fields — belong here rather than in each agent body (the rule map's "Report formats" row),
    ///     so the pins below read this file instead of the per-agent copies.
    /// </summary>
    private const string TaskGateSkillPath = "products/kyber-squad/skills/task-gate/SKILL.md";

    /// <summary>
    ///     The one always-on source of worker text (decision D27): the Discovery order, the plan-read
    ///     ban, the full-suite ban and the batched-read rule are injected from here into every worker
    ///     render, so no agent body repeats them.
    /// </summary>
    private const string WorkerBaselinePath = "products/kyber-squad/shared/worker-baseline.md";
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

    private const string NeverRunFullSuiteRegex = @"(?i)\bnever run the full test suite\b";

    private const string DigestTestCountersRegex =
        @"TESTS:\s*total=(?:\d+|N|<[^>\r\n]+>)\s+failed=(?:\d+|N|<[^>\r\n]+>)\s+passed=(?:\d+|N|<[^>\r\n]+>)";

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

    /// <summary>
    ///     A tripwire for the retired wording, not proof that workers obey the new rule; the
    ///     instruction itself enforces that. When "task end" counted as a full-suite moment, every
    ///     worker that finished ran the whole suite, and harnesses finish workers together, so
    ///     several full runs landed on one machine at once. It reads sentences, not lines: hard
    ///     wraps in Markdown would otherwise hide the old wording, while a task-end clause in the
    ///     next sentence ("At task end a worker runs only ...") must not be mistaken for it. A
    ///     blank line, heading, bullet or table row starts a new block, so an unpunctuated bullet
    ///     never borrows words from its neighbour; "e.g." and "i.e." do not end a sentence.
    /// </summary>
    internal static bool ReservesFullSuiteForTaskEnd(string content)
    {
        string text = Regex.Replace(content, @"\b(e\.g|i\.e)\.", "$1", RegexOptions.IgnoreCase);
        foreach (string block in Regex.Split(
                     text,
                     @"(?:\r?\n){2,}|\r?\n(?=\s*(?:[-*+]\s|\d+\.\s|#{1,6}\s|\|))|(?<=^#{1,6}[ \t][^\r\n]*)\r?\n",
                     RegexOptions.Multiline))
        {
            string flattened = Regex.Replace(block, @"\s+", " ");
            foreach (string sentence in Regex.Split(flattened, @"(?<=[.!?])\s+"))
                if (Regex.IsMatch(sentence, @"\breserv(?:e|es|ed|ing|ation|ations)\b", RegexOptions.IgnoreCase) &&
                    Regex.IsMatch(sentence, @"\bfull[- ](?:test[- ])?suite\b", RegexOptions.IgnoreCase) &&
                    Regex.IsMatch(sentence, @"\btask[- ](?:end|exit[- ]gates?)\b", RegexOptions.IgnoreCase))
                    return true;
        }

        return false;
    }

    [Theory]
    [InlineData("Reserve full test suite execution for task end or the final review council.", true)]
    [InlineData("Full test suite runs are reserved strictly for task exit gates and final council review.", true)]
    [InlineData("Full test suite\nruns are reserved strictly for task\nexit gates and final council review.", true)]
    [InlineData(
        "Reserve full test suite execution for the final review council. A worker's task end runs only the test classes its task touched.",
        false)]
    [InlineData("Task end used to be a full-suite moment.", false)]
    [InlineData("Full suite runs are reserved for task-end verification.", true)]
    [InlineData("The reservoir holds the full suite at task end.", false)]
    [InlineData("Reserve the full suite, e.g. at task end.", true)]
    [InlineData("Reserve the full suite, E.g. at task end.", true)]
    [InlineData("Full suite reservations cover task end.", true)]
    [InlineData("- Reserve the full suite for the council\n- At task end run only touched classes", false)]
    [InlineData("## Reserve the full suite\nAt task end run only touched classes", false)]
    public void FullSuiteReservationHelperReadsSentencesNotLines(string input, bool expected)
    {
        Assert.Equal(expected, ReservesFullSuiteForTaskEnd(input));
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
    [InlineData(SharedExecutionSkillPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyDirectAssemblyTestRunner(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        AssertNamesTheHostTestStandardForTheRunner(content, relativePath);
        Assert.DoesNotContain(QuietReporter, content, StringComparison.Ordinal);
        Assert.Matches(@"(?i)\bpre-?computed\s+test\s+commands?\b", content);
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
    ///         never reached.
    ///     </para>
    /// </remarks>
    private static void AssertNamesTheHostTestStandardForTheRunner(string portableContent, string relativePath)
    {
        Assert.True(
            portableContent.Contains("test standard", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must name the host's test standard as the home of the exact runner; a "
            + "portable file cannot carry this repository's own assembly path.");
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
    [InlineData(SharedExecutionSkillPath)]
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
    public void ConductorSurfacesSpecifyInlinedTaskPacketContents(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)\binlined\s+entry-?file\s+table\b", content);
        Assert.Contains("acceptance criteria", content, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"(?i)\bpre-?computed\s+test\s+commands?\b", content);
        Assert.Matches(@"(?i)\bout\s+of\s+(?:the\s+)?worker\s+context\b", content);
    }

    [Theory]
    [InlineData(SharedExecutionSkillPath)]
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
    [InlineData(SharedExecutionSkillPath)]
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
    [InlineData(SharedExecutionSkillPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesSpecifyDiscoveryHeaderPrecedesDigest(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("Discovery: codegraph N, docs N, direct reads N", content, StringComparison.Ordinal);
        Assert.Matches(@"(?i)\bprecedes?\s+(?:the\s+)?(?:six|6)-field\s+(?:completion\s+)?digest\b", content);
    }

    [Theory]
    [InlineData(ConductorAgentPath)]
    [InlineData(SharedExecutionSkillPath)]
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
        string content = ReadRepoFile(SharedExecutionSkillPath);
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

    [Theory]
    [InlineData(ConductorAgentPath)]
    [InlineData(SharedExecutionSkillPath)]
    public void ConductorSurfacesScopeSixFieldRejectionToMigratedSpecialistsDuringPhasedRollout(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?im)^(?=[^\r\n]*\b(?:phased\s+rollout|migrated\s+(?:specialist\s+)?agents?)\b)(?=[^\r\n]*\bcsharp-dev\b)(?=[^\r\n]*\btest-dev\b)[^\r\n]+",
            content);
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
    [InlineData(TestDevSkillPath)]
    public void WorkerSurfacesSpecifyBuildOnceTestManyWithDirectAssemblyRunner(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        AssertNamesTheHostTestStandardForTheRunner(content, relativePath);
        Assert.DoesNotContain(QuietReporter, content, StringComparison.Ordinal);
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

    /// <summary>
    ///     One row per (sentence, product file) pair for the developer input-site rules: the table an
    ///     implementer fills for a change that takes a value from outside the process, the whole-class
    ///     rule for defect fixes, the six-field digest's non-field lines, and the test-dev and developer
    ///     agent obligations that follow from them. A failure names the sentence and the file it is missing from.
    /// </summary>
    [Theory]
    [InlineData("S1", "An implementation worker whose change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API fills the input-site table for that change before it reports done.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S2", "The table's columns and cell values are defined once, in the correctness lens of the code-review skill (references/lenses/correctness.md); the worker uses them unchanged.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S3", "Every THROWS or accepted wrong cell is fixed before the worker reports done, and each fixed cell is covered by a test that fails without the fix; a fixed cell that no test covers yet goes on the worker's COVERAGE_GAPS line, and the conductor sends it to test-dev before the task clears its exit gate.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S4", "The worker saves the table as .agent_scratchpad/<task-id>/input-sites.md, cites it on an Artifact: line, and reports Input sites: N traced, M files with none in the message body beside its digest.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S5", "A fix for a reported defect covers the defect's whole class: the worker states the class in one sentence and names at least three other inputs of that class that the original failure scenario did not name, each covered by a test, on one line: Defect class: <one sentence> — inputs: <input>; <input>; <input>.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S6", "Lines that are not one of the six digest fields are never counted or rejected as a seventh field. They are exactly these shapes: the Discovery: codegraph N, docs N, direct reads N header; COVERAGE_GAPS: followed by free text; RED_EVIDENCE: followed by the runner filter, the failing tests and the reason; Input sites: N traced, M files with none; Defect class: followed by one sentence and the inputs it covers; and Artifact: or Baseline: followed by one repository-relative or absolute path.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S6", "Lines that are not one of the six digest fields are never counted or rejected as a seventh field. They are exactly these shapes: the Discovery: codegraph N, docs N, direct reads N header; COVERAGE_GAPS: followed by free text; RED_EVIDENCE: followed by the runner filter, the failing tests and the reason; Input sites: N traced, M files with none; Defect class: followed by one sentence and the inputs it covers; and Artifact: or Baseline: followed by one repository-relative or absolute path.", "skills/task-gate/SKILL.md")]
    [InlineData("S6", "Lines that are not one of the six digest fields are never counted or rejected as a seventh field. They are exactly these shapes: the Discovery: codegraph N, docs N, direct reads N header; COVERAGE_GAPS: followed by free text; RED_EVIDENCE: followed by the runner filter, the failing tests and the reason; Input sites: N traced, M files with none; Defect class: followed by one sentence and the inputs it covers; and Artifact: or Baseline: followed by one repository-relative or absolute path.", "agents/test-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/csharp-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/typescript-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/python-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/go-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/rust-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/dal-dev.md")]
    [InlineData("S8", "A RED test for code that parses or converts a value from outside the process has one case for every column after Source in the input-site table that the correctness lens of the code-review skill defines, and a RED test for a reported defect also covers at least three other inputs of its class, as the Developer exit gate in the delivery-execution skill requires.", "agents/test-dev.md")]
    public void DeveloperInputRuleSentenceIsStatedInItsProductFile(string sentenceId, string sentence, string file)
    {
        string text = Normalise(ReadRepoFile($"products/kyber-squad/{file}"));
        string expected = Normalise(sentence);

        Assert.True(
            text.Contains(expected, StringComparison.Ordinal),
            $"{sentenceId} is missing from products/kyber-squad/{file}: {expected}");
    }

    /// <summary>
    ///     The docs rows for the developer input-site rules: the six-field digest's non-field lines in the
    ///     requirements, and the parser fail-closed rule (KW-STD-INPUT-001) and its test obligation in each
    ///     language standard. Each row resolves from the repository root.
    /// </summary>
    [Theory]
    [InlineData("S6", "Lines that are not one of the six digest fields are never counted or rejected as a seventh field. They are exactly these shapes: the Discovery: codegraph N, docs N, direct reads N header; COVERAGE_GAPS: followed by free text; RED_EVIDENCE: followed by the runner filter, the failing tests and the reason; Input sites: N traced, M files with none; Defect class: followed by one sentence and the inputs it covers; and Artifact: or Baseline: followed by one repository-relative or absolute path.", "docs/kyber-squad/requirements.md")]
    [InlineData("S9", "Parsers fail closed (KW-STD-INPUT-001). Code that parses or converts a value from outside the process refuses any input it does not recognise, with an error or diagnostic that names the input; it never silently drops, defaults or coerces it. The one exception is an unknown field in a format whose specification says to ignore unknown fields, and the code names that specification where it skips the field.", "docs/standards/csharp/README.md")]
    [InlineData("S9", "Parsers fail closed (KW-STD-INPUT-001). Code that parses or converts a value from outside the process refuses any input it does not recognise, with an error or diagnostic that names the input; it never silently drops, defaults or coerces it. The one exception is an unknown field in a format whose specification says to ignore unknown fields, and the code names that specification where it skips the field.", "docs/standards/typescript/README.md")]
    [InlineData("S9", "Parsers fail closed (KW-STD-INPUT-001). Code that parses or converts a value from outside the process refuses any input it does not recognise, with an error or diagnostic that names the input; it never silently drops, defaults or coerces it. The one exception is an unknown field in a format whose specification says to ignore unknown fields, and the code names that specification where it skips the field.", "docs/standards/go/README.md")]
    [InlineData("S9", "Parsers fail closed (KW-STD-INPUT-001). Code that parses or converts a value from outside the process refuses any input it does not recognise, with an error or diagnostic that names the input; it never silently drops, defaults or coerces it. The one exception is an unknown field in a format whose specification says to ignore unknown fields, and the code names that specification where it skips the field.", "docs/standards/rust/README.md")]
    [InlineData("S9", "Parsers fail closed (KW-STD-INPUT-001). Code that parses or converts a value from outside the process refuses any input it does not recognise, with an error or diagnostic that names the input; it never silently drops, defaults or coerces it. The one exception is an unknown field in a format whose specification says to ignore unknown fields, and the code names that specification where it skips the field.", "docs/standards/test/README.md")]
    [InlineData("S10", "A test of such code has one case for every column after Source in the input-site table that the correctness lens of the code-review skill defines, and asserts the refusal.", "docs/standards/test/README.md")]
    public void DeveloperInputRuleSentenceIsStatedInItsDocsFile(string sentenceId, string sentence, string file)
    {
        string text = Normalise(ReadRepoFile(file));
        string expected = Normalise(sentence);

        Assert.True(
            text.Contains(expected, StringComparison.Ordinal),
            $"{sentenceId} is missing from {file}: {expected}");
    }

    [Fact]
    public void TestCodingStandardDocumentsDirectAssemblyRunnerAndBuildOnceIteration()
    {
        string content = ReadRepoFile(TestCodingStandardPath);
        Assert.Contains(DirectAssemblyRunner, content, StringComparison.Ordinal);
        Assert.Matches(DirectAssemblyRegex, content);
        Assert.DoesNotContain(QuietReporter, content, StringComparison.Ordinal);
        Assert.Contains(NoBuildFlag, content, StringComparison.Ordinal);
        Assert.True(
            HasBuildOnceReservationClause(content),
            $"{TestCodingStandardPath} must specify reserving full test suite execution for task end or council.");
    }

    [Theory]
    [InlineData(SharedExecutionSkillPath)]
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
        Assert.Matches(@"(?i)\bonce per review cycle\b", content);
        string[] fullSuiteSentences = CouncilFullSuiteSentences(content).ToArray();
        Assert.NotEmpty(fullSuiteSentences);
        foreach (string sentence in fullSuiteSentences)
            Assert.DoesNotMatch(@"(?i)\bonce per run\b", sentence);
        Assert.Matches(@"(?i)\bintegrated\s+head\b", content);
        Assert.Matches(@"(?i)`TESTS: total=0`\s+with\s+the\s+reason\b", content);
        Assert.False(
            ReservesFullSuiteForTaskEnd(content),
            $"{relativePath} must not reserve the full suite for task end.");
    }

    /// <summary>
    ///     The sentences describing the council's full-suite run. The "once per run" absence check is
    ///     scoped to these rather than the whole file, because the phrase may occur in unrelated prose.
    /// </summary>
    private static IEnumerable<string> CouncilFullSuiteSentences(string content)
    {
        string flattened = Regex.Replace(content, @"\s+", " ");
        foreach (string sentence in Regex.Split(flattened, @"(?<=[.!?])\s+"))
            if (Regex.IsMatch(sentence, @"\bfull\b", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(sentence, @"\bcouncil\b", RegexOptions.IgnoreCase))
                yield return sentence;
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestCodingStandardPath)]
    public void WorkerSurfacesRunATargetedSetOnSharedTestInfrastructure(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)\bshared test infrastructure\b", content);
        Assert.Matches(@"(?i)\btargeted set\b", content);
        Assert.Matches(@"(?i)\bcross-cutting\b", content);
        Assert.DoesNotMatch(@"(?i)\bask the conductor for one full run\b", content);
    }

    [Theory]
    [InlineData(SharedExecutionSkillPath)]
    [InlineData(ConductorAgentPath)]
    public void ConductorSurfacesNeverDispatchAFullRun(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)\bshared test infrastructure\b", content);
        Assert.Matches(@"(?i)\bconductor never dispatches a full run\b", content);
        Assert.Matches(@"(?i)\btargeted set\b", content);
        Assert.DoesNotMatch(@"(?i)\bconductor alone dispatches one full run\b", content);
    }

    [Fact]
    public void TestStandardDefinesSharedTestInfrastructureAndGivesNoFullRunRoute()
    {
        string content = ReadRepoFile(TestCodingStandardPath);
        Assert.Matches(@"(?i)\banything referenced by more than one test class\b", content);
        Assert.Matches(@"(?i)`codegraph_explore` on the type shows\b", content);
        Assert.Matches(@"(?i)\btest-project build settings, which every test class depends on\b", content);
        Assert.Matches(@"(?i)\breview council's `test` gate\b", content);
        Assert.Matches(@"(?i)\bno dev agent runs it\b", content);
        Assert.DoesNotMatch(@"(?i)\bat most one full run\b", content);
    }

    /// <summary>
    ///     Only the review council and CI run the full suite. Decision D27 puts that ban in one
    ///     always-on line injected into every worker's text from the shared baseline, so the ban is
    ///     asserted where it is written, and no agent body repeats it — text in each agent file is
    ///     re-sent on every round trip of every worker.
    /// </summary>
    [Fact]
    public void TheSharedBaselineIsTheOnePlaceTheFullSuiteBanIsWritten()
    {
        Assert.Matches(NeverRunFullSuiteRegex, ReadRepoFile(WorkerBaselinePath));

        string agents = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad", "agents");
        string[] repeating = Directory.GetFiles(agents, "*.md", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(File.ReadAllText(path), NeverRunFullSuiteRegex))
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToArray();

        Assert.True(
            repeating.Length == 0,
            "The full-suite ban is injected from the shared baseline; these agent bodies repeat it: " +
            string.Join(", ", repeating));
    }

    /// <summary>The harnesses that ship; each one renders the bug-crusher surfaces.</summary>
    public static TheoryData<SquadTarget> ShippedRenderTargets() =>
        new(SquadCommandComposition.CreateRenderers().SelectMany(renderer => renderer.SupportedTargets));

    /// <summary>
    ///     The bug-crusher skill is a primary session, so the baseline never reaches it and the ban has
    ///     to be in its own text.
    /// </summary>
    [Fact]
    public void BugCrusherSkillIsToldNeverToRunTheFullTestSuite()
    {
        Assert.Matches(
            NeverRunFullSuiteRegex,
            ReadRepoFile("products/kyber-squad/skills/bug-crusher/SKILL.md"));
    }

    /// <summary>
    ///     The investigator is a dispatched subagent, so the ban reaches it the way it reaches every
    ///     worker: from the shared baseline, once per render (decision D27). The evidence that this
    ///     agent is told never to run the full suite is therefore that render, not a second copy in its
    ///     body — a body copy is what
    ///     <see cref="TheSharedBaselineIsTheOnePlaceTheFullSuiteBanIsWritten"/> forbids. The subject is
    ///     unchanged; only where it is read from moved.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedRenderTargets))]
    public async Task BugCrusherInvestigatorIsToldNeverToRunTheFullTestSuiteByItsRender(SquadTarget target)
    {
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(target);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        (string RelativePath, string Content)[] investigatorFiles =
        [
            .. result.Files
                .Where(file => file.RelativePath.Contains("bug-crusher-investigator", StringComparison.Ordinal))
                .Select(file => (file.RelativePath, Content: Encoding.UTF8.GetString(file.Content.Span)))
        ];

        Assert.True(
            investigatorFiles.Length > 0,
            $"The {target} render carries no file for 'bug-crusher-investigator', so this case would prove nothing.");

        Assert.True(
            investigatorFiles.Any(file => Regex.IsMatch(file.Content, NeverRunFullSuiteRegex)),
            $"The {target} render of 'bug-crusher-investigator' carries no 'never run the full test suite' line. " +
            "It reaches the agent from the shared baseline, which every worker render injects once.");
    }

    [Theory]
    [InlineData("products/kyber-squad/skills/rust-dev/references/gates-and-platforms.md")]
    [InlineData("products/kyber-squad/skills/go-dev/references/gates-and-platforms.md")]
    [InlineData("products/kyber-squad/skills/typescript-dev/references/esm-and-gates.md")]
    public void LanguageGateTablesGiveATargetedTestCommand(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.DoesNotMatch(@"(?i)but run it to prove you broke nothing", content);
        Assert.Matches(NeverRunFullSuiteRegex, content);
    }

    [Fact]
    public void KiloReviewLoopGivesNoLocalFullRunRoute()
    {
        string content = ReadRepoFile("products/kyber-squad/skills/kilo-review-loop/SKILL.md");
        Assert.DoesNotMatch(@"(?i)\brun it locally only when\b", content);
        Assert.Matches(@"(?i)\bdo not run the full suite locally\b", content);
        Assert.Matches(@"(?i)\btargeted set\b", content);
        Assert.Matches(@"(?i)\bclasses or files CI reported\b", content);
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
    [InlineData(SharedExecutionSkillPath)]
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

    /// <summary>
    /// The conductor holds the docs tools and no shell, so the task file is checked by a tool it
    /// can call: a file that disagrees with its plan, or one whose digest is stale, has to be
    /// refused before a worker is dispatched from it.
    /// </summary>
    [Fact]
    public void SharedExecutionSkillRequiresTheTaskFileToBeValidatedBeforeDispatch()
    {
        string content = ReadRepoFile(SharedExecutionSkillPath);

        string? checking = BlockContainingBoth(content, "task file", "validat");
        Assert.True(
            checking is not null,
            $"{SharedExecutionSkillPath} must say the conductor validates the plan's task file before it " +
            "dispatches from it.");
    }

    /// <summary>
    /// The conductor pastes each prompt unchanged and fills two slots and nothing else. A
    /// conductor that rewrote a prompt would reintroduce the 128 KB of conductor-authored text
    /// this work replaces, and the plan would no longer describe what the worker was told.
    /// </summary>
    [Fact]
    public void SharedExecutionSkillPastesPromptsUnchangedAndFillsOnlyTheTwoSlots()
    {
        string content = ReadRepoFile(SharedExecutionSkillPath);

        Assert.Contains("{worktree}", content, StringComparison.Ordinal);
        Assert.Contains("{prior_results}", content, StringComparison.Ordinal);

        string? pasting = BlockContainingBoth(content, "paste", "unchanged");
        Assert.True(
            pasting is not null,
            $"{SharedExecutionSkillPath} must say each prompt is pasted unchanged.");

        string? slots = BlockContainingBoth(content, "{worktree}", "{prior_results}");
        Assert.True(slots is not null, $"{SharedExecutionSkillPath} must name both slots in one instruction.");
        Assert.Matches(@"(?i)\b(?:only|nothing else|exclusively)\b", slots);
    }

    /// <summary>
    /// Resuming carried nothing because the prompts said "Do not commit", so a mid-run push held
    /// no record of a finished task. A finished task commits with a <c>Task: &lt;id&gt;</c> line
    /// and its report, which is what a later run reads to learn where the work stopped.
    /// </summary>
    [Fact]
    public void SharedExecutionSkillTellsWorkersToCommitWithATaskLine()
    {
        string content = ReadRepoFile(SharedExecutionSkillPath);

        string? committing = BlockContaining(content, block =>
            block.Contains("Task:", StringComparison.Ordinal)
            && Regex.IsMatch(block, @"(?i)\bcommit\w*\b"));

        Assert.True(
            committing is not null,
            $"{SharedExecutionSkillPath} must tell a finished task to commit with a 'Task: <id>' line.");
    }

    /// <summary>The first blank-line separated block that contains every one of <paramref name="needles"/>.</summary>
    private static string? BlockContainingBoth(string content, string first, string second)
    {
        return BlockContaining(
            content,
            block => block.Contains(first, StringComparison.OrdinalIgnoreCase)
                     && block.Contains(second, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The first blank-line separated block satisfying <paramref name="match"/>: a rule is read
    /// from the paragraph that states it, not from words that happen to sit near each other in a
    /// long document.
    /// </summary>
    private static string? BlockContaining(string content, Func<string, bool> match)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(match);

        foreach (string block in content
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (match(block))
                return block;
        }

        return null;
    }

    /// <summary>
    ///     Removes Markdown emphasis and code markers and collapses every whitespace run to one space, so a
    ///     re-wrap or an emphasis change does not move a sentence while its words and their order still must match.
    /// </summary>
    private static string Normalise(string text)
    {
        string unmarked = text
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal);

        return Regex.Replace(unmarked, @"\s+", " ").Trim();
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
