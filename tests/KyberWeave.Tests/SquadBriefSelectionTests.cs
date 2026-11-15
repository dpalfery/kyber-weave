using KyberWeave.Core.Squad.Briefs;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Which sections of a reference file a brief keeps: the always, path, autonomy and mode conditions, the
///     tolerant heading match, and the rule that nothing the map does not mention is ever dropped.
/// </summary>
public sealed class SquadBriefSelectionTests : IDisposable
{
    private const string Reference = """
        # Plan Path

        Intro.

        ## Run start

        Start text.

        ## Autonomous behaviour

        Accept the recommendation.

        ### Nested detail

        Only meaningful under autonomous.

        ## Hard stops

        Stop text.

        ## Brand New Section

        Added after the map was written.

        ```text
        ## Not a heading
        ```
        """;

    private const string MapYaml = """
        schema: kyber-squad.briefs/v1
        agents:
          conductor:
            - file: plan-path.md
              when: path=plan
              sections:
                Plan Path: always
                Run start: always
                autonomous behaviour!: autonomy=autonomous
                Hard stops: autonomy=autonomous
                A heading that was renamed: always
        """;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Install(string name, string text, string? agentDirectory = null)
    {
        string directory = agentDirectory ?? _temp.Path;
        File.WriteAllText(Path.Combine(directory, name), text);
        return directory;
    }

    private BriefResult Assemble(BriefFacts facts, string? text = null)
    {
        string directory = Install("plan-path.md", text ?? Reference);
        return BriefAssembler.Assemble(BriefMap.Parse(MapYaml), "conductor", facts, directory);
    }

    [Fact]
    public void AlwaysSectionsStayAndAutonomousOnesGoWhenSupervised()
    {
        BriefPart part = Assert.Single(Assemble(new BriefFacts("plan", "supervised", null)).Parts);

        Assert.Contains("## Run start", part.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Accept the recommendation.", part.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop text.", part.Text, StringComparison.Ordinal);
        Assert.Equal(["Autonomous behaviour", "Hard stops"], part.LeftOut.Select(item => item.Heading));
    }

    [Fact]
    public void AutonomousKeepsTheAutonomousSections()
    {
        BriefPart part = Assert.Single(Assemble(new BriefFacts("plan", "autonomous", null)).Parts);

        Assert.Contains("Accept the recommendation.", part.Text, StringComparison.Ordinal);
        Assert.Contains("Stop text.", part.Text, StringComparison.Ordinal);
        Assert.Empty(part.LeftOut);
    }

    [Fact]
    public void ASectionCarriesItsSubheadingsOut()
    {
        BriefPart part = Assert.Single(Assemble(new BriefFacts("plan", "supervised", null)).Parts);

        Assert.DoesNotContain("Nested detail", part.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Only meaningful under autonomous.", part.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileForAnotherPathIsLeftOutWhole()
    {
        BriefPart part = Assert.Single(Assemble(new BriefFacts("spec", "autonomous", null)).Parts);

        Assert.Equal("path=plan", part.WholeFileLeftOut);
        Assert.Equal(string.Empty, part.Text);
    }

    [Fact]
    public void AnUnknownFactKeepsEverythingThatDependsOnIt()
    {
        BriefResult unknownPath = Assemble(new BriefFacts(null, "supervised", null));
        BriefResult unknownAutonomy = Assemble(new BriefFacts("plan", null, null));

        Assert.Null(unknownPath.Parts[0].WholeFileLeftOut);
        Assert.Empty(unknownAutonomy.Parts[0].LeftOut);
        Assert.Contains("Stop text.", unknownAutonomy.Parts[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ModeSelectsBetweenTheTwoContracts()
    {
        BriefMap map = BriefMap.Parse("""
            schema: kyber-squad.briefs/v1
            agents:
              architect:
                - file: test-first-contract.md
                  when: mode=test-first
                  sections:
                    Test-First Contract: always
                - file: standard-verification.md
                  when: mode=standard
                  sections:
                    Standard Verification Contract: always
            """);
        Install("test-first-contract.md", "# Test-First Contract\n\nRED first.\n");
        Install("standard-verification.md", "# Standard Verification Contract\n\nVerify.\n");

        BriefResult standard = BriefAssembler.Assemble(map, "architect", new BriefFacts("plan", "supervised", "standard"), _temp.Path);

        Assert.Equal("mode=test-first", standard.Parts[0].WholeFileLeftOut);
        Assert.Null(standard.Parts[1].WholeFileLeftOut);
    }

    [Theory]
    [InlineData("Autonomous behaviour")]
    [InlineData("AUTONOMOUS BEHAVIOUR")]
    [InlineData("Autonomous  behaviour:")]
    [InlineData("**Autonomous behaviour**")]
    public void HeadingsMatchIgnoringCasePunctuationAndEmphasis(string heading)
    {
        string text = $"# Plan Path\n\n#### {heading}\n\nBody.\n";

        BriefPart part = Assert.Single(Assemble(new BriefFacts("plan", "supervised", null), text).Parts);

        Assert.DoesNotContain("Body.", part.Text, StringComparison.Ordinal);
        Assert.Contains(part.LeftOut, item => item.Heading == heading);
    }

    [Fact]
    public void AHeadingTheMapDoesNotMentionIsIncludedAndReported()
    {
        BriefPart part = Assert.Single(Assemble(new BriefFacts("plan", "supervised", null)).Parts);

        Assert.Contains("Added after the map was written.", part.Text, StringComparison.Ordinal);
        Assert.Contains("Brand New Section", part.Unmapped);
    }

    [Fact]
    public void AMappedHeadingTheFileLacksIsReported()
    {
        BriefPart part = Assert.Single(Assemble(new BriefFacts("plan", "supervised", null)).Parts);

        Assert.Equal(["A heading that was renamed"], part.MissingFromFile);
    }

    [Fact]
    public void AHeadingInsideACodeFenceIsText()
    {
        BriefPart part = Assert.Single(Assemble(new BriefFacts("plan", "supervised", null)).Parts);

        Assert.Contains("## Not a heading", part.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Not a heading", part.Unmapped);
    }

    [Fact]
    public void TheHeaderNamesTheFactsAndTheSectionsLeftOut()
    {
        BriefResult result = Assemble(new BriefFacts("plan", "supervised", null, new Dictionary<string, string> { ["autonomy"] = "AGENTS.md Config Reg" }));

        string text = BriefFormatter.ToText(result);
        string header = text.Split('\n')[0];

        Assert.Contains("autonomy=supervised (AGENTS.md Config Reg)", header, StringComparison.Ordinal);
        Assert.Contains("mode=unknown (all kept)", header, StringComparison.Ordinal);
        Assert.Contains("plan-path.md § Hard stops (autonomy=autonomous)", header, StringComparison.Ordinal);
        Assert.Contains("BRIEF diagnostics | unmapped, included: plan-path.md § Brand New Section", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingReferenceFileIsAnErrorThatNamesThePath()
    {
        string directory = Path.Combine(_temp.Path, "empty");
        Directory.CreateDirectory(directory);

        BriefMapException error = Assert.Throws<BriefMapException>(
            () => BriefAssembler.Assemble(BriefMap.Parse(MapYaml), "conductor", new BriefFacts("plan", null, null), directory));

        Assert.Contains(Path.Combine(directory, "plan-path.md"), error.Message, StringComparison.Ordinal);
        Assert.Contains("plan-path.markdown", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHarnessThatInstallsMarkdownAsMarkdownExtensionIsRead()
    {
        Install("plan-path.markdown", Reference);

        BriefResult result = BriefAssembler.Assemble(BriefMap.Parse(MapYaml), "conductor", new BriefFacts("plan", "autonomous", null), _temp.Path);

        Assert.EndsWith("plan-path.markdown", result.Parts[0].ReadFrom, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceFileBesideTheMappedOnesIsReportedNotDropped()
    {
        Install("plan-path.md", Reference);
        Install("brand-new-reference.md", "# New\n");

        BriefResult result = BriefAssembler.Assemble(BriefMap.Parse(MapYaml), "conductor", new BriefFacts("plan", null, null), _temp.Path);

        Assert.Equal(["brand-new-reference.md"], result.UnlistedFiles);
        Assert.Contains("read whole: brand-new-reference.md", BriefFormatter.ToText(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("path=sideways")]
    [InlineData("colour=red")]
    [InlineData("autonomy")]
    public void AnInvalidConditionIsRefusedWhenTheMapLoads(string condition)
    {
        string yaml = $"schema: kyber-squad.briefs/v1\nagents:\n  a:\n    - file: x.md\n      when: {condition}\n";

        Assert.Throws<BriefMapException>(() => BriefMap.Parse(yaml));
    }

    [Theory]
    [InlineData("autonomous", null, null, "autonomous", "prompt")]
    [InlineData(null, "- **<autonomy>**: `autonomous`", "supervised", "autonomous", "AGENTS.md Config Reg")]
    [InlineData(null, null, "autonomous", "autonomous", "configuration")]
    [InlineData(null, null, null, "supervised", "default")]
    [InlineData("nonsense", null, null, "supervised", "default")]
    public void AutonomyResolvesPromptThenRegistryThenConfigurationThenSupervised(
        string? prompt, string? agents, string? configured, string expected, string source)
    {
        (string value, string from) = BriefFacts.ResolveAutonomy(prompt, agents, configured);

        Assert.Equal((expected, source), (value, from));
    }

    [Fact]
    public void TheShippedMapLoadsAndDescribesBothAgents()
    {
        BriefMap map = BriefMap.Load();

        Assert.Contains("conductor", map.Agents);
        Assert.Contains("architect", map.Agents);
    }
}
