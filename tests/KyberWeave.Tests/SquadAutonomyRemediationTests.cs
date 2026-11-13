using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Checks the tokens and structure of the council review loop: the severity band that never loops,
/// the blocking-gate bypass, the retired reserved-path rule ids, and the code-review verdict table.
/// The loop rules themselves (re-review until APPROVE, the cycle bounds, blocking gates fixed in the
/// loop, verifier cycles) are graded from the prose rule catalog (<c>Fixtures/prose-rules/catalog.yml</c>).
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
    /// so that skill is the surface these checks read.
    /// </summary>
    private static string ExecutionAndReview()
    {
        return Read("skills", "delivery-execution", "SKILL.md");
    }

    private static string CodeReviewSkill()
    {
        return Read("skills", "code-review", "SKILL.md");
    }

    /// <summary>
    /// The execution contract names the Minor/Info severity band; the rule that it never starts
    /// another loop cycle is graded from the catalog (<c>minor-findings-never-loop</c>).
    /// </summary>
    [Fact]
    public void ExecutionContractNamesTheMinorInfoBand()
    {
        Assert.Contains("Minor/Info", ExecutionAndReview(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Reserved-path, size, and blocking-gate verdicts are not findings for this loop: they
    /// keep their escalation and REQUEST_CHANGES paths and bypass the review loop entirely.
    /// </summary>
    [Fact]
    public void CouncilBypassPathsStayOutsideTheRemediationLoop()
    {
        string execution = ExecutionAndReview();

        Assert.Contains("blocking-gate", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-REVIEW-008", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-REVIEW-009", execution, StringComparison.Ordinal);
    }

    /// <summary>
    /// The code-review skill keeps its verdict table with the fixed <c>major-count-blocks</c> rule.
    /// </summary>
    [Fact]
    public void CodeReviewSkillKeepsTheVerdictTable()
    {
        Assert.Contains("major-count-blocks", CodeReviewSkill(), StringComparison.Ordinal);
    }
}
