using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Validation;

namespace KyberWeave.Core.Skills.Review;

/// <summary>
/// Exports skill and agent description review candidates and validates/imports agent review verdicts.
/// </summary>
public static partial class SkillReviewExchange
{
    public const string CandidateSchema = "kyber-weave.skill-review.candidates/v1";
    public const string VerdictSchema = "kyber-weave.skill-review.verdicts/v1";
    public const string ReviewRuleCode = "KW-SKILL-REVIEW-001";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex(@"\b((use|uses)\s+(this\s+)?(skill\s+|agent\s+|tool\s+)?(when|for)|(apply|applies|invoke|invokes|trigger|triggers)\s+when)\b",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TriggerClauseRegex();

    public static SkillReviewExportResult ExportCandidates(
        SkillSet? skills = null,
        AgentSet? agents = null) =>
        ExportCandidates(skills?.Skills, agents?.Agents);

    public static SkillReviewExportResult ExportCandidates(
        IEnumerable<Skill>? skills,
        IEnumerable<AgentModel>? agents = null)
    {
        var candidates = new List<SkillReviewCandidate>();
        var linter = new RoutingLinter();

        if (skills is not null)
        {
            foreach (var skill in skills)
            {
                var id = skill.Frontmatter.Name ?? skill.DirectoryName;
                var description = skill.Frontmatter.Description ?? string.Empty;
                var score = DescriptionScorer.Score(skill).Total;
                var flags = linter.LintSkill(skill)
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
            foreach (var agent in agents)
            {
                // Role names repeat across harnesses; the review key must distinguish them.
                var id = AgentCandidateId(agent);
                var description = agent.Description ?? string.Empty;
                var dummySkill = new Skill
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
                var score = DescriptionScorer.Score(dummySkill).Total;

                var flags = new List<string>();
                var trimmedDesc = description.Trim();
                if (!string.IsNullOrWhiteSpace(trimmedDesc))
                {
                    if (!TriggerClauseRegex().IsMatch(trimmedDesc))
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

        var bundle = new SkillReviewCandidateBundle(CandidateSchema, candidates);
        var json = JsonSerializer.Serialize(bundle, SerializerOptions);
        return new SkillReviewExportResult(bundle, json, new DiagnosticReport());
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

        if (bundle.Verdicts is null || bundle.Verdicts.Count == 0)
        {
            return Failure("The verdict bundle does not contain any verdicts.");
        }

        var candidateMap = new Dictionary<string, SkillReviewCandidate>(StringComparer.Ordinal);
        foreach (var candidate in currentCandidates)
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

        var seenCandidateIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var verdict in bundle.Verdicts)
        {
            if (verdict is null)
            {
                return Failure("The verdict bundle contains a null verdict entry.");
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
                var conf = verdict.Confidence.Value;
                if (double.IsNaN(conf) || double.IsInfinity(conf) || conf < 0.0 || conf > 1.0)
                {
                    return Failure($"Verdict candidate '{verdict.CandidateId}' has invalid confidence {conf}. Must be between 0.0 and 1.0.");
                }
            }
        }

        foreach (var id in candidateMap.Keys)
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
        var diagnostics = new DiagnosticReport();
        diagnostics.Add(new Diagnostic(
            ReviewRuleCode,
            Severity.Error,
            message,
            "skill review import",
            Hint: "Export a fresh candidate bundle, review descriptions, and import a complete verdicts/v1 document."));
        return new SkillReviewImportResult(false, 0, Array.Empty<SkillReviewVerdict>(), diagnostics);
    }
}
