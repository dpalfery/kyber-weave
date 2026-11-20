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
    public void EveryPlannerGivesOneLineOfGuidanceForEachDifficulty(string relativePath)
    {
        string text = Corpus.ReadText(relativePath);

        Assert.Contains("an existing pattern covers it", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the plan leaves open", text, StringComparison.OrdinalIgnoreCase);
        // Hard is for designing security or concurrency; applying an existing pattern of either is never hard.
        Assert.Contains("the worker designs security or concurrency", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cache busting", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never `hard`", text, StringComparison.OrdinalIgnoreCase);
        // Size is not difficulty: a rating that rose with the file count sent routine work to the dearest tier.
        Assert.Contains("does not by itself raise the rating", text, StringComparison.OrdinalIgnoreCase);
        // The tie-break: guess low, because a failed attempt steps the task up. No exception sends a guess high: an
        // authorization attribute copied from a neighbour sent a routine endpoint to the dearest tier.
        Assert.Contains("pick the lower", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("moves the task one tier up", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pick the higher", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("follows one existing pattern", text, StringComparison.OrdinalIgnoreCase);
    }

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
