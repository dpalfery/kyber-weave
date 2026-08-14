using System.Text.Json;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies skill and agent description review candidate export and verdict import exchange.
/// </summary>
public sealed class SkillReviewTests
{
    private static Skill MakeSkill(string name, string description, string dir = "/tmp/skills")
    {
        var content = $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\nBody instructions.";
        return SkillParser.Parse(content, $"{dir}/{name}/SKILL.md", $"{dir}/{name}");
    }

    private static AgentModel MakeAgent(string roleName, string description, HarnessKind harness = HarnessKind.Claude)
    {
        return new AgentModel
        {
            RoleName = roleName,
            Harness = harness,
            FilePath = $"/tmp/.claude/agents/{roleName}.md",
            DirectoryPath = "/tmp/.claude/agents",
            Description = description,
            InstructionsBody = "Agent instructions."
        };
    }

    [Fact]
    public void ExportCandidates_FromSkills_ProducesCandidatesWithTriggerScoresAndHeuristicFlags()
    {
        var triggerSkill = MakeSkill(
            "postgres-query",
            "Use when querying PostgreSQL databases or troubleshooting SQL syntax. Do NOT use for database migrations.");
        var actionSkill = MakeSkill(
            "sql-generator",
            "Generates SQL queries, validates schema syntax, and connects to Postgres database.");
        var vagueSkill = MakeSkill(
            "general-helper",
            "Helps with stuff.");

        var skillSet = new SkillSet([triggerSkill, actionSkill, vagueSkill]);

        var result = SkillReviewExchange.ExportCandidates(skills: skillSet);

        Assert.NotNull(result);
        Assert.Equal("kyber-weave.skill-review.candidates/v1", result.Bundle.Schema);
        Assert.Equal(3, result.Bundle.Candidates.Count);

        var triggerCandidate = Assert.Single(result.Bundle.Candidates, c => c.Id == "postgres-query");
        Assert.Equal(SkillReviewCandidateType.Skill, triggerCandidate.Type);
        Assert.Equal(triggerSkill.Frontmatter.Description, triggerCandidate.CurrentDescription);
        Assert.True(triggerCandidate.TriggerScore >= 70, $"Expected trigger score >= 70, was {triggerCandidate.TriggerScore}");
        Assert.DoesNotContain("KW-SKILL-LINT-007", triggerCandidate.HeuristicFlags);

        var actionCandidate = Assert.Single(result.Bundle.Candidates, c => c.Id == "sql-generator");
        Assert.Equal(SkillReviewCandidateType.Skill, actionCandidate.Type);
        Assert.Equal(actionSkill.Frontmatter.Description, actionCandidate.CurrentDescription);
        Assert.Contains("KW-SKILL-LINT-007", actionCandidate.HeuristicFlags);

        using var parsedJson = JsonDocument.Parse(result.Json);
        Assert.Equal("kyber-weave.skill-review.candidates/v1", parsedJson.RootElement.GetProperty("schema").GetString());
        Assert.True(parsedJson.RootElement.GetProperty("candidates").GetArrayLength() == 3);
    }

    [Fact]
    public void ExportCandidates_FromAgents_ProducesCandidatesWithAgentTypeAndFlags()
    {
        var triggerAgent = MakeAgent(
            "schema-architect",
            "Use when designing SQL schemas, writing migrations, or optimizing database queries.");
        var actionAgent = MakeAgent(
            "data-engineer",
            "Designs database schemas and generates migrations.");

        var agentSet = new AgentSet([triggerAgent, actionAgent]);

        var result = SkillReviewExchange.ExportCandidates(agents: agentSet);

        Assert.NotNull(result);
        Assert.Equal("kyber-weave.skill-review.candidates/v1", result.Bundle.Schema);
        Assert.Equal(2, result.Bundle.Candidates.Count);

        var triggerCandidate = Assert.Single(result.Bundle.Candidates, c => c.Id == "schema-architect");
        Assert.Equal(SkillReviewCandidateType.Agent, triggerCandidate.Type);
        Assert.Equal(triggerAgent.Description, triggerCandidate.CurrentDescription);
        Assert.DoesNotContain("KW-AGENT-LINT-002", triggerCandidate.HeuristicFlags);

        var actionCandidate = Assert.Single(result.Bundle.Candidates, c => c.Id == "data-engineer");
        Assert.Equal(SkillReviewCandidateType.Agent, actionCandidate.Type);
        Assert.Equal(actionAgent.Description, actionCandidate.CurrentDescription);
        Assert.Contains("KW-AGENT-LINT-002", actionCandidate.HeuristicFlags);
    }

    [Fact]
    public void ExportCandidates_CombinedSkillsAndAgents_IncludesBothInSingleBundle()
    {
        var skill = MakeSkill("test-skill", "Use when testing application logic.");
        var agent = MakeAgent("test-agent", "Use when orchestrating unit tests.");

        var skillSet = new SkillSet([skill]);
        var agentSet = new AgentSet([agent]);

        var result = SkillReviewExchange.ExportCandidates(skillSet, agentSet);

        Assert.NotNull(result);
        Assert.Equal("kyber-weave.skill-review.candidates/v1", result.Bundle.Schema);
        Assert.Equal(2, result.Bundle.Candidates.Count);
        Assert.Contains(result.Bundle.Candidates, c => c.Id == "test-skill" && c.Type == SkillReviewCandidateType.Skill);
        Assert.Contains(result.Bundle.Candidates, c => c.Id == "test-agent" && c.Type == SkillReviewCandidateType.Agent);
    }

    [Fact]
    public void ExportCandidates_EmptyInputs_ReturnsEmptyBundle()
    {
        var result = SkillReviewExchange.ExportCandidates(new SkillSet([]), new AgentSet([]));

        Assert.NotNull(result);
        Assert.Equal("kyber-weave.skill-review.candidates/v1", result.Bundle.Schema);
        Assert.Empty(result.Bundle.Candidates);
    }

    [Fact]
    public void ImportVerdicts_ValidVerdicts_ParsesAndValidatesAllFieldsSuccessfully()
    {
        var candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"]),
            new("schema-architect", SkillReviewCandidateType.Agent, "Use when designing SQL schemas.", 85, [])
        };

        var verdictJson = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": [
                {
                    "candidate_id": "sql-generator",
                    "is_trigger_oriented": false,
                    "confidence": 0.95,
                    "suggested_trigger_description": "Use when writing or optimizing PostgreSQL queries.",
                    "rationale": "Description was an action summary without trigger framing."
                },
                {
                    "candidate_id": "schema-architect",
                    "is_trigger_oriented": true,
                    "confidence": 0.90,
                    "suggested_trigger_description": null,
                    "rationale": "Already states trigger conditions clearly."
                }
            ]
        }
        """;

        var result = SkillReviewExchange.ImportVerdicts(verdictJson, candidates);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(2, result.Verdicts.Count);

        var firstVerdict = result.Verdicts[0];
        Assert.Equal("sql-generator", firstVerdict.CandidateId);
        Assert.False(firstVerdict.IsTriggerOriented);
        Assert.Equal(0.95, firstVerdict.Confidence);
        Assert.Equal("Use when writing or optimizing PostgreSQL queries.", firstVerdict.SuggestedTriggerDescription);
        Assert.Equal("Description was an action summary without trigger framing.", firstVerdict.Rationale);

        var secondVerdict = result.Verdicts[1];
        Assert.Equal("schema-architect", secondVerdict.CandidateId);
        Assert.True(secondVerdict.IsTriggerOriented);
        Assert.Equal(0.90, secondVerdict.Confidence);
        Assert.Null(secondVerdict.SuggestedTriggerDescription);
    }

    [Fact]
    public void ImportVerdicts_MalformedJson_FailsWithDiagnosticError()
    {
        var candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        var malformedJson = "{ \"schema\": \"kyber-weave.skill-review.verdicts/v1\", invalid json syntax";

        var result = SkillReviewExchange.ImportVerdicts(malformedJson, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001" && d.Severity == Severity.Error);
    }

    [Fact]
    public void ImportVerdicts_MismatchedCandidateId_RejectsImportWithDiagnosticError()
    {
        var candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        var mismatchedJson = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": [
                {
                    "candidate_id": "non-existent-candidate",
                    "is_trigger_oriented": false,
                    "confidence": 0.95,
                    "suggested_trigger_description": "Use when..."
                }
            ]
        }
        """;

        var result = SkillReviewExchange.ImportVerdicts(mismatchedJson, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001" && d.Severity == Severity.Error);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void ImportVerdicts_ConfidenceOutOfRange_RejectsImport(double invalidConfidence)
    {
        var candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        var json = $$"""
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": [
                {
                    "candidate_id": "sql-generator",
                    "is_trigger_oriented": false,
                    "confidence": {{invalidConfidence}},
                    "suggested_trigger_description": "Use when..."
                }
            ]
        }
        """;

        var result = SkillReviewExchange.ImportVerdicts(json, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001");
    }

    [Fact]
    public void ImportVerdicts_UnsupportedSchema_RejectsImport()
    {
        var candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        var json = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v999",
            "verdicts": [
                {
                    "candidate_id": "sql-generator",
                    "is_trigger_oriented": false,
                    "confidence": 0.9,
                    "suggested_trigger_description": "Use when..."
                }
            ]
        }
        """;

        var result = SkillReviewExchange.ImportVerdicts(json, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001");
    }

    [Fact]
    public void ImportVerdicts_DuplicateCandidateIdsInBundle_RejectsImport()
    {
        var candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        var json = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": [
                {
                    "candidate_id": "sql-generator",
                    "is_trigger_oriented": false,
                    "confidence": 0.9,
                    "suggested_trigger_description": "Use when..."
                },
                {
                    "candidate_id": "sql-generator",
                    "is_trigger_oriented": true,
                    "confidence": 0.85,
                    "suggested_trigger_description": null
                }
            ]
        }
        """;

        var result = SkillReviewExchange.ImportVerdicts(json, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001");
    }

    [Fact]
    public void ImportVerdicts_EmptyVerdictsList_RejectsImport()
    {
        var candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        var json = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": []
        }
        """;

        var result = SkillReviewExchange.ImportVerdicts(json, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001");
    }
}
