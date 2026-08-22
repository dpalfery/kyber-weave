using System.Text.Json.Serialization;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Skills.Review;

/// <summary>Identifies whether a review candidate originates from a Skill or an Agent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SkillReviewCandidateType
{
    Skill,
    Agent
}

/// <summary>One skill or agent description review candidate.</summary>
public sealed record SkillReviewCandidate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] SkillReviewCandidateType Type,
    [property: JsonPropertyName("currentDescription")] string CurrentDescription,
    [property: JsonPropertyName("triggerScore")] int TriggerScore,
    [property: JsonPropertyName("heuristicFlags")] IReadOnlyList<string> HeuristicFlags,
    [property: JsonPropertyName("filePath")] string? FilePath = null);

/// <summary>Versioned candidate exchange document for skill and agent description reviews.</summary>
public sealed record SkillReviewCandidateBundle(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("candidates")] IReadOnlyList<SkillReviewCandidate> Candidates);

/// <summary>Serialized candidate export result.</summary>
public sealed record SkillReviewExportResult(
    SkillReviewCandidateBundle Bundle,
    string Json);

/// <summary>One reviewer verdict on a skill or agent description.</summary>
public sealed record SkillReviewVerdict(
    [property: JsonPropertyName("candidateId")] string CandidateId,
    [property: JsonPropertyName("isTriggerOriented")] bool? IsTriggerOriented,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("suggestedTriggerDescription")] string? SuggestedTriggerDescription,
    [property: JsonPropertyName("rationale")] string? Rationale = null);

/// <summary>Versioned reviewer verdict exchange document.</summary>
public sealed record SkillReviewVerdictBundle(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("verdicts")] IReadOnlyList<SkillReviewVerdict> Verdicts);

/// <summary>Atomic verdict-import outcome.</summary>
public sealed record SkillReviewImportResult(
    bool Success,
    int ImportedCount,
    IReadOnlyList<SkillReviewVerdict> Verdicts,
    DiagnosticReport Diagnostics);
