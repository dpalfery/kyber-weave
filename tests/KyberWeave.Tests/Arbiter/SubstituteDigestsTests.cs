using KyberWeave.Core.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.7 (shortcuts): the substitute digests an answered shortcut hands back in place of
/// the worker's own report. The text is the contract: a worker or reviewer parses these fields by name and
/// order, so each test pins the exact lines.
/// RED: <c>SubstituteDigests</c> does not exist yet.
/// </summary>
/// <remarks>
/// Each digest is one line per field, joined with <c>\n</c>, with no trailing newline. The docs digest's
/// justification separator is an em dash, as written in the approved design.
/// </remarks>
public sealed class SubstituteDigestsTests
{
    private const string DecisionId = "dec-0001";

    [Fact]
    public void TriageDigestIsTheFiveFieldsInOrder()
    {
        string expected = string.Join(
            "\n",
            "REPRODUCED: not-run (Arbiter shortcut)",
            "ROOT_CAUSE: trivial single-site defect (Arbiter decision dec-0001)",
            "BLAST_RADIUS: single file",
            "VERDICT: TRIVIAL",
            "ARBITER_DECISION: dec-0001");

        Assert.Equal(expected, SubstituteDigests.Triage(DecisionId));
    }

    [Fact]
    public void TriageDigestHasNoStatusLine()
    {
        Assert.DoesNotContain("STATUS:", SubstituteDigests.Triage(DecisionId), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docs/todo/uploader-retry.md", "PLAN")]
    [InlineData("open request", "SPEC")]
    public void IntakeDigestIsTheSevenFieldsInOrder(string input, string recommendation)
    {
        string expected = string.Join(
            "\n",
            "STATUS: INTAKE_RECOMMENDATION",
            $"INPUT: {input}",
            $"RECOMMENDATION: {recommendation}",
            "RATIONALE: Arbiter shortcut (decision dec-0001)",
            "GAPS: none",
            "OPEN_QUESTIONS: none",
            "ARBITER_DECISION: dec-0001");

        Assert.Equal(expected, SubstituteDigests.Intake(input, recommendation, DecisionId));
    }

    [Fact]
    public void DocsImpactDigestIsTheThreeFieldsInOrder()
    {
        string expected = string.Join(
            "\n",
            "DOCS_REVIEW: no doc change with justification — Arbiter shortcut (decision dec-0001)",
            "RECOMMENDATIONS: none",
            "ARBITER_DECISION: dec-0001");

        Assert.Equal(expected, SubstituteDigests.DocsImpact(DecisionId));
    }

    [Fact]
    public void EveryDigestCarriesTheDecisionIdItWasGiven()
    {
        Assert.EndsWith("ARBITER_DECISION: dec-0001", SubstituteDigests.Triage(DecisionId), StringComparison.Ordinal);
        Assert.EndsWith("ARBITER_DECISION: dec-0001", SubstituteDigests.Intake("open request", "PLAN", DecisionId), StringComparison.Ordinal);
        Assert.EndsWith("ARBITER_DECISION: dec-0001", SubstituteDigests.DocsImpact(DecisionId), StringComparison.Ordinal);
    }
}
