using System.Text.Json;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Validation;

namespace KyberWeave.Core.Skills.Review;

/// <summary>
/// Exports skill and agent description review candidates and validates/imports agent review verdicts.
/// </summary>
public static class SkillReviewExchange
{
    private const string CandidateSchema = "kyber-weave.skill-review.candidates/v1";
    private const string VerdictSchema = "kyber-weave.skill-review.verdicts/v1";
    private const string ReviewRuleCode = "KW-SKILL-REVIEW-001";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static SkillReviewExportResult ExportCandidates(
        SkillSet? skills = null,
        AgentSet? agents = null) =>
        ExportCandidates(skills?.Skills, agents?.Agents);

    private static SkillReviewExportResult ExportCandidates(
        IEnumerable<Skill>? skills,
        IEnumerable<AgentModel>? agents = null)
    {
        List<SkillReviewCandidate> candidates = new List<SkillReviewCandidate>();
        RoutingLinter linter = new RoutingLinter();

        if (skills is not null)
        {
            foreach (Skill skill in skills)
            {
                string id = skill.Frontmatter.Name ?? skill.DirectoryName;
                string description = skill.Frontmatter.Description ?? string.Empty;
                int score = DescriptionScorer.Score(skill).Total;
                List<string> flags = linter.LintSkill(skill)
                    .Select(d => d.Code)
                    .Distinct()
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList();

                candidates.Add(new SkillReviewCandidate(
                    Id: id,
                    Type: SkillReviewCandidateType.Skill,
                    CurrentDescription: description,
                    TriggerScore: score,
                    HeuristicFlags: flags,
                    FilePath: skill.SkillFilePath));
            }
        }

        if (agents is not null)
        {
            foreach (AgentModel agent in agents)
            {
                // Role names repeat across harnesses; the review key must distinguish them.
                string id = AgentCandidateId(agent);
                string description = agent.Description;
                Skill dummySkill = new Skill
                {
                    SkillFilePath = agent.FilePath,
                    DirectoryPath = agent.DirectoryPath,
                    RawFrontmatter = string.Empty,
                    InstructionsBody = agent.InstructionsBody,
                    Frontmatter = new SkillFrontmatter
                    {
                        Name = agent.RoleName,
                        Description = agent.Description
                    }
                };
                int score = DescriptionScorer.Score(dummySkill).Total;

                List<string> flags = new List<string>();
                string trimmedDesc = description.Trim();
                if (!string.IsNullOrWhiteSpace(trimmedDesc))
                {
                    if (!RoutingLinter.TriggerClauseRegex().IsMatch(trimmedDesc))
                    {
                        flags.Add(AgentSyncLinter.RuleMissingTriggerPhrasing);
                    }
                    if (score < 50)
                    {
                        flags.Add(AgentSyncLinter.RuleLowRoutingScore);
                    }
                }
                else
                {
                    flags.Add(AgentSpecValidator.RuleMissingDescription);
                }

                candidates.Add(new SkillReviewCandidate(
                    Id: id,
                    Type: SkillReviewCandidateType.Agent,
                    CurrentDescription: description,
                    TriggerScore: score,
                    HeuristicFlags: flags,
                    FilePath: agent.FilePath));
            }
        }

        SkillReviewCandidateBundle bundle = new SkillReviewCandidateBundle(CandidateSchema, candidates);
        string json = JsonSerializer.Serialize(bundle, SerializerOptions);
        return new SkillReviewExportResult(bundle, json);
    }

    public static SkillReviewImportResult ImportVerdicts(
        string json,
        SkillReviewCandidateBundle expectedBundle)
    {
        ArgumentNullException.ThrowIfNull(expectedBundle);
        return ImportVerdicts(json, expectedBundle.Candidates);
    }

    public static SkillReviewImportResult ImportVerdicts(
        string json,
        IReadOnlyList<SkillReviewCandidate> currentCandidates)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Failure("The verdict bundle is empty or whitespace.");
        }
        ArgumentNullException.ThrowIfNull(currentCandidates);

        SkillReviewVerdictBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<SkillReviewVerdictBundle>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return Failure($"The verdict bundle is not valid JSON for the review schema: {ex.Message}");
        }

        if (bundle is null)
        {
            return Failure("The verdict bundle is empty.");
        }

        if (!string.Equals(bundle.Schema, VerdictSchema, StringComparison.Ordinal))
        {
            return Failure($"Unsupported verdict schema '{bundle.Schema}'. Expected '{VerdictSchema}'.");
        }

        if (bundle.Verdicts is null)
        {
            return Failure("The verdict bundle omits the verdicts collection.");
        }

        if (bundle.Verdicts.Count == 0)
        {
            return Failure("The verdict bundle does not contain any verdicts.");
        }

        Dictionary<string, SkillReviewCandidate> candidateMap = new Dictionary<string, SkillReviewCandidate>(StringComparer.Ordinal);
        foreach (SkillReviewCandidate candidate in currentCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id))
            {
                return Failure("Current candidates contain an empty id.");
            }

            if (!candidateMap.TryAdd(candidate.Id, candidate))
            {
                return Failure($"Current candidates contain duplicate id '{candidate.Id}'.");
            }
        }

        HashSet<string> seenCandidateIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (SkillReviewVerdict? verdict in bundle.Verdicts)
        {
            if (verdict is null)
            {
                return Failure("The verdict bundle contains a null verdict.");
            }
            if (string.IsNullOrWhiteSpace(verdict.CandidateId))
            {
                return Failure("Verdict candidate_id must be non-empty.");
            }

            if (!seenCandidateIds.Add(verdict.CandidateId))
            {
                return Failure($"Duplicate verdict for candidate id '{verdict.CandidateId}'.");
            }

            if (!candidateMap.ContainsKey(verdict.CandidateId))
            {
                return Failure($"Verdict candidate '{verdict.CandidateId}' does not exist in current candidates.");
            }

            if (verdict.Confidence.HasValue)
            {
                double conf = verdict.Confidence.Value;
                if (double.IsNaN(conf) || double.IsInfinity(conf) || conf < 0.0 || conf > 1.0)
                {
                    return Failure($"Verdict candidate '{verdict.CandidateId}' has invalid confidence {conf}. Must be between 0.0 and 1.0.");
                }
            }
        }

        foreach (string id in candidateMap.Keys)
        {
            if (!seenCandidateIds.Contains(id))
            {
                return Failure($"Missing verdict for candidate id '{id}'.");
            }
        }

        return new SkillReviewImportResult(true, bundle.Verdicts.Count, bundle.Verdicts, new DiagnosticReport());
    }

    private static string AgentCandidateId(AgentModel agent) =>
        $"{agent.Harness}:{agent.RoleName}";

    private static SkillReviewImportResult Failure(string message)
    {
        DiagnosticReport diagnostics = new DiagnosticReport();
        diagnostics.Add(new Diagnostic(
            ReviewRuleCode,
            Severity.Error,
            message,
            "skill review import",
            Hint: "Export a fresh candidate bundle, review descriptions, and import a complete verdicts/v1 document."));
        return new SkillReviewImportResult(false, 0, Array.Empty<SkillReviewVerdict>(), diagnostics);
    }
}
