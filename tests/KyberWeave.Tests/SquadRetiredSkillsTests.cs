using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A receipt path that merely contains a retired skill name as a substring is not
/// retained retired state: only a full path segment counts.
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
            InstalledAtUtc: DateTimeOffset.UtcNow,
            Degradations: [],
            Files: [new SquadOwnedFile("skills/my-second-brainstorm/notes.md", "0".PadRight(64, '0'), "claude", Adopted: false)]);

        Assert.Empty(SquadRetiredSkills.FindRetainedReceiptPaths(receipt));
    }
}
