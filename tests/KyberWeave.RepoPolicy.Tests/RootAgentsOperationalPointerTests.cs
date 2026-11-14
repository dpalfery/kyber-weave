using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the operational pointers and tokens the root <c>AGENTS.md</c> slim must keep: the .NET
/// gate argv lives in the review catalogue, the staged and unstaged
/// diffs CodeGraph init records, the KyberDash rows' paths, and the area documents the Commands
/// section points at.
/// </summary>
/// <remarks>
/// Assertions match stable tokens in the hand-authored sections, not the generated Config Reg
/// block. The rules those sections state in prose (the display layers,
/// the full-suite ban, the quick-review threshold) are checked by the prose rule catalog,
/// <c>tests/KyberWeave.Tests/Fixtures/prose-rules/catalog.yml</c>.
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

    /// <summary>
    /// A <c>dash/web</c> edit that follows only the architecture row never reads the
    /// display-layer rule, so isolated feature logic can ship in the display layer.
    /// </summary>
    [Fact]
    public void EveryKyberDashRowNamesTheDisplayLayerPaths()
    {
        string next = Section(ReadAgents(), "## Where to go next", "## House style");
        int rows = 0;
        foreach (string line in next.Split('\n'))
        {
            if (!line.StartsWith("| KyberDash", StringComparison.Ordinal))
            {
                continue;
            }

            rows++;
            Assert.True(
                line.Contains("`dash/web`", StringComparison.Ordinal)
                    && line.Contains("`dash/tray`", StringComparison.Ordinal),
                "Each KyberDash row must name dash/web and dash/tray: " + line);
            Assert.True(
                line.Contains("docs/rules/kyberdash-display-layer.md", StringComparison.Ordinal),
                "Each KyberDash row must point at docs/rules/kyberdash-display-layer.md: " + line);
            Assert.True(
                line.Contains("dash/AGENTS.md", StringComparison.Ordinal),
                "Each KyberDash row must point at dash/AGENTS.md: " + line);
        }

        Assert.True(rows >= 1, "The Where to go next table has no KyberDash row.");
    }

    /// <summary>
    /// Only the review council and CI run the full suite. A bare <c>review gates .</c> is one of
    /// those runs: its <c>test</c> gate has no filter, so any .NET change runs the whole product
    /// suite. The ban itself is a prose rule in the catalog; this case keeps the commands out.
    /// </summary>
    [Fact]
    public void TheCommandsSectionHandsDevAgentsNoFullRunCommand()
    {
        // Flattened so a re-wrap of the paragraph cannot break a phrase across two lines.
        string commands = System.Text.RegularExpressions.Regex.Replace(
            Section(ReadAgents(), "## Commands", "## Exploration order"), @"\s+", " ");

        Assert.False(
            commands.Contains("review gates . --out", StringComparison.Ordinal),
            "The Commands section must not hand dev agents a command that runs the test gate.");
    }

    /// <summary>
    /// The script prints its last line to every agent that used it, so advice there is an
    /// instruction. And <c>-class</c> matches the fully qualified name, so <c>Docs*</c> runs nothing.
    /// </summary>
    [Fact]
    public void FastLoopNeitherOffersAFullRunNorDocumentsAClassPatternThatMatchesNothing()
    {
        string script = File.ReadAllText(
            Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "fast-loop.sh"));

        Assert.False(
            script.Contains("Use --full only for", StringComparison.Ordinal),
            "fast-loop.sh must not end a clean run by offering --full.");
        Assert.False(
            script.Contains("--class Docs*", StringComparison.Ordinal),
            "fast-loop.sh must not document a --class pattern with no namespace.");
    }

    /// <summary>
    /// The root file points at these documents for the commands instead of carrying them, so
    /// a command dropped from one of them leaves an empty target.
    /// </summary>
    [Theory]
    [InlineData("docs/standards/test/README.md", "FullyQualifiedName~")]
    [InlineData("tests/KyberWeave.RepoPolicy.Tests/AGENTS.md", "FullyQualifiedName~")]
    [InlineData("dash/AGENTS.md", "test:related")]
    [InlineData("dash/tray/AGENTS.md", "cargo test --manifest-path dash/tray/src-tauri/Cargo.toml '")]
    public void EachAreaDocumentHoldsTheTargetedCommandTheRootFilePointsAt(string relativePath, string command)
    {
        string commands = Section(ReadAgents(), "## Commands", "## Exploration order");
        string target = File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, relativePath));

        Assert.True(
            commands.Contains(relativePath, StringComparison.Ordinal),
            $"The Commands section must point at {relativePath}.");
        Assert.True(
            target.Contains(command, StringComparison.Ordinal),
            $"{relativePath} must hold the targeted command '{command}'.");
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
