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
    public void FlagsMissingDescription()
    {
        Skill skill = Make("---\nname: skill-x\n---\nbody");
        IEnumerable<string> codes = SpecValidator.Validate(skill).Select(d => d.Code);
        Assert.Contains("KW-SKILL-SPEC-005", codes);
    }

    [Fact]
    public void FlagsInvalidNameCharacters()
    {
        Skill skill = Make("---\nname: Bad_Name\ndescription: d\n---\nbody");
        IEnumerable<string> codes = SpecValidator.Validate(skill).Select(d => d.Code);
        Assert.Contains("KW-SKILL-SPEC-003", codes);
    }

    [Fact]
    public void FlagsAngleBracketsInFrontmatter()
    {
        Skill skill = Make("---\nname: skill-x\ndescription: use <tool> now\n---\nbody");
        IEnumerable<string> codes = SpecValidator.Validate(skill).Select(d => d.Code);
        Assert.Contains("KW-SKILL-SPEC-008", codes);
    }

    [Fact]
    public void ValidSkillHasNoErrors()
    {
        Skill skill = Make("---\nname: skill-x\ndescription: Use to do X. Use when Y. Do NOT use for Z.\n---\nbody");
        List<Diagnostic> diags = SpecValidator.Validate(skill).ToList();
        Assert.DoesNotContain(diags, d => d.Severity is Severity.Error or Severity.Critical);
    }
}

public class RoutingLinterTests
{
    private static Skill Make(string name, string description, string dir)
    {
        string content = $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\nbody";
        return SkillParser.Parse(content, $"{dir}/SKILL.md", dir);
    }

    [Fact]
    public void LowQualityDescriptionScoresBelowThreshold()
    {
        Skill skill = Make("helper", "Helps with stuff.", "/tmp/helper");
        DescriptionScore score = DescriptionScorer.Score(skill);
        Assert.True(score.Total < 70, $"expected < 70 but was {score.Total}");
    }

    [Fact]
    public void HighQualityDescriptionScoresHigh()
    {
        Skill skill = Make("password-reset",
            "Use to reset a corporate password. Use when a user is locked out. Do NOT use for service accounts or MFA enrollment.",
            "/tmp/password-reset");
        DescriptionScore score = DescriptionScorer.Score(skill);
        Assert.True(score.Total >= 70, $"expected >= 70 but was {score.Total}");
    }

    [Fact]
    public void DetectsNameCollision()
    {
        SkillSet set = new SkillSet(
        [
            Make("dup", "Use to do A. Use when A. Do NOT use for B.", "/tmp/a"),
            Make("dup", "Use to do C. Use when C. Do NOT use for D.", "/tmp/b")
        ]);
        IEnumerable<string> codes = new RoutingLinter().LintSet(set).Select(d => d.Code);
        Assert.Contains("KW-SKILL-LINT-010", codes);
    }

    [Fact]
    public void DetectsDescriptionOverlap()
    {
        SkillSet set = new SkillSet(
        [
            Make("reset-one", "Use to reset a corporate password when a user is locked out. Do NOT use for service accounts.", "/tmp/one"),
            Make("reset-two", "Use to reset a corporate password when a user is locked out. Do NOT use for service accounts.", "/tmp/two")
        ]);
        IEnumerable<string> codes = new RoutingLinter().LintSet(set).Select(d => d.Code);
        Assert.Contains("KW-SKILL-LINT-011", codes);
    }

    [Theory]
    [InlineData("Generates SQL queries, validates schema syntax, and connects to Postgres database.")]
    [InlineData("Handles user data synchronization across external CRM services.")]
    [InlineData("Processes incoming events from Kafka topics and writes aggregates.")]
    [InlineData("Calculates metrics for weekly developer velocity reports.")]
    [InlineData("Creates Kubernetes deployment manifests and Helm charts.")]
    [InlineData("Validates authentication tokens and manages OAuth session lifecycles.")]
    public void FlagsActionSummaryWithoutTrigger(string description)
    {
        Skill skill = Make("action-skill", description, "/tmp/action-skill");
        RoutingLinter linter = new RoutingLinter();

        List<Diagnostic> diags = linter.LintSkill(skill).ToList();

        Diagnostic? diag = diags.FirstOrDefault(d => d.Code == "KW-SKILL-LINT-007");
        Assert.NotNull(diag);
        Assert.Equal(Severity.Warning, diag.Severity);
    }

    [Theory]
    [InlineData("Use when querying PostgreSQL databases.")]
    [InlineData("Use when the user asks to inspect, write, or troubleshoot PostgreSQL queries.")]
    [InlineData("Invoke when an automated security scan reports critical vulnerabilities.")]
    [InlineData("Trigger when pull request validation fails on CI pipeline steps.")]
    [InlineData("Apply when formatting markdown tables according to repo standards.")]
    [InlineData("Use this skill when analyzing memory leaks in .NET applications.")]
    public void DoesNotFlagKWSKILLLINT007WhenExplicitTriggerClausePresent(string description)
    {
        Skill skill = Make("trigger-skill", description, "/tmp/trigger-skill");
        RoutingLinter linter = new RoutingLinter();

        List<Diagnostic> diags = linter.LintSkill(skill).ToList();

        Assert.DoesNotContain(diags, d => d.Code == "KW-SKILL-LINT-007");
    }

    [Theory]
    [InlineData("This skill is designed to allow the user to query Postgres databases.")]
    [InlineData("This tool is intended to help with generating API client code.")]
    [InlineData("In this skill we provide the functionality to automate build tasks.")]
    [InlineData("The purpose of this skill is to allow users to format markdown files.")]
    [InlineData("This skill can be used by the agent to triage pull requests.")]
    public void FlagsExcessiveFillerAndVerbosity(string description)
    {
        Skill skill = Make("filler-skill", description, "/tmp/filler-skill");
        RoutingLinter linter = new RoutingLinter();

        List<Diagnostic> diags = linter.LintSkill(skill).ToList();

        Diagnostic? diag = diags.FirstOrDefault(d => d.Code == "KW-SKILL-LINT-008");
        Assert.NotNull(diag);
        Assert.Equal(Severity.Warning, diag.Severity);
    }

    [Theory]
    [InlineData("Use when querying PostgreSQL databases. Do NOT use for database migrations.")]
    [InlineData("Use for triaging high-severity production alerts. Excludes routine maintenance.")]
    [InlineData("Invoke when security scan fails on pull requests.")]
    public void DoesNotFlagKWSKILLLINT008WhenConciseAndDirect(string description)
    {
        Skill skill = Make("concise-skill", description, "/tmp/concise-skill");
        RoutingLinter linter = new RoutingLinter();

        List<Diagnostic> diags = linter.LintSkill(skill).ToList();

        Assert.DoesNotContain(diags, d => d.Code == "KW-SKILL-LINT-008");
    }
}
