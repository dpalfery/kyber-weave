using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins three operational facts the root <c>AGENTS.md</c> slim must keep: the .NET gate argv
/// lives in the review catalogue, InspectCode's exit code is not the report, and CodeGraph
/// init is preceded by both staged and unstaged diffs.
/// </summary>
/// <remarks>
/// Assertions match stable tokens in the hand-authored sections, not the generated Config Reg
/// block, so a wording pass can move a sentence without hiding a dropped constraint.
/// </remarks>
public sealed class RootAgentsOperationalPointerTests
{
    private static string AgentsPath =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "AGENTS.md");

    /// <summary>
    /// An agent told that restore, format, build, test, and skill commands live on the CLI
    /// command surface follows a page of <c>kyber-weave</c> verbs and never finds the gate argv.
    /// The Commands section has to name <c>review.gates</c> in the catalogue that actually lists them.
    /// </summary>
    [Fact]
    public void FormatBuildTestAndSkillGatesPointAtTheReviewCatalogue()
    {
        string commands = Section(ReadAgents(), "## Commands", "## Exploration order");

        Assert.True(
            commands.Contains(".kyber-weave/kyber-weave.yml", StringComparison.Ordinal),
            "The Commands section must point at .kyber-weave/kyber-weave.yml, where review.gates lists the argv.");
        Assert.True(
            commands.Contains("review.gates", StringComparison.Ordinal),
            "The Commands section must name review.gates as the list of format, build, test, and skill argv.");
        Assert.False(
            commands.Contains("cli-command-surface.md", StringComparison.Ordinal),
            "The Commands section must not send format/build/test/skill argv to docs/cli-command-surface.md.");

        string catalogue = File.ReadAllText(
            Path.Combine(KyberWeaveTestPaths.ToolRoot, ".kyber-weave", "kyber-weave.yml"));
        foreach (string gateId in new[] { "id: format-whitespace", "id: format-style", "id: build", "id: test", "id: skill-validate" })
        {
            Assert.True(
                catalogue.Contains(gateId, StringComparison.Ordinal),
                $"review.gates is missing '{gateId}', so the AGENTS.md pointer would be another empty target.");
        }
    }

    /// <summary>
    /// InspectCode exits zero with findings still in the XML. An agent that treats that exit
    /// code as clean never hands <c>artifacts/inspectcode.xml</c> to the triage lens.
    /// </summary>
    [Fact]
    public void InspectCodeExitZeroIsNotACleanReport()
    {
        string commands = Section(ReadAgents(), "## Commands", "## Exploration order");

        Assert.True(
            commands.Contains("exits zero whether or not it finds anything", StringComparison.Ordinal),
            "The Commands section must say InspectCode exits zero whether or not it finds anything.");
        Assert.True(
            commands.Contains("artifacts/inspectcode.xml", StringComparison.Ordinal),
            "The Commands section must name artifacts/inspectcode.xml as the InspectCode result.");
    }

    /// <summary>
    /// <c>codegraph init</c> can rewrite the marked block. Recording only <c>git diff</c> misses
    /// staged edits, and the undo step then attributes them to init or drops them.
    /// </summary>
    [Fact]
    public void CodeGraphInitRecordsStagedAndUnstagedTrackedDiffs()
    {
        string exploration = Section(ReadAgents(), "## Exploration order", "## Non-negotiables");

        Assert.True(
            exploration.Contains("`git diff --cached`", StringComparison.Ordinal),
            "The CodeGraph init instructions must name the staged capture `git diff --cached`.");
        Assert.True(
            exploration.Contains("`git diff`", StringComparison.Ordinal),
            "The CodeGraph init instructions must name the unstaged capture `git diff`.");
        Assert.True(
            exploration.Contains("git status --short", StringComparison.Ordinal),
            "The CodeGraph init instructions must still record git status --short before init.");
    }

    private static string ReadAgents()
    {
        Assert.True(File.Exists(AgentsPath), $"Root instruction file '{AgentsPath}' is missing.");
        return File.ReadAllText(AgentsPath);
    }

    private static string Section(string text, string startHeading, string endHeading)
    {
        int start = text.IndexOf(startHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"AGENTS.md is missing the heading '{startHeading}'.");

        int end = text.IndexOf(endHeading, start + startHeading.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"AGENTS.md is missing the heading '{endHeading}' after '{startHeading}'.");

        return text[start..end];
    }
}
