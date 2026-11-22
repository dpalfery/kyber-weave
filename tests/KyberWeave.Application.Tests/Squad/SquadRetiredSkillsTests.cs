using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A receipt path that merely contains a retired skill name as a substring is not
/// retained retired state: only a full path segment counts. Each retired skill also
/// carries the place its content moved to, which doctor reports with the warning.
/// </summary>
public sealed class SquadRetiredSkillsTests
{
    /// <summary>
    /// A lookalike directory (<c>my-second-brainstorm</c>) shares the retired
    /// name's characters but is a different skill, so it is not retained state.
    /// </summary>
    [Fact]
    public void LookalikeSegmentIsNotARetiredSkillPath()
    {
        Assert.False(SquadRetiredSkills.IsRetiredSkillPath("skills/my-second-brainstorm/notes.md"));
    }

    /// <summary>An exact retired skill segment still reports as a retired skill path.</summary>
    [Fact]
    public void ExactSegmentIsARetiredSkillPath()
    {
        Assert.True(SquadRetiredSkills.IsRetiredSkillPath("skills/second-brain/SKILL.md"));
    }

    /// <summary>
    /// A receipt holding only a lookalike path retains no retired files, so doctor
    /// would not warn about it.
    /// </summary>
    [Fact]
    public void LookalikeReceiptPathIsNotRetained()
    {
        SquadReceipt receipt = new(
            Schema: "kyber-squad.receipt/v1",
            Scope: SquadDeploymentScope.Project,
            TargetRoot: ".",
            InstalledAtUtc: new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero),
            Degradations: [],
            Files: [new SquadOwnedFile("skills/my-second-brainstorm/notes.md", "0".PadRight(64, '0'), "claude", Adopted: false)]);

        Assert.Empty(SquadRetiredSkills.FindRetainedReceiptPaths(receipt));
    }

    /// <summary>
    /// A retired technology skill resolves to the standards template its rules moved
    /// into, and the sentence names the command that adopts the template.
    /// </summary>
    /// <param name="name">The retired skill directory name.</param>
    /// <param name="technology">The standards template that now holds its rules.</param>
    [Theory]
    [InlineData("csharp-dev", "csharp")]
    [InlineData("dal-dev", "data-access-layer")]
    [InlineData("maui-dev", "maui")]
    [InlineData("typescript-dev", "typescript")]
    [InlineData("rust-dev", "rust")]
    [InlineData("python-dev", "python")]
    [InlineData("go-dev", "go")]
    [InlineData("csp-security", "csharp")]
    public void TechnologySkillNamesItsStandardsTemplate(string name, string technology)
    {
        SquadRetiredSkill skill = Assert.IsType<SquadRetiredSkill>(SquadRetiredSkills.Find(name));

        Assert.Contains($"`{technology}` standards template", skill.NewHome, StringComparison.Ordinal);
        Assert.Contains(
            $"kyber-weave docs init . --standards {technology}",
            skill.NewHome,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>github-devops</c> split across two templates, so its sentence names both and
    /// adopts both at once.
    /// </summary>
    [Fact]
    public void GitHubDevopsNamesBothOfItsStandardsTemplates()
    {
        SquadRetiredSkill skill = Assert.IsType<SquadRetiredSkill>(SquadRetiredSkills.Find("github-devops"));

        Assert.Contains("`github-actions` and `csharp` standards templates", skill.NewHome, StringComparison.Ordinal);
        Assert.Contains("--standards \"github-actions csharp\"", skill.NewHome, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lookalike technology skill name (<c>csharp-dev-extra</c>) is a different skill,
    /// so doctor does not treat it as retired state.
    /// </summary>
    [Fact]
    public void LookalikeTechnologySegmentIsNotARetiredSkillPath()
    {
        Assert.False(SquadRetiredSkills.IsRetiredSkillPath("skills/csharp-dev-extra/SKILL.md"));
    }

    /// <summary>A retained receipt path reports the skill whose directory it names.</summary>
    [Fact]
    public void RetainedTechnologyPathNamesItsRetiredSkill()
    {
        SquadReceipt receipt = new(
            Schema: "kyber-squad.receipt/v3",
            Scope: SquadDeploymentScope.Global,
            TargetRoot: "/home",
            InstalledAtUtc: new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero),
            Degradations: [],
            Files: [new SquadOwnedFile("skills/dal-dev/SKILL.md", "0".PadRight(64, '0'), "claude", Adopted: false)]);

        SquadRetiredSkillClaim claim = Assert.Single(SquadRetiredSkills.FindRetainedReceiptPaths(receipt));

        Assert.Equal("dal-dev", claim.Skill.Name);
        Assert.Equal("skills/dal-dev/SKILL.md", claim.RelativePath);
        Assert.Contains("`data-access-layer` standards template", claim.Skill.NewHome, StringComparison.Ordinal);
    }
}
