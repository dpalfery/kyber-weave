using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the spec-gate collapse contract (plan item D): a tasks-phase
/// <c>READY_FOR_REVIEW</c> with <c>GAPS: none</c> and <c>OPEN_QUESTIONS: none</c>
/// lets the conductor present one consolidated review covering tasks approval and
/// the final approve-and-execute. Any gap or open question reopens separate gates,
/// and supervised mode always keeps every gate separately. Today the spec path
/// always presents separate gates, so every assertion here fails until
/// <c>spec-path.md</c> and the tasks-phase contract name the collapse.
/// </summary>
public sealed class SquadSpecGateCollapseTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string Read(params string[] relative)
    {
        return File.ReadAllText(Path.Combine([ProductRoot, .. relative]));
    }

    private static string SpecPath()
    {
        return Read("agents", "conductor", "references", "spec-path.md");
    }

    private static string TasksPhase()
    {
        return Read("skills", "product-owner", "references", "tasks-phase.md");
    }

    private static void AssertAll(string text, params string[] phrases)
    {
        foreach (string phrase in phrases)
        {
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Happy path collapses the presentation: tasks <c>READY_FOR_REVIEW</c> with no
    /// gaps and no open questions may be presented as one consolidated review covering
    /// both tasks approval and approve-and-execute, instead of two separate gates.
    /// </summary>
    [Fact]
    public void ConsolidatedReviewMayCoverTasksApprovalAndApproveAndExecuteWhenNoGapsOrQuestions()
    {
        string spec = SpecPath();

        AssertAll(spec,
            "READY_FOR_REVIEW",
            "GAPS: none",
            "OPEN_QUESTIONS: none",
            "consolidated review",
            "tasks approval",
            "approve and execute");
    }

    /// <summary>
    /// Any reported gap or open question disables the collapse for that spec: the
    /// conductor must fall back to separate gates so each question gets its own answer.
    /// </summary>
    [Fact]
    public void AnyGapOrOpenQuestionDisablesConsolidationAndRequiresSeparateGates()
    {
        string spec = SpecPath();

        string disableClause = Assert.Single(
            spec.Split('\n'),
            line => line.Contains("disables the collapse", StringComparison.Ordinal));
        AssertAll(disableClause,
            "Any gap",
            "separate gates");
        Assert.Contains("OPEN_QUESTIONS", disableClause, StringComparison.Ordinal);
    }

    /// <summary>
    /// Supervised mode is unchanged: every gate is presented separately even when there
    /// is nothing to ask, so a supervised run never sees a consolidated review.
    /// </summary>
    [Fact]
    public void SupervisedModeAlwaysKeepsSeparateGates()
    {
        string spec = SpecPath();

        string supervisedLine = Assert.Single(
            spec.Split('\n'),
            line => line.Contains("Supervised", StringComparison.Ordinal));
        Assert.Contains("separate gates", supervisedLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// The happy path costs at most two gates: the consolidated review records two
    /// autonomous approvals (tasks plus execute) from one presentation instead of
    /// charging a gate per approval.
    /// </summary>
    [Fact]
    public void ConsolidatedHappyPathHasAtMostTwoGatesWithTwoApprovalsFromOnePresentation()
    {
        string spec = SpecPath();

        AssertAll(spec,
            "max 2 gates",
            "two AUTONOMOUS_APPROVAL",
            "one presentation");
    }

    /// <summary>
    /// The collapse merges presentations, not approvals: a consolidated review still
    /// ends at <c>SPEC_FINALIZED</c> with validation evidence, so execution never starts
    /// from a Draft artifact.
    /// </summary>
    [Fact]
    public void ConsolidatedReviewStillRequiresSpecFinalizedWithValidationEvidence()
    {
        string spec = SpecPath();

        string collapsePath = Assert.Single(
            spec.Split('\n'),
            line => line.Contains("consolidated review", StringComparison.Ordinal) &&
                    line.Contains("SPEC_FINALIZED", StringComparison.Ordinal));
        Assert.Contains("validation evidence", collapsePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tasks-phase contract permits the combined recording: one consolidated
    /// presentation carries both the tasks approval and the execute approval as two
    /// approval rows, so the owner side agrees with the conductor side.
    /// </summary>
    [Fact]
    public void TasksPhasePermitsCombinedApprovalRecordingFromOnePresentation()
    {
        string tasks = TasksPhase();

        AssertAll(tasks,
            "two AUTONOMOUS_APPROVAL",
            "one presentation");
    }
}
