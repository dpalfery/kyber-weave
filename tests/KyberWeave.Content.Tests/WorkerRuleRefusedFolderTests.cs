using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the refused-folder stop rule to its one home, the worker baseline (decision D11, task T5), and the
/// onboarding steps that go with the agent worktree folder.
/// </summary>
/// <remarks>
/// Every subagent gets the baseline block once, so the rule that a refused folder is a stop lives there and
/// nowhere else: a worker that is refused a folder must report the path and the refusal, never build its own
/// worktree, clone or copy to work around it. The rule map records the row (D7) and the prose rule catalog
/// checks that row's home and control in <c>ProseRuleCatalogTests</c>.
/// </remarks>
public sealed class WorkerRuleRefusedFolderTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string ProductRoot = "products/kyber-squad";

    private const string BaselinePath = "products/kyber-squad/shared/worker-baseline.md";

    private const string RuleMapPath = "docs/kyber-squad/worker-rule-map.md";

    private const string OnboardingSkill = ".apm/skills/kw-onboarding/SKILL.md";

    private const string McpReference = ".apm/skills/kw-onboarding/references/mcp-and-index.md";

    private const string DoctorChecksReference = ".apm/skills/kw-onboarding/references/doctor-checks.md";

    /// <summary>
    ///     The baseline holds the refused-folder rule exactly once, under its bold lead-in, so every worker
    ///     that receives the block is told to stop rather than to build its own folder.
    /// </summary>
    [Fact]
    public void TheBaselineStatesTheRefusedFolderRuleExactlyOnce()
    {
        string baseline = Corpus.ReadText(BaselinePath);

        Assert.Equal(1, CountOccurrences(baseline, "**A refused folder is a stop.**"));
    }

    /// <summary>
    ///     No other shipped squad file restates the rule. A second copy in an agent body or a skill drifts from
    ///     the baseline and reaches every worker twice, which is what the baseline's single-home rule forbids.
    /// </summary>
    [Fact]
    public void NoOtherSquadFileRestatesTheRefusedFolderRule()
    {
        string[] repeating = Corpus.EnumerateFiles(ProductRoot, "*.md")
            .Where(path => path != BaselinePath)
            .Where(path => Corpus.ReadText(path).Contains("refused folder", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            repeating.Length == 0,
            "The refused-folder stop rule lives once, in the worker baseline. Still restated in: " +
            string.Join(", ", repeating));
    }

    /// <summary>
    ///     The rule map records the refused-folder rule as row D7, so the map and the prose rule catalog name the
    ///     same rules and the rule cannot disappear from the inventory unnoticed.
    /// </summary>
    [Fact]
    public void TheRuleMapRecordsTheRefusedFolderRow()
    {
        string map = Corpus.ReadText(RuleMapPath);

        Assert.Contains("| D7 |", map, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The onboarding reference asks, after the worktree index hook, whether to add the agent worktree folder
    ///     to the shared ignore file, so a run's worktrees never show as untracked and a copied folder is not indexed.
    /// </summary>
    [Fact]
    public void TheMcpAndIndexReferenceAsksAboutTheAgentWorktreeIgnoreLine()
    {
        string reference = Corpus.ReadText(McpReference);

        Assert.Contains(".agent_worktrees/", reference, StringComparison.Ordinal);
        Assert.Contains(".gitignore", reference, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Step 2 of the onboarding skill names the ignore-file question in its ask list, so the question is asked
    ///     where the worktree hook is asked.
    /// </summary>
    [Fact]
    public void TheOnboardingStepTwoAsksTheGitignoreQuestion()
    {
        string skill = Corpus.ReadText(OnboardingSkill);

        Assert.Contains(".gitignore", skill, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The doctor-check table explains the nested-worktrees check, what it means and its fix, so the onboarding
    ///     skill can ask about a line the doctor prints.
    /// </summary>
    [Fact]
    public void TheDoctorChecksReferenceExplainsTheNestedWorktreesCheck()
    {
        string doctorChecks = Corpus.ReadText(DoctorChecksReference);

        Assert.Contains("`codegraph.nested-worktrees`", doctorChecks, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
