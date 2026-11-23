namespace KyberWeave.Core.Docs.Validation;

/// <summary>One task's agent pick and difficulty check: what the model chose and how sure it was.</summary>
/// <param name="Agent">
/// The chosen agent's name; null when the pick is missing, below the picker's confidence floor or names no
/// candidate, which leaves the agent for the conductor to choose. The difficulty check is independent of it.
/// </param>
/// <param name="Confidence">The model's confidence in <paramref name="Agent"/>; 0 when there is no agent.</param>
/// <param name="DifficultyCheck">
/// The decision model's answer to whether the architect's rating fits the rubric; null when the answer did
/// not clear the floor, the model gave none, or the rubric could not be read.
/// </param>
public sealed record PlanTaskAgentChoice(
    string? Agent,
    double Confidence,
    DifficultyAgreement? DifficultyCheck = null);

/// <summary>The decision model's answer to the difficulty agreement question.</summary>
/// <param name="Answer">Either <c>agreed</c> or <c>not-agreed</c>.</param>
/// <param name="Confidence">The model's confidence in <paramref name="Answer"/>, at or above the picker's floor.</param>
public sealed record DifficultyAgreement(string Answer, double Confidence)
{
    /// <summary>The answer that keeps the architect's rating.</summary>
    public const string Agreed = "agreed";

    /// <summary>The answer that sends the plan back to the architect to rate the task again.</summary>
    public const string NotAgreed = "not-agreed";

    /// <summary>Whether the model agreed with the architect's rating.</summary>
    public bool IsAgreed => string.Equals(Answer, Agreed, StringComparison.Ordinal);
}
