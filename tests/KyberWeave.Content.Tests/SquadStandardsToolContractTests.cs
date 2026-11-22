using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T19 RED — every developer agent reaches a coding standard through
/// <c>docs_get_coding_standard_for_technology</c> instead of being told to read a path.
/// </summary>
/// <remarks>
/// <para>
/// Reading the path costs a worker the whole standard. <c>docs/standards/test/README.md</c> is
/// 21,354 characters and the tool's default budget is 12,000, so a worker that reads the file
/// spends context the budget exists to bound — and a worker whose profile grants no file read
/// cannot reach the standard at all. The tool answers with the sections the task names, and says
/// what it left out.
/// </para>
/// <para>
/// That a file read is not presented as the way to get the standard is graded from the prose
/// rule catalog.
/// </para>
/// </remarks>
public sealed class SquadStandardsToolContractTests
{
    private const string ToolName = "docs_get_coding_standard_for_technology";

    private const string ProductRoot = "products/kyber-squad";

    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>
    /// A developer agent reaches its stack's standard through the tool, so the standard arrives
    /// with the sections the task names and within a budget rather than as a whole file.
    /// </summary>
    /// <param name="agentName">The canonical agent identity.</param>
    [Theory]
    [InlineData("dev-easy")]
    [InlineData("dev-hard")]
    [InlineData("dev-medium")]
    public void EveryDeveloperAgentReachesItsCodingStandardThroughTheTool(string agentName)
    {
        AssertRoutesThroughTheTool($"agents/{agentName}.md");
    }

    /// <summary>
    /// The delivery agent names no platform of its own, so it asks for the one its task names through
    /// the tool rather than embedding a path to it.
    /// </summary>
    [Fact]
    public void DevopsAsksForItsPlatformStandardThroughTheTool()
    {
        string text = Corpus.ReadText($"{ProductRoot}/agents/devops.md");

        Assert.Contains(ToolName, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A routing instruction names the tool. What it says about reading the file instead is graded
    /// from the prose rule catalog (<c>standards-via-tool</c>).
    /// </summary>
    private static void AssertRoutesThroughTheTool(string relativePath)
    {
        string path = $"{ProductRoot}/{relativePath}";
        Assert.True(Corpus.Exists(path), $"Expected the canonical product file '{relativePath}' at '{path}'.");

        string text = Corpus.ReadText(path);
        Assert.True(
            text.Contains(ToolName, StringComparison.Ordinal),
            $"'{relativePath}' must reach its coding standard through {ToolName}; it does not name it.");
    }
}
