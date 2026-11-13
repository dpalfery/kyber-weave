using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the review council's Arbiter contract (packet 7.3): the <c>LENS</c> and
/// <c>REFUTE</c> routing headers on the reviewer's dispatches, the handling of each
/// Arbiter review note status, the gate, verdict and audit commands the run executes, and
/// <c>review-lens</c>'s refutation header. How each note and blocked spawn is handled, and
/// the refuter's default, are graded from the prose rule catalog.
/// </summary>
public sealed class ArbiterReviewContractTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string ReviewerContract =>
        File.ReadAllText(Path.Combine(ProductRoot, "agents", "code-reviewer.md"));

    private static string LensContract =>
        File.ReadAllText(Path.Combine(ProductRoot, "agents", "review-lens.md"));

    private static string SkillContract =>
        File.ReadAllText(Path.Combine(ProductRoot, "skills", "code-review", "SKILL.md"));

    [Fact]
    public void CouncilDispatches_OpenWithArbiterMarkerAndLensHeader()
    {
        string contract = ReviewerContract;

        Assert.Contains("KYBER-ARBITER: true", contract, StringComparison.Ordinal);
        Assert.Contains("LENS: <lens>", contract, StringComparison.Ordinal);
        Assert.Contains("header block", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void RefutationDispatches_CarryRefuteHeaderAndFindingYamlFollowsTheHeaderBlock()
    {
        Assert.Contains(
            "REFUTE: <lens>/<slug>",
            ReviewerContract,
            StringComparison.Ordinal);
        Assert.Contains(
            "after the header block",
            ReviewerContract,
            StringComparison.Ordinal);
        // The design pins the <lens>/<slug> form by example; keep one canonical example cited.
        Assert.Contains("security/key-in-argv", ReviewerContract, StringComparison.Ordinal);
        Assert.Contains("REFUTE: <lens>/<slug>", SkillContract, StringComparison.Ordinal);
    }

    [Fact]
    public void AzureReaderDispatches_CarryTheMarkerWithoutALens()
    {
        string contract = ReviewerContract;

        Assert.Contains("azure-reader", contract, StringComparison.Ordinal);
        Assert.Contains("carries the marker and no", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void ArbiterSkipNotes_RecordTheLensAsSkippedWithItsReason()
    {
        string contract = ReviewerContract;

        Assert.Contains("STATUS: ARBITER_SKIP", contract, StringComparison.Ordinal);
        Assert.Contains("SKIPPED", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void ArbiterVerifiedNotes_AreHandledByStatus()
    {
        Assert.Contains("STATUS: ARBITER_VERIFIED", ReviewerContract, StringComparison.Ordinal);
    }

    [Fact]
    public void ArbiterAnnotationNotes_AreHandledByStatus()
    {
        Assert.Contains("STATUS: ARBITER_ANNOTATION", ReviewerContract, StringComparison.Ordinal);
    }

    [Fact]
    public void GateCommands_PassBaseAndWriteReportsUnderArtifacts()
    {
        Assert.Contains(
            "kyber-weave review gates . --base <ref> --out artifacts/gates.json",
            SkillContract,
            StringComparison.Ordinal);
        Assert.Contains(
            "kyber-weave review gates . --base <ref> --out artifacts/gates.json",
            ReviewerContract,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdict is computed from the seat folder of each cycle, not from a hand-written findings
    /// file. The retired <c>--findings</c> form let a run skip the seat record that KW-REVIEW-041 checks.
    /// </summary>
    [Fact]
    public void VerdictCommand_ComputesFromTheCycleSeatsFolder()
    {
        string skill = SkillContract;

        Assert.Matches(@"kyber-weave review verdict \. --seats[^\n]*--out", skill);
        Assert.Contains(".agent_scratchpad/<task-id>/review/cycle-<n>/", skill, StringComparison.Ordinal);
        Assert.DoesNotContain("kyber-weave review verdict . --findings", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_CitesTheArbiterAudit()
    {
        Assert.Contains(
            "kyber-weave arbiter audit --plan <PLAN_FILE>",
            ReviewerContract,
            StringComparison.Ordinal);
        Assert.Contains(
            "kyber-weave arbiter audit --plan <PLAN_FILE>",
            SkillContract,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RefutationRuns_CarryTheRefuteHeaderAndFindingYamlFollowsTheHeaderBlock()
    {
        string contract = LensContract;

        Assert.Contains("REFUTE: <lens>/<slug>", contract, StringComparison.Ordinal);
        Assert.Contains("after the header block", contract, StringComparison.Ordinal);
    }
}
