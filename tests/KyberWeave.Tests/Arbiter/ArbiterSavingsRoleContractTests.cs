using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Role contract for the Arbiter savings work (tasks 3.11 to 3.15): the Squad prose under <c>products/kyber-squad</c>
/// names the Arbiter surfaces each role must use. The prose is what a role reads at dispatch time, so a role that does
/// not name the surface never calls it. Each row pins one load-bearing identifier in one file, so a reader who renames
/// the identifier has to come back here.
/// RED: none of the identifiers below appear in the Squad prose yet; the GREEN task edits those files.
/// </summary>
/// <remarks>
/// Rendering for other harnesses is not tested here. Only the source prose under <c>products/kyber-squad</c> is read.
/// </remarks>
public sealed class ArbiterSavingsRoleContractTests
{
    /// <summary>The Squad prose file and one identifier it must carry.</summary>
    public static IEnumerable<object[]> RoleProseMustNameTheSurface() =>
    [
        // Intake shortcut: the conductor answers an intake from the Arbiter's digest, not a planner spawn.
        ["agents/conductor.md", "ARBITER_DECISION"],
        ["agents/conductor.md", "intake.shortcut"],
        ["agents/conductor/references/intake-path.md", "ARBITER_DECISION"],
        ["agents/conductor/references/intake-path.md", "intake.shortcut"],

        // Triage shortcut: bug-crusher substitutes a trivial verdict for an investigator spawn.
        ["agents/bug-crusher.md", "ARBITER_DECISION"],
        ["agents/bug-crusher.md", "VERDICT: TRIVIAL"],
        ["skills/bug-crusher/SKILL.md", "ARBITER_DECISION"],
        ["skills/bug-crusher/SKILL.md", "VERDICT: TRIVIAL"],

        // Review: the run header, and the two ways a finding is withheld from the worker.
        ["agents/code-reviewer.md", "REVIEW_RUN"],
        ["agents/code-reviewer.md", "KW-REVIEW-055"],
        ["skills/code-review/SKILL.md", "REVIEW_RUN"],
        ["skills/code-review/SKILL.md", "KW-REVIEW-055"],

        // Docs-impact shortcut: a docs-dev dispatch that says it needs no documentation change.
        ["agents/docs-dev.md", "DOCS-IMPACT: true"],
        ["agents/docs-dev.md", "docs-impact.shortcut"],

        // Same-failure: a rework dispatch carries the cluster marker and the advisory is read back.
        ["skills/delivery-execution/SKILL.md", "ARBITER_SAME_FAILURE"],
        ["skills/delivery-execution/SKILL.md", "FAILURE-CLUSTER:"],

        // Bot comment triage: the review-loop skills classify each bot comment before fixing it.
        ["skills/kilo-review-loop/SKILL.md", "arbiter_classify_bot_review_comment"],
        ["skills/code-review-loop/SKILL.md", "arbiter_classify_bot_review_comment"],
    ];

    /// <summary>Each Squad prose file names the Arbiter surface its role depends on.</summary>
    [Theory]
    [MemberData(nameof(RoleProseMustNameTheSurface))]
    public void TheRoleProseNamesTheArbiterSurface(string relativePath, string identifier)
    {
        string path = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "products",
            "kyber-squad",
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"The Squad prose file '{relativePath}' must exist at '{path}'.");
        string prose = File.ReadAllText(path);
        Assert.True(
            prose.Contains(identifier, StringComparison.Ordinal),
            $"'{relativePath}' must name '{identifier}' so the role uses the Arbiter surface it depends on.");
    }
}
