using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The architect and the product-owner write a <c>Difficulty:</c> and one <c>Technology:</c> line on every
/// file-changing task, split work that spans two unrelated technologies, and read a standard through the docs
/// tool unless it is missing or fails.
/// </summary>
public sealed class PlannerDifficultyAndTechnologyContractTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    public static IEnumerable<object[]> PlannerFiles()
    {
        yield return ["products/kyber-squad/agents/architect.md"];
        yield return ["products/kyber-squad/agents/architect/references/plan-authoring.md"];
        yield return ["products/kyber-squad/skills/product-owner/references/tasks-phase.md"];
    }

    [Theory]
    [MemberData(nameof(PlannerFiles))]
    public void EveryPlannerRequiresBothTaskLines(string relativePath)
    {
        string text = Corpus.ReadText(relativePath);

        Assert.Contains("Difficulty:", text, StringComparison.Ordinal);
        Assert.Contains("Technology:", text, StringComparison.Ordinal);
        Assert.Contains("easy", text, StringComparison.Ordinal);
        Assert.Contains("medium", text, StringComparison.Ordinal);
        Assert.Contains("hard", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PlannerFiles))]
    public void EveryPlannerCarriesTheSharedDifficultyRubric(string relativePath)
    {
        // The architect body sits near the Copilot agent-file cap, so it points at the rubric in its plan-authoring
        // reference instead of carrying a second copy.
        if (relativePath == "products/kyber-squad/agents/architect.md")
        {
            string architect = Corpus.ReadText(relativePath);
            Assert.Contains("**Difficulty** rubric in the [plan authoring](architect/references/plan-authoring.md)", architect, StringComparison.Ordinal);
            Assert.DoesNotContain("pick the higher", architect, StringComparison.OrdinalIgnoreCase);
            return;
        }

        // One rubric, products/kyber-squad/shared/difficulty-rubric.md, rates every task: the planners carry it word for
        // word and the decision model's agreement check reads the same file, so the two judge by the same text.
        string rubric = Collapse(Corpus.ReadText("products/kyber-squad/shared/difficulty-rubric.md").Split("-->", 2)[1]);
        string text = Collapse(Corpus.ReadText(relativePath));

        Assert.Contains(rubric, text, StringComparison.Ordinal);
        Assert.Contains("does not by itself raise the rating", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pick the lower", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("moves the task one tier up", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pick the higher", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [MemberData(nameof(PlannerFiles))]
    public void EveryPlannerSplitsTwoUnrelatedTechnologiesWithTheEndpointExample(string relativePath)
    {
        string text = Corpus.ReadText(relativePath);

        Assert.Contains("two tasks", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("endpoint", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("migration", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(PlannerFiles))]
    public void EveryPlannerFetchesAStandardThroughTheToolUnlessItIsUnavailable(string relativePath)
    {
        string text = Corpus.ReadText(relativePath);

        Assert.Contains("docs_get_coding_standard_for_technology", text, StringComparison.Ordinal);
        Assert.True(
            text.Contains("missing", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fails", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failure", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} must say what to do when the standards tool is missing or fails.");
    }
}
