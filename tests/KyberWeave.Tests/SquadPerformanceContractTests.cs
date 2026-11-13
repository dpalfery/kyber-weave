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
///     skills, and test coding standards: the command tokens, digest templates, renders and
///     single-home checks, and the sentences the prose rule catalog does not state for a file. The
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
    [InlineData(TestDevSkillPath)]
    public void WorkerSurfacesSpecifyBuildOnceTestManyWithDirectAssemblyRunner(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        AssertNamesTheHostTestStandardForTheRunner(content, relativePath);
        Assert.DoesNotContain(QuietReporter, content, StringComparison.Ordinal);
        Assert.Contains(NoBuildFlag, content, StringComparison.Ordinal);
    }

    [Fact]
    public void TestDevSkillSpecifiesTheDiscoveryHeader()
    {
        string content = ReadRepoFile(TestDevSkillPath);
        Assert.Contains("Discovery: codegraph N, docs N, direct reads N", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestDevSkillPath)]
    public void SpecialistSurfacesNameTheArtifactsPathFlag(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("--artifacts-path", content, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpDevAgentCarriesNoRetiredRedVerificationSentence()
    {
        string content = ReadRepoFile(CSharpDevAgentPath);
        Assert.DoesNotContain("never verify implementation without independent RED test verification", content,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    public void SpecialistPromptsPreserveTheSixFieldDigestSchema(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(
            @"(?i)\bpreserv(?:e|ing)\s+(?:the\s+)?(?:rigid\s+)?(?:6|six)-field\s+(?:completion\s+)?digest\s+schema\b",
            content);
    }

    [Fact]
    public void TestDevPromptPreservesTheSixFieldDigestSchema()
    {
        string content = ReadRepoFile(TestDevAgentPath);
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
    ///     One row per (sentence, product file) pair for the developer input-site rules the prose rule
    ///     catalog does not state for that file: the developer agents' pointer sentence to the input-site
    ///     table and the whole-class rule, and test-dev's digest non-field lines and RED-test cases. The
    ///     rest are catalog rules. A failure names the sentence and the file it is missing from.
    /// </summary>
    [Theory]
    [InlineData("S6", "Lines that are not one of the six digest fields are never counted or rejected as a seventh field. They are exactly these shapes: the Discovery: codegraph N, docs N, direct reads N header; COVERAGE_GAPS: followed by free text; RED_EVIDENCE: as `review gates --red` prints it; Input sites: N traced, M files with none; Defect class: followed by one sentence and the inputs it covers; and Artifact: or Baseline: followed by one repository-relative or absolute path.", "agents/test-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/csharp-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/typescript-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/python-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/go-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/rust-dev.md")]
    [InlineData("S7", "If your change adds or alters a site where a value from outside the process is parsed, converted, or passed to a file or collection API, fill the input-site table before you report done, and make a fix for a reported defect cover its whole class; the Developer exit gate in the delivery-execution skill holds the rule, and the correctness lens of the code-review skill defines the table.", "agents/dal-dev.md")]
    [InlineData("S8", "A test for code that parses or converts a value from outside the process has one case for every column after Source in the input-site table that the correctness lens of the code-review skill defines, and a test for a reported defect also covers at least three other inputs of its class, as the Developer exit gate in the delivery-execution skill requires.", "agents/test-dev.md")]
    public void DeveloperInputRuleSentenceIsStatedInItsProductFile(string sentenceId, string sentence, string file)
    {
        string text = Normalise(ReadRepoFile($"products/kyber-squad/{file}"));
        string expected = Normalise(sentence);

        Assert.True(
            text.Contains(expected, StringComparison.Ordinal),
            $"{sentenceId} is missing from products/kyber-squad/{file}: {expected}");
    }

    /// <summary>
    ///     The docs row for the parser fail-closed rule (KW-STD-INPUT-001) in the test standard, which the
    ///     prose rule catalog does not list as a home; the language standards' rows are catalog rules. The
    ///     row resolves from the repository root.
    /// </summary>
    [Theory]
    [InlineData("S9", "Parsers fail closed (KW-STD-INPUT-001). Code that parses or converts a value from outside the process refuses any input it does not recognise, with an error or diagnostic that names the input; it never silently drops, defaults or coerces it. The one exception is an unknown field in a format whose specification says to ignore unknown fields, and the code names that specification where it skips the field.", "docs/standards/test/README.md")]
    public void DeveloperInputRuleSentenceIsStatedInItsDocsFile(string sentenceId, string sentence, string file)
    {
        string text = Normalise(ReadRepoFile(file));
        string expected = Normalise(sentence);

        Assert.True(
            text.Contains(expected, StringComparison.Ordinal),
            $"{sentenceId} is missing from {file}: {expected}");
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
    [InlineData(CSharpDevAgentPath)]
    [InlineData(TestDevAgentPath)]
    [InlineData(TestCodingStandardPath)]
    [InlineData(OnboardingDocPath)]
    [InlineData(RequirementsDocPath)]
    public void TestRunSurfacesDescribeTheCouncilFullRunAndTheZeroTestsReport(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        string[] fullSuiteSentences = CouncilFullSuiteSentences(content).ToArray();
        Assert.NotEmpty(fullSuiteSentences);
        foreach (string sentence in fullSuiteSentences)
            Assert.DoesNotMatch(@"(?i)\bonce per run\b", sentence);
        Assert.Matches(@"(?i)`TESTS: total=0`\s+with\s+the\s+reason\b", content);

        // Catalog rule full-suite-in-first-council-review states where the full suite runs only for
        // the skill, the conductor and the test standard, so the other surfaces keep the phrases here.
        if (relativePath is not (SharedExecutionSkillPath or ConductorAgentPath or TestCodingStandardPath))
        {
            Assert.Matches(@"(?i)\bin the first full review\b", content);
            Assert.Matches(@"(?i)\bintegrated\s+head\b", content);
        }

        // The catalog's task-end-touched-classes-only rule keeps the retired task-end reservation out
        // of the other surfaces; the two developer agents are not on its absent-from list.
        if (relativePath is CSharpDevAgentPath or TestDevAgentPath)
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

    /// <summary>
    ///     The retired full-run route stays out of the shared execution skill. The conductor body is on
    ///     the catalog's <c>conductor-never-dispatches-full-run</c> absent-from list; this skill is not.
    /// </summary>
    [Fact]
    public void SharedExecutionSkillCarriesNoConductorFullRunRoute()
    {
        string content = ReadRepoFile(SharedExecutionSkillPath);
        Assert.DoesNotMatch(@"(?i)\bconductor alone dispatches one full run\b", content);
    }

    [Fact]
    public void TestStandardLeavesTheFullRunToTheCouncilTestGate()
    {
        string content = ReadRepoFile(TestCodingStandardPath);
        Assert.Matches(@"(?i)\breview council's `test` gate\b", content);
        Assert.Matches(@"(?i)\bno dev agent runs it\b", content);
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

    [Fact]
    public void KiloReviewLoopGivesNoLocalFullRunRoute()
    {
        string content = ReadRepoFile("products/kyber-squad/skills/kilo-review-loop/SKILL.md");
        Assert.Matches(@"(?i)\bdo not run the full suite locally\b", content);
        Assert.Matches(@"(?i)\btargeted set\b", content);
        Assert.Matches(@"(?i)\bclasses or files CI reported\b", content);
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
        Assert.Contains("--out .agent_scratchpad/<task-id>/task-gate.json", content, StringComparison.Ordinal);
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

        // The code-review skill and the code-reviewer agent state the cleanup and the materialization
        // as catalog rules (materialized-diff, materialized-diff-cleanup); the other surfaces are pinned here.
        if (relativePath is not (CodeReviewSkillPath or CodeReviewerAgentPath))
            Assert.Matches(CouncilDiffCleanupRegex, content);

        if (relativePath is ReviewLensAgentPath or ReviewTriageAgentPath)
        {
            Assert.Matches(@"(?i)\b(?:view_file|filesystem\.read)\b", content);
            Assert.Matches(@"(?i)\b(?:never|not|without)\s+(?:execut\w+|run\w*)\s+git\b", content);
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

    [Theory]
    [InlineData(CorrectnessLensPath)]
    [InlineData(SupportabilityLensPath)]
    public void CorrectnessAndSupportabilityLensesNameTheLifecycleStages(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Matches(@"(?i)`create`\s*→\s*`execute`\s*→\s*`integrate`\s*→\s*`cleanup`", content);
    }

    [Theory]
    [InlineData(CodeReviewSkillPath)]
    [InlineData(CodeReviewerAgentPath)]
    public void CodeReviewSurfacesCiteTheRefutationGuardrailRules(string relativePath)
    {
        string content = ReadRepoFile(relativePath);
        Assert.Contains("R14", content, StringComparison.Ordinal);
        Assert.Contains("R16", content, StringComparison.Ordinal);
        Assert.Matches(@"(?i)\bGround\s+Rule\s+1\b", content);
        Assert.Matches(@"(?i)\b(?:'repo-idiom'|repo-idiom)\b", content);
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
