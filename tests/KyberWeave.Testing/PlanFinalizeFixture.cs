
namespace KyberWeave.Tests;

/// <summary>The Draft plan a finalize test starts from, and the repository that holds it.</summary>
internal static class PlanFinalizeFixture
{
    internal const string Date = "2026-11-13";
    internal const string Approval = "AUTONOMOUS_APPROVAL: approve-and-execute";
    internal const string PlanIndexTable = """
        ---
        id: plans/index
        title: Plans
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-11-07
        ---

        # Plans

        ## Active Plans

        | Plan | Status |
        |---|---|
        | [Ready tasks](2026-11-07-ready-tasks.md) | Draft |
        """;

    internal static string DraftPlan(bool autonomous = true) => PlanTaskFileProbe.PlanMarkdown()
        .Replace("status: current", "status: draft", StringComparison.Ordinal)
        .Replace("development-mode: test-first", autonomous ? "development-mode: test-first\nautonomy: autonomous" : "development-mode: test-first\nautonomy: supervised", StringComparison.Ordinal)
        .Replace("**Status: Ready.** This plan is approved for execution.", "**Status: Draft.** Awaiting approve and execute.", StringComparison.Ordinal);

    internal static DocFixture Repository(string? plan = null)
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus(plan ?? DraftPlan()).Write(PlanTaskFileProbe.PlanIndexPath, PlanIndexTable);
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        return fixture;
    }
}
