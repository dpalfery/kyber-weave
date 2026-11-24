using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Scaffolding;

/// <summary>
/// The standards-template reader takes the embedded-resource seam, so a unit test seeds the template text and asserts
/// what it reads and renders, without a build or a shipped resource anywhere in the test.
/// </summary>
public sealed class KyberStandardsTemplateReaderTests
{
    private const string Standard = "Standards.csharp.standards.md";
    private const string ArbiterRules = "Standards.csharp.arbiter-rules.yml";
    private const string SharedRules = "Standards.shared-rules.md";

    private const string CSharpStandard =
        """
        ---
        id: standards/csharp
        title: csharp coding standard
        owner: unassigned
        last-reviewed: 2026-01-01
        ---

        # csharp coding standard

        How csharp code is written here.
        """;

    private readonly FakeEmbeddedResources _resources = new();

    private KyberStandardsTemplateReader Reader => new(_resources);

    [Fact]
    public void TryGetReadsTheStandardTheBuildShips()
    {
        _resources.Add(Standard, CSharpStandard);

        bool found = Reader.TryGet("csharp", out string? content);

        Assert.True(found);
        Assert.Equal(CSharpStandard, content);
    }

    [Fact]
    public void TryGetReportsNoTemplateForAFolderTheBuildDoesNotShip()
    {
        bool found = Reader.TryGet("csharp", out string? content);

        Assert.False(found);
        Assert.Null(content);
    }

    [Fact]
    public void TryGetMatchesTheSlugWhateverItsCasing()
    {
        _resources.Add(Standard, CSharpStandard);

        bool found = Reader.TryGet("CSharp", out string? content);

        Assert.True(found);
        Assert.Equal(CSharpStandard, content);
    }

    [Fact]
    public void TryGetPartReadsTheFileThatPartNames()
    {
        const string rules = "# Machine rules the Arbiter evaluates.\nrules: []\n";
        _resources.Add(ArbiterRules, rules);

        bool found = Reader.TryGetPart("csharp", KyberStandardsTemplates.Part.ArbiterRules, out string? content);

        Assert.True(found);
        Assert.Equal(rules, content);
    }

    [Fact]
    public void TryGetPartReportsNoFileForAPartTheFolderDoesNotShip()
    {
        _resources.Add(Standard, CSharpStandard);

        bool found = Reader.TryGetPart("csharp", KyberStandardsTemplates.Part.Skills, out string? content);

        Assert.False(found);
        Assert.Null(content);
    }

    [Fact]
    public void TryRenderReplacesTheOwnerAndDateAndLeavesTheBodyIntact()
    {
        _resources.Add(Standard, CSharpStandard);

        bool renderedFound = Reader.TryRender("csharp", "alice", "2026-02-02", out string? rendered);

        Assert.True(renderedFound);
        Assert.Contains("owner: 'alice'", rendered, StringComparison.Ordinal);
        Assert.Contains("last-reviewed: '2026-02-02'", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("owner: unassigned", rendered, StringComparison.Ordinal);
        Assert.Contains("How csharp code is written here.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRenderPartLeavesArbiterRulesWithoutFrontmatterUnchanged()
    {
        const string rules = "# Machine rules the Arbiter evaluates.\nrules: []\n";
        _resources.Add(ArbiterRules, rules);

        bool renderedFound = Reader.TryRenderPart(
            "csharp", KyberStandardsTemplates.Part.ArbiterRules, "alice", "2026-02-02", out string? rendered);

        Assert.True(renderedFound);
        Assert.Equal(rules, rendered);
    }

    [Fact]
    public void TryRenderSharedRulesReadsTheRulesThatSitBesideTheFolders()
    {
        _resources.Add(
            SharedRules,
            """
            ---
            owner: unassigned
            last-reviewed: 2026-01-01
            ---

            Every task reads these rules.
            """);

        bool renderedFound = Reader.TryRenderSharedRules("alice", "2026-02-02", out string? rendered);

        Assert.True(renderedFound);
        Assert.Contains("owner: 'alice'", rendered, StringComparison.Ordinal);
        Assert.Contains("Every task reads these rules.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRenderSharedRulesReportsNoFileWhenTheBuildShipsNone()
    {
        bool renderedFound = Reader.TryRenderSharedRules("alice", "2026-02-02", out string? rendered);

        Assert.False(renderedFound);
        Assert.Null(rendered);
    }

    [Fact]
    public void RenderNamesTheNearestFolderWhenTheSlugIsMisspelled()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => Reader.Render("cshar", "owner", "2026-01-01"));

        Assert.Equal("technology", exception.ParamName);
        Assert.Contains("Did you mean 'csharp'?", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderPartRefusesAFolderWithNoTemplate()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => Reader.RenderPart("csharp", KyberStandardsTemplates.Part.Standard, "owner", "2026-01-01"));

        Assert.Equal("technology", exception.ParamName);
        Assert.Contains("Unknown technology 'csharp'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadShippedSkillNamesTheFileTheBuildDoesNotShip()
    {
        _resources.Add(
            "KwSkills/skills.manifest.yml",
            """
            skills:
              - name: shipped-one
                files:
                  - SKILL.md
                  - references/ref.md
            """);
        _resources.Add("KwSkills/shipped-one/SKILL.md", "---\nname: shipped-one\ndescription: One.\n---\n\nBody\n");

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => Reader.TryReadShippedSkill("shipped-one", out _));

        Assert.Contains("shipped-one", error.Message, StringComparison.Ordinal);
        Assert.Contains("references/ref.md", error.Message, StringComparison.Ordinal);
        Assert.Contains("does not ship", error.Message, StringComparison.Ordinal);
    }
}
