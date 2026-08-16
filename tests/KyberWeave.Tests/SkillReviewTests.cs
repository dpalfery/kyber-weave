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
        string content = $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\nBody instructions.";
        return SkillParser.Parse(content, $"{dir}/{name}/SKILL.md", $"{dir}/{name}");
    }

    private static AgentModel MakeAgent(string roleName, string description, HarnessKind harness = HarnessKind.Claude)
    {
        return new AgentModel
        {
            RoleName = roleName,
            Harness = harness,
            FilePath = $"/tmp/{harness}/agents/{roleName}.md",
            DirectoryPath = $"/tmp/{harness}/agents",
            Description = description,
            InstructionsBody = "Agent instructions."
        };
    }

    [Fact]
    public void ExportCandidatesFromSkillsProducesCandidatesWithTriggerScoresAndHeuristicFlags()
    {
        Skill triggerSkill = MakeSkill(
            "postgres-query",
            "Use when querying PostgreSQL databases or troubleshooting SQL syntax. Do NOT use for database migrations.");
        Skill actionSkill = MakeSkill(
            "sql-generator",
            "Generates SQL queries, validates schema syntax, and connects to Postgres database.");
        Skill vagueSkill = MakeSkill(
            "general-helper",
            "Helps with stuff.");

        SkillSet skillSet = new SkillSet([triggerSkill, actionSkill, vagueSkill]);

        SkillReviewExportResult result = SkillReviewExchange.ExportCandidates(skills: skillSet);

        Assert.NotNull(result);
        Assert.Equal("kyber-weave.skill-review.candidates/v1", result.Bundle.Schema);
        Assert.Equal(3, result.Bundle.Candidates.Count);

        SkillReviewCandidate triggerCandidate = Assert.Single(result.Bundle.Candidates, c => c.Id == "postgres-query");
        Assert.Equal(SkillReviewCandidateType.Skill, triggerCandidate.Type);
        Assert.Equal(triggerSkill.Frontmatter.Description, triggerCandidate.CurrentDescription);
        Assert.True(triggerCandidate.TriggerScore >= 70, $"Expected trigger score >= 70, was {triggerCandidate.TriggerScore}");
        Assert.DoesNotContain("KW-SKILL-LINT-007", triggerCandidate.HeuristicFlags);

        SkillReviewCandidate actionCandidate = Assert.Single(result.Bundle.Candidates, c => c.Id == "sql-generator");
        Assert.Equal(SkillReviewCandidateType.Skill, actionCandidate.Type);
        Assert.Equal(actionSkill.Frontmatter.Description, actionCandidate.CurrentDescription);
        Assert.Contains("KW-SKILL-LINT-007", actionCandidate.HeuristicFlags);

        using JsonDocument parsedJson = JsonDocument.Parse(result.Json);
        Assert.Equal("kyber-weave.skill-review.candidates/v1", parsedJson.RootElement.GetProperty("schema").GetString());
        Assert.Equal(3, parsedJson.RootElement.GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public void ExportCandidatesFromAgentsProducesCandidatesWithAgentTypeAndFlags()
    {
        AgentModel triggerAgent = MakeAgent(
            "schema-architect",
            "Use when designing SQL schemas, writing migrations, or optimizing database queries.");
        AgentModel actionAgent = MakeAgent(
            "data-engineer",
            "Designs database schemas and generates migrations.");

        AgentSet agentSet = new AgentSet([triggerAgent, actionAgent]);

        SkillReviewExportResult result = SkillReviewExchange.ExportCandidates(agents: agentSet);

        Assert.NotNull(result);
        Assert.Equal("kyber-weave.skill-review.candidates/v1", result.Bundle.Schema);
        Assert.Equal(2, result.Bundle.Candidates.Count);

        SkillReviewCandidate triggerCandidate = Assert.Single(result.Bundle.Candidates, c => c.Id == "Claude:schema-architect");
        Assert.Equal(SkillReviewCandidateType.Agent, triggerCandidate.Type);
        Assert.Equal(triggerAgent.Description, triggerCandidate.CurrentDescription);
        Assert.DoesNotContain("KW-AGENT-LINT-002", triggerCandidate.HeuristicFlags);

        SkillReviewCandidate actionCandidate = Assert.Single(result.Bundle.Candidates, c => c.Id == "Claude:data-engineer");
        Assert.Equal(SkillReviewCandidateType.Agent, actionCandidate.Type);
        Assert.Equal(actionAgent.Description, actionCandidate.CurrentDescription);
        Assert.Contains("KW-AGENT-LINT-002", actionCandidate.HeuristicFlags);
    }

    [Fact]
    public void ExportCandidatesCombinedSkillsAndAgentsIncludesBothInSingleBundle()
    {
        Skill skill = MakeSkill("test-skill", "Use when testing application logic.");
        AgentModel agent = MakeAgent("test-agent", "Use when orchestrating unit tests.");

        SkillSet skillSet = new SkillSet([skill]);
        AgentSet agentSet = new AgentSet([agent]);

        SkillReviewExportResult result = SkillReviewExchange.ExportCandidates(skillSet, agentSet);

        Assert.NotNull(result);
        Assert.Equal("kyber-weave.skill-review.candidates/v1", result.Bundle.Schema);
        Assert.Equal(2, result.Bundle.Candidates.Count);
        Assert.Contains(result.Bundle.Candidates, c => c.Id == "test-skill" && c.Type == SkillReviewCandidateType.Skill);
        Assert.Contains(result.Bundle.Candidates, c => c.Id == "Claude:test-agent" && c.Type == SkillReviewCandidateType.Agent);
    }

    [Fact]
    public void ExportCandidatesEmptyInputsReturnsEmptyBundle()
    {
        SkillReviewExportResult result = SkillReviewExchange.ExportCandidates(new SkillSet([]), new AgentSet([]));

        Assert.NotNull(result);
        Assert.Equal("kyber-weave.skill-review.candidates/v1", result.Bundle.Schema);
        Assert.Empty(result.Bundle.Candidates);
    }

    [Fact]
    public void ImportVerdictsValidVerdictsParsesAndValidatesAllFieldsSuccessfully()
    {
        List<SkillReviewCandidate> candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"]),
            new("schema-architect", SkillReviewCandidateType.Agent, "Use when designing SQL schemas.", 85, [])
        };

        string verdictJson = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": [
                {
                    "candidateId": "sql-generator",
                    "isTriggerOriented": false,
                    "confidence": 0.95,
                    "suggestedTriggerDescription": "Use when writing or optimizing PostgreSQL queries.",
                    "rationale": "Description was an action summary without trigger framing."
                },
                {
                    "candidateId": "schema-architect",
                    "isTriggerOriented": true,
                    "confidence": 0.90,
                    "suggestedTriggerDescription": null,
                    "rationale": "Already states trigger conditions clearly."
                }
            ]
        }
        """;

        SkillReviewImportResult result = SkillReviewExchange.ImportVerdicts(verdictJson, candidates);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(2, result.Verdicts.Count);

        SkillReviewVerdict firstVerdict = result.Verdicts[0];
        Assert.Equal("sql-generator", firstVerdict.CandidateId);
        Assert.False(firstVerdict.IsTriggerOriented);
        Assert.Equal(0.95, firstVerdict.Confidence);
        Assert.Equal("Use when writing or optimizing PostgreSQL queries.", firstVerdict.SuggestedTriggerDescription);
        Assert.Equal("Description was an action summary without trigger framing.", firstVerdict.Rationale);

        SkillReviewVerdict secondVerdict = result.Verdicts[1];
        Assert.Equal("schema-architect", secondVerdict.CandidateId);
        Assert.True(secondVerdict.IsTriggerOriented);
        Assert.Equal(0.90, secondVerdict.Confidence);
        Assert.Null(secondVerdict.SuggestedTriggerDescription);
    }

    [Fact]
    public void ImportVerdictsMalformedJsonFailsWithDiagnosticError()
    {
        List<SkillReviewCandidate> candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        string malformedJson = "{ \"schema\": \"kyber-weave.skill-review.verdicts/v1\", invalid json syntax";

        SkillReviewImportResult result = SkillReviewExchange.ImportVerdicts(malformedJson, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001" && d.Severity == Severity.Error);
    }

    [Fact]
    public void ImportVerdictsMismatchedCandidateIdRejectsImportWithDiagnosticError()
    {
        List<SkillReviewCandidate> candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        string mismatchedJson = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": [
                {
                    "candidateId": "non-existent-candidate",
                    "isTriggerOriented": false,
                    "confidence": 0.95,
                    "suggestedTriggerDescription": "Use when..."
                }
            ]
        }
        """;

        SkillReviewImportResult result = SkillReviewExchange.ImportVerdicts(mismatchedJson, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001" && d.Severity == Severity.Error);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void ImportVerdictsConfidenceOutOfRangeRejectsImport(double invalidConfidence)
    {
        List<SkillReviewCandidate> candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        string formattedConfidence = invalidConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string json = $$"""
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": [
                {
                    "candidateId": "sql-generator",
                    "isTriggerOriented": false,
                    "confidence": {{formattedConfidence}},
                    "suggestedTriggerDescription": "Use when..."
                }
            ]
        }
        """;

        SkillReviewImportResult result = SkillReviewExchange.ImportVerdicts(json, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001");
    }

    [Fact]
    public void ImportVerdictsUnsupportedSchemaRejectsImport()
    {
        List<SkillReviewCandidate> candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        string json = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v999",
            "verdicts": [
                {
                    "candidateId": "sql-generator",
                    "isTriggerOriented": false,
                    "confidence": 0.9,
                    "suggestedTriggerDescription": "Use when..."
                }
            ]
        }
        """;

        SkillReviewImportResult result = SkillReviewExchange.ImportVerdicts(json, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001");
    }

    [Fact]
    public void ImportVerdictsDuplicateCandidateIdsInBundleRejectsImport()
    {
        List<SkillReviewCandidate> candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        string json = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": [
                {
                    "candidateId": "sql-generator",
                    "isTriggerOriented": false,
                    "confidence": 0.9,
                    "suggestedTriggerDescription": "Use when..."
                },
                {
                    "candidateId": "sql-generator",
                    "isTriggerOriented": true,
                    "confidence": 0.85,
                    "suggestedTriggerDescription": null
                }
            ]
        }
        """;

        SkillReviewImportResult result = SkillReviewExchange.ImportVerdicts(json, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001");
    }

    [Fact]
    public void ImportVerdictsEmptyVerdictsListRejectsImport()
    {
        List<SkillReviewCandidate> candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"])
        };

        string json = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": []
        }
        """;

        SkillReviewImportResult result = SkillReviewExchange.ImportVerdicts(json, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d => d.Code == "KW-SKILL-REVIEW-001");
    }

    [Fact]
    public void ExportCandidatesDuplicateRoleAcrossHarnessesEmitsDistinctCandidateIds()
    {
        AgentModel claude = MakeAgent(
            "schema-architect",
            "Use when designing SQL schemas in Claude.",
            HarnessKind.Claude);
        AgentModel cursor = MakeAgent(
            "schema-architect",
            "Use when designing SQL schemas in Cursor.",
            HarnessKind.Cursor);

        AgentSet agentSet = new AgentSet([claude, cursor]);
        SkillReviewExportResult result = SkillReviewExchange.ExportCandidates(agents: agentSet);

        Assert.Equal(2, result.Bundle.Candidates.Count);
        Assert.Contains(result.Bundle.Candidates, c => c.Id == "Claude:schema-architect");
        Assert.Contains(result.Bundle.Candidates, c => c.Id == "Cursor:schema-architect");
        Assert.Equal(2, result.Bundle.Candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ImportVerdictsOmittedCandidateRejectsImport()
    {
        List<SkillReviewCandidate> candidates = new List<SkillReviewCandidate>
        {
            new("sql-generator", SkillReviewCandidateType.Skill, "Generates SQL queries.", 35, ["KW-SKILL-LINT-007"]),
            new("Claude:schema-architect", SkillReviewCandidateType.Agent, "Use when designing SQL schemas.", 85, [])
        };

        string json = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": [
                {
                    "candidateId": "sql-generator",
                    "isTriggerOriented": false,
                    "confidence": 0.95,
                    "suggestedTriggerDescription": "Use when writing or optimizing PostgreSQL queries."
                }
            ]
        }
        """;

        SkillReviewImportResult result = SkillReviewExchange.ImportVerdicts(json, candidates);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(0, result.ImportedCount);
        Assert.Contains(result.Diagnostics.Items, d =>
            d.Code == "KW-SKILL-REVIEW-001" &&
            d.Message.Contains("Claude:schema-architect", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportVerdictsCompleteVerdictsForMultiHarnessAgentsSucceeds()
    {
        AgentModel claude = MakeAgent(
            "schema-architect",
            "Use when designing SQL schemas in Claude.",
            HarnessKind.Claude);
        AgentModel cursor = MakeAgent(
            "schema-architect",
            "Use when designing SQL schemas in Cursor.",
            HarnessKind.Cursor);

        SkillReviewExportResult exported = SkillReviewExchange.ExportCandidates(agents: new AgentSet([claude, cursor]));
        string json = """
        {
            "schema": "kyber-weave.skill-review.verdicts/v1",
            "verdicts": [
                {
                    "candidateId": "Claude:schema-architect",
                    "isTriggerOriented": true,
                    "confidence": 0.9,
                    "suggestedTriggerDescription": null
                },
                {
                    "candidateId": "Cursor:schema-architect",
                    "isTriggerOriented": true,
                    "confidence": 0.8,
                    "suggestedTriggerDescription": null
                }
            ]
        }
        """;

        SkillReviewImportResult result = SkillReviewExchange.ImportVerdicts(json, exported.Bundle);

        Assert.True(result.Success);
        Assert.Equal(2, result.ImportedCount);
    }
}
