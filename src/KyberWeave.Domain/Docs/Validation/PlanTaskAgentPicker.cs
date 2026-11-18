namespace KyberWeave.Core.Docs.Validation;

/// <summary>One task's agent pick and difficulty rating: what the model chose and how sure it was.</summary>
/// <param name="Agent">The chosen agent's name.</param>
/// <param name="Confidence">The model's confidence, at or above the confidence floor of the picker.</param>
/// <param name="Difficulty">
/// The rating the model chose: <c>easy</c>, <c>medium</c> or <c>hard</c>, with the shipped rule's
/// <c>standard</c> answer mapped to <c>medium</c>. Null when the rating did not clear the floor.
/// </param>
/// <param name="DifficultyConfidence">The model's confidence in <paramref name="Difficulty"/>; null when there is none.</param>
public sealed record PlanTaskAgentChoice(
    string Agent,
    double Confidence,
    string? Difficulty = null,
    double? DifficultyConfidence = null);

/// <summary>Chooses an agent for each task of a task file while the file is written.</summary>
public interface IPlanTaskAgentPicker : IDisposable
{
    /// <summary>
    /// The confident picks by task id. A task with no confident pick is left out, and a picker that cannot reach its
    /// model returns no picks at all rather than failing.
    /// </summary>
    /// <param name="tasks">The tasks that need an agent: the plan assigned them no role.</param>
    IReadOnlyDictionary<string, PlanTaskAgentChoice> Pick(IReadOnlyList<PlanTaskFileEntry> tasks);
}
