using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Validation;
using Xunit;

namespace KyberWeave.Tests;

public class DescriptionScorerTests
{
    private static Skill MakeSkill(string description, string name = "test-skill") =>
        new()
        {
            SkillFilePath = $"/tmp/{name}/SKILL.md",
            DirectoryPath = $"/tmp/{name}",
            Frontmatter = new SkillFrontmatter
            {
                Name = name,
                Description = description
            },
            RawFrontmatter = $"name: {name}\ndescription: {description}",
            InstructionsBody = "# Instructions\nExecute skill steps."
        };

    [Theory]
    [InlineData("Use when the user asks to inspect, write, or troubleshoot PostgreSQL queries.")]
    [InlineData("Use for triaging high-severity production alerts and incident response.")]
    [InlineData("Invoke when an automated security scan reports critical vulnerabilities.")]
    [InlineData("Trigger when pull request validation fails on CI pipeline steps.")]
    [InlineData("Apply when formatting markdown tables according to repo standards.")]
    [InlineData("Use this skill when analyzing memory leaks in .NET applications.")]
    public void ScoreWhenExplicitTriggerClausePresentAwardsFullTriggerScore(string description)
    {
        Skill skill = MakeSkill(description);

        DescriptionScore score = DescriptionScorer.Score(skill);
        ScoreComponent trigger = score.Components.First(c => c.Name == "Trigger clause");

        Assert.Equal(35, trigger.MaxPoints);
        Assert.Equal(35, trigger.Points);
    }

    [Theory]
    [InlineData("Generates SQL queries, validates schema syntax, and connects to Postgres database.")]
    [InlineData("Handles user data synchronization across external CRM services.")]
    [InlineData("Processes incoming events from Kafka topics and writes aggregates.")]
    [InlineData("Calculates metrics for weekly developer velocity reports.")]
    [InlineData("Creates Kubernetes deployment manifests and Helm charts.")]
    [InlineData("Validates authentication tokens and manages OAuth session lifecycles.")]
    [InlineData("Retrieves documents from vector database and reranks query results.")]
    [InlineData("Transforms legacy XML configuration files into modern YAML formats.")]
    [InlineData("Use this to generate architecture diagrams from C# models.")]
    public void ScoreWhenPureActionSummaryAwardsZeroTriggerScore(string description)
    {
        Skill skill = MakeSkill(description);

        DescriptionScore score = DescriptionScorer.Score(skill);
        ScoreComponent trigger = score.Components.First(c => c.Name == "Trigger clause");

        Assert.Equal(35, trigger.MaxPoints);
        Assert.Equal(0, trigger.Points);
    }

    [Theory]
    [InlineData("Use when managing database connections. Do NOT use for schema migrations.")]
    [InlineData("Use when deploying web apps. Not for local unit testing.")]
    [InlineData("Use when auditing IAM roles. Avoid using for routine user onboarding.")]
    [InlineData("Use when generating release notes. Never use for production hotfixes.")]
    [InlineData("Use when reviewing pull requests. Excludes automated dependency updates.")]
    [InlineData("Use when modifying configurations. Don't use for secret rotation.")]
    public void ScoreWhenNegativeBoundaryPresentAwardsFullBoundaryScore(string description)
    {
        Skill skill = MakeSkill(description);

        DescriptionScore score = DescriptionScorer.Score(skill);
        ScoreComponent boundary = score.Components.First(c => c.Name == "Negative boundary");

        Assert.Equal(20, boundary.MaxPoints);
        Assert.Equal(20, boundary.Points);
    }

    [Theory]
    [InlineData("Use when managing database connections and optimizing query performance.")]
    [InlineData("Use for triaging high-severity production alerts in Kubernetes clusters.")]
    public void ScoreWhenNegativeBoundaryMissingAwardsZeroBoundaryScore(string description)
    {
        Skill skill = MakeSkill(description);

        DescriptionScore score = DescriptionScorer.Score(skill);
        ScoreComponent boundary = score.Components.First(c => c.Name == "Negative boundary");

        Assert.Equal(20, boundary.MaxPoints);
        Assert.Equal(0, boundary.Points);
    }

    [Theory]
    [InlineData("USE WHEN the user asks to inspect PostgreSQL queries. DO NOT USE FOR database migrations.")]
    [InlineData("use when the user asks to inspect postgresql queries. do not use for database migrations.")]
    [InlineData("Use When the user asks to inspect PostgreSQL queries. Do Not Use For database migrations.")]
    [InlineData("INVOKE WHEN high-priority bugs are filed. AVOID USING FOR minor doc changes.")]
    [InlineData("TRIGGER WHEN deploy fails. NEVER USE FOR local tests.")]
    [InlineData("APPLY WHEN formatting text. EXCLUDES binary files.")]
    public void ScoreWhenDetectingTriggersAndBoundariesIsCaseInsensitive(string description)
    {
        Skill skill = MakeSkill(description);

        DescriptionScore score = DescriptionScorer.Score(skill);
        ScoreComponent trigger = score.Components.First(c => c.Name == "Trigger clause");
        ScoreComponent boundary = score.Components.First(c => c.Name == "Negative boundary");

        Assert.Equal(35, trigger.MaxPoints);
        Assert.Equal(35, trigger.Points);
        Assert.Equal(20, boundary.MaxPoints);
        Assert.Equal(20, boundary.Points);
    }

    [Fact]
    public void ScorePrioritizesTriggerQualityAndSemanticsOverPureWordLength()
    {
        // Concise, high-quality trigger-oriented description
        Skill triggerOriented = MakeSkill(
            "Use when resetting corporate passwords. Do NOT use for service accounts or MFA enrollment.",
            "password-reset");

        // Long action summary that describes execution details but lacks trigger conditions
        Skill actionSummary = MakeSkill(
            "Generates SQL queries, validates syntax against schema models, creates connection pools, handles network failover, processes incoming database requests, and formats tabular query results into JSON outputs for API consumers.",
            "sql-generator");

        DescriptionScore triggerScore = DescriptionScorer.Score(triggerOriented);
        DescriptionScore actionScore = DescriptionScorer.Score(actionSummary);

        ScoreComponent triggerClause = triggerScore.Components.First(c => c.Name == "Trigger clause");
        ScoreComponent actionClause = actionScore.Components.First(c => c.Name == "Trigger clause");

        Assert.Equal(35, triggerClause.Points);
        Assert.Equal(0, actionClause.Points);

        Assert.True(triggerScore.Total >= 70, $"Expected trigger-oriented score >= 70, but got {triggerScore.Total}");
        Assert.True(actionScore.Total < 70, $"Expected action-summary score < 70, but got {actionScore.Total}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ScoreWhenEmptyOrWhitespaceAwardsZero(string description)
    {
        Skill skill = MakeSkill(description);

        DescriptionScore score = DescriptionScorer.Score(skill);

        Assert.Equal(0, score.Total);
        Assert.All(score.Components, c => Assert.Equal(0, c.Points));
    }
}
