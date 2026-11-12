using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the council review loop: the council re-reviews after every fix round until it returns
/// APPROVE, the loop stops after five cycles or three flat cycles with the open findings listed
/// for human review, every review after the first is a verifier cycle, a failed blocking gate is
/// fixed inside the same loop, and minor findings never loop.
/// </summary>
public sealed class SquadAutonomyRemediationTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string Read(params string[] relative)
    {
        return File.ReadAllText(Path.Combine([ProductRoot, .. relative]));
    }

    /// <summary>
    /// The council loop lives in the shared execution contract both entry points load,
    /// so that skill is the surface these pins read.
    /// </summary>
    private static string ExecutionAndReview()
    {
        return Read("skills", "delivery-execution", "SKILL.md");
    }

    private static string CodeReviewer()
    {
        return Read("agents", "code-reviewer.md");
    }

    private static string CodeReviewSkill()
    {
        return Read("skills", "code-review", "SKILL.md");
    }

    private static string Normalise(string text)
    {
        string unmarked = text
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal);

        return Regex.Replace(unmarked, @"\s+", " ").Trim();
    }

    private static void AssertSentence(string text, string sentence)
    {
        Assert.True(
            Normalise(text).Contains(Normalise(sentence), StringComparison.Ordinal),
            $"Missing sentence: {Normalise(sentence)}");
    }

    private static void AssertAll(string text, params string[] phrases)
    {
        foreach (string phrase in phrases)
        {
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The council re-reviews after every fix round until it returns APPROVE, so a fix is never
    /// accepted on the strength of the previous verdict.
    /// </summary>
    [Fact]
    public void CouncilReReviewsAfterEveryFixRoundUntilApprove()
    {
        AssertSentence(
            ExecutionAndReview(),
            "The council re-reviews after every fix round until it returns APPROVE.");
    }

    /// <summary>
    /// The loop has a hard stop: five cycles, or three cycles in a row in which the surviving
    /// critical and major count does not fall. The findings still open are listed in the pull
    /// request for human review, and delivery continues rather than parking the change.
    /// </summary>
    [Fact]
    public void LoopStopsAfterFiveCyclesOrThreeFlatCyclesAndListsTheOpenFindings()
    {
        AssertSentence(
            ExecutionAndReview(),
            "The loop stops after 5 review cycles, or after 3 cycles in a row in which the count of surviving critical and major findings does not fall; the findings still open are then listed in the pull request as pending human review, and delivery continues.");
    }

    /// <summary>
    /// A failed blocking gate is not a separate path: it is fixed inside the same loop by the
    /// worker that owns the failing scope, exactly like a finding.
    /// </summary>
    [Fact]
    public void BlockingGateFailureIsFixedInsideTheSameLoopByItsOwningWorker()
    {
        AssertSentence(
            ExecutionAndReview(),
            "A failed blocking gate is fixed inside the same loop: it goes to the worker that owns the failing scope, like a finding.");
    }

    /// <summary>
    /// A Minor-only council reports and moves on: Minor/Info findings never start another loop
    /// cycle, so a cosmetic finding cannot hold a change open.
    /// </summary>
    [Fact]
    public void MinorOnlyCouncilNeverLoops()
    {
        string execution = ExecutionAndReview();

        AssertAll(execution,
            "Minor/Info",
            "never loop");
    }

    /// <summary>
    /// Reserved-path, size, and blocking-gate verdicts are not findings for this loop: they
    /// keep their escalation and REQUEST_CHANGES paths and bypass the review loop entirely.
    /// </summary>
    [Fact]
    public void CouncilBypassPathsStayOutsideTheRemediationLoop()
    {
        string execution = ExecutionAndReview();

        AssertAll(execution,
            "blocking-gate",
            "bypass");
        Assert.DoesNotContain("KW-REVIEW-008", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-REVIEW-009", execution, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reviewer side of the contract agrees: every review after the first is a verifier
    /// cycle that re-checks the prior findings and the fix delta, not a full council.
    /// </summary>
    [Fact]
    public void CodeReviewerRunsEveryReviewAfterTheFirstAsAVerifierCycle()
    {
        AssertSentence(
            CodeReviewer(),
            "Every review after the first runs in the verifier mode of the dp-code-reviewer skill: it re-checks the prior findings and the fix delta, not a full council.");
    }

    /// <summary>
    /// The skill adjudication states the verifier cycle without rewriting the verdict table: the
    /// fixed rules still decide, and the verifier rule is stated alongside them.
    /// </summary>
    [Fact]
    public void CodeReviewSkillStatesTheVerifierCycleWhileVerdictTableStillDecides()
    {
        string skill = CodeReviewSkill();

        AssertSentence(
            skill,
            "Every review after the first runs in the verifier mode of the dp-code-reviewer skill: it re-checks the prior findings and the fix delta, not a full council.");
        AssertAll(skill,
            "Rule",
            "Verdict",
            "major-count-blocks");
    }
}
