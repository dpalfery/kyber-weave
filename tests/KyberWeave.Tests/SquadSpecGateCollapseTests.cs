using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Checks the tokens and line structure of the spec-gate collapse contract (plan item D) in
/// <c>spec-path.md</c>: the <c>READY_FOR_REVIEW</c>, <c>GAPS: none</c> and
/// <c>OPEN_QUESTIONS: none</c> condition, the one line that disables the collapse, the one
/// supervised line, and the one line tying a consolidated review to <c>SPEC_FINALIZED</c>.
/// The collapse rules themselves are graded from the prose rule catalog
/// (<c>Fixtures/prose-rules/catalog.yml</c>).
/// </summary>
public sealed class SquadSpecGateCollapseTests
{
    private static string SpecPath()
    {
        return File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad", "agents", "conductor", "references", "spec-path.md"));
    }

    /// <summary>
    /// The collapse condition names the tasks-phase <c>READY_FOR_REVIEW</c> digest with no gaps
    /// and no open questions.
    /// </summary>
    [Fact]
    public void SpecPathNamesTheCollapseConditionTokens()
    {
        string spec = SpecPath();

        Assert.Contains("READY_FOR_REVIEW", spec, StringComparison.Ordinal);
        Assert.Contains("GAPS: none", spec, StringComparison.Ordinal);
        Assert.Contains("OPEN_QUESTIONS: none", spec, StringComparison.Ordinal);
    }

    /// <summary>
    /// One line disables the collapse, and it names <c>OPEN_QUESTIONS</c>.
    /// </summary>
    [Fact]
    public void OneLineDisablesTheCollapseAndNamesOpenQuestions()
    {
        string disableClause = Assert.Single(
            SpecPath().Split('\n'),
            line => line.Contains("disables the collapse", StringComparison.Ordinal));
        Assert.Contains("OPEN_QUESTIONS", disableClause, StringComparison.Ordinal);
    }

    /// <summary>
    /// Supervised mode is stated on exactly one line of the spec path.
    /// </summary>
    [Fact]
    public void SpecPathStatesSupervisedModeOnOneLine()
    {
        _ = Assert.Single(
            SpecPath().Split('\n'),
            line => line.Contains("Supervised", StringComparison.Ordinal));
    }

    /// <summary>
    /// Exactly one line ties a consolidated review to <c>SPEC_FINALIZED</c>.
    /// </summary>
    [Fact]
    public void OneLineTiesTheConsolidatedReviewToSpecFinalized()
    {
        _ = Assert.Single(
            SpecPath().Split('\n'),
            line => line.Contains("consolidated review", StringComparison.Ordinal) &&
                    line.Contains("SPEC_FINALIZED", StringComparison.Ordinal));
    }
}
