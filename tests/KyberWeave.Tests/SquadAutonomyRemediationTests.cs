using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the auto-remediate loop: one actionable Critical/Major council finding earns a single
/// bounded same-session remediation rework (with a task-gate re-run) before any park flag.
/// Today the council parks immediately, so every assertion here fails until the conductor and
/// worker contracts name the loop, its cap, and its bypass paths.
/// </summary>
public sealed class SquadAutonomyRemediationTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string Read(params string[] relative)
    {
        return File.ReadAllText(Path.Combine([ProductRoot, .. relative]));
    }

    private static string ExecutionAndReview()
    {
        return Read("agents", "conductor", "references", "execution-and-review.md");
    }

    private static string CodeReviewer()
    {
        return Read("agents", "code-reviewer.md");
    }

    private static string CodeReviewSkill()
    {
        return Read("skills", "code-review", "SKILL.md");
    }

    private static void AssertAll(string text, params string[] phrases)
    {
        foreach (string phrase in phrases)
        {
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// One actionable Major must route to rework first: the conductor packet names a single
    /// bounded remediation pass before any park flag, instead of parking on first failure.
    /// </summary>
    [Fact]
    public void ActionableMajorFindingsEarnOneBoundedRemediationPassBeforeAnyPark()
    {
        string execution = ExecutionAndReview();

        AssertAll(execution,
            "one bounded remediation pass",
            "before park");
        Assert.Matches(@"\bactionable\b", execution);
        Assert.True(
            execution.IndexOf("one bounded remediation pass", StringComparison.Ordinal) <
            execution.IndexOf("before park", StringComparison.Ordinal),
            "Remediation rework must be ordered before any park flag.");
    }

    /// <summary>
    /// The remediation pass continues the same worker session in the same file scope and
    /// ends with a fresh task-gate re-run, so evidence is current instead of remembered.
    /// </summary>
    [Fact]
    public void RemediationReusesTheSameWorkerSessionAndRerunsTheTaskGate()
    {
        string execution = ExecutionAndReview();

        AssertAll(execution,
            "same session",
            "same file scope",
            "task-gate re-run");
    }

    /// <summary>
    /// The loop is bounded at one: unactionable, out-of-scope, or second-failure findings
    /// park immediately with their finding ids instead of earning another worker dispatch.
    /// </summary>
    [Fact]
    public void SecondFailureOrUnactionableFindingParksImmediatelyWithFindingIds()
    {
        string execution = ExecutionAndReview();

        AssertAll(execution,
            "Unactionable",
            "second failure",
            "park immediately",
            "finding ids");
    }

    /// <summary>
    /// A Minor-only council reports and moves on: Minor/Info findings never trigger a
    /// remediation loop and never park the change or task.
    /// </summary>
    [Fact]
    public void MinorOnlyCouncilNeverLoopsNorParks()
    {
        string execution = ExecutionAndReview();

        AssertAll(execution,
            "Minor/Info",
            "never trigger remediation",
            "never park");
    }

    /// <summary>
    /// Reserved-path, size, and blocking-gate verdicts are not findings for this loop: they
    /// keep their escalation and REQUEST_CHANGES paths and bypass remediation entirely.
    /// </summary>
    [Fact]
    public void CouncilBypassPathsStayOutsideTheRemediationLoop()
    {
        string execution = ExecutionAndReview();

        AssertAll(execution,
            "KW-REVIEW-008",
            "blocking-gate",
            "bypass");
    }

    /// <summary>
    /// The worker side of the contract agrees: actionable Critical/Major findings get one
    /// remediation pass, while Minor/Info findings are reported without a loop or park.
    /// </summary>
    [Fact]
    public void CodeReviewerRoutesActionableCriticalMajorFindingsToOneRemediationPass()
    {
        string reviewer = CodeReviewer();

        AssertAll(reviewer,
            "actionable",
            "Critical",
            "Major",
            "one remediation pass",
            "Minor/Info");
    }

    /// <summary>
    /// The skill adjudication notes the same loop without rewriting the verdict table: the
    /// fixed rules still decide, and the Minor/Info never-loop rule is stated alongside them.
    /// </summary>
    [Fact]
    public void CodeReviewSkillNotesRemediationLoopWhileVerdictTableStillDecides()
    {
        string skill = CodeReviewSkill();

        AssertAll(skill,
            "remediation pass",
            "Minor/Info",
            "| # | Rule | Verdict |",
            "major-count-blocks");
    }
}
