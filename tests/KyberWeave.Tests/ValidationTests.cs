using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Validation;
using Xunit;

namespace KyberWeave.Tests;

public class SpecValidatorTests
{
    private static Skill Make(string content, string dir = "/tmp/skill-x") =>
        SkillParser.Parse(content, $"{dir}/SKILL.md", dir);

    [Fact]
    public void Flags_missing_description()
    {
        var skill = Make("---\nname: skill-x\n---\nbody");
        var codes = SpecValidator.Validate(skill).Select(d => d.Code);
        Assert.Contains("KW-SKILL-SPEC-005", codes);
    }

    [Fact]
    public void Flags_invalid_name_characters()
    {
        var skill = Make("---\nname: Bad_Name\ndescription: d\n---\nbody");
        var codes = SpecValidator.Validate(skill).Select(d => d.Code);
        Assert.Contains("KW-SKILL-SPEC-003", codes);
    }

    [Fact]
    public void Flags_angle_brackets_in_frontmatter()
    {
        var skill = Make("---\nname: skill-x\ndescription: use <tool> now\n---\nbody");
        var codes = SpecValidator.Validate(skill).Select(d => d.Code);
        Assert.Contains("KW-SKILL-SPEC-008", codes);
    }

    [Fact]
    public void Valid_skill_has_no_errors()
    {
        var skill = Make("---\nname: skill-x\ndescription: Use to do X. Use when Y. Do NOT use for Z.\n---\nbody");
        var diags = SpecValidator.Validate(skill).ToList();
        Assert.DoesNotContain(diags, d => d.Severity is Severity.Error or Severity.Critical);
    }
}

public class RoutingLinterTests
{
    private static Skill Make(string name, string description, string dir)
    {
        var content = $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\nbody";
        return SkillParser.Parse(content, $"{dir}/SKILL.md", dir);
    }

    [Fact]
    public void Low_quality_description_scores_below_threshold()
    {
        var skill = Make("helper", "Helps with stuff.", "/tmp/helper");
        var score = DescriptionScorer.Score(skill);
        Assert.True(score.Total < 70, $"expected < 70 but was {score.Total}");
    }

    [Fact]
    public void High_quality_description_scores_high()
    {
        var skill = Make("password-reset",
            "Use to reset a corporate password. Use when a user is locked out. Do NOT use for service accounts or MFA enrollment.",
            "/tmp/password-reset");
        var score = DescriptionScorer.Score(skill);
        Assert.True(score.Total >= 70, $"expected >= 70 but was {score.Total}");
    }

    [Fact]
    public void Detects_name_collision()
    {
        var set = new SkillSet(new[]
        {
            Make("dup", "Use to do A. Use when A. Do NOT use for B.", "/tmp/a"),
            Make("dup", "Use to do C. Use when C. Do NOT use for D.", "/tmp/b")
        });
        var codes = new RoutingLinter().LintSet(set).Select(d => d.Code);
        Assert.Contains("KW-SKILL-LINT-010", codes);
    }

    [Fact]
    public void Detects_description_overlap()
    {
        var set = new SkillSet(new[]
        {
            Make("reset-one", "Use to reset a corporate password when a user is locked out. Do NOT use for service accounts.", "/tmp/one"),
            Make("reset-two", "Use to reset a corporate password when a user is locked out. Do NOT use for service accounts.", "/tmp/two")
        });
        var codes = new RoutingLinter().LintSet(set).Select(d => d.Code);
        Assert.Contains("KW-SKILL-LINT-011", codes);
    }
}
