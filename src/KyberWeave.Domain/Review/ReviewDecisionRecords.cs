using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Review;

/// <summary>One subject put to a decision-model rule: what it is about and the fact values its question reads.</summary>
/// <param name="Subject">What the question is about, as the log and the report name it: a lens or a finding id.</param>
/// <param name="Facts">The value of each fact the rule's declared state reads, by fact name.</param>
/// <param name="Lens">For a rule whose instructions come from a lens, the lens whose text they are.</param>
public sealed record ReviewQuestion(string Subject, IReadOnlyDictionary<string, object?> Facts, string? Lens = null);

/// <summary>What the decision model answered for one subject.</summary>
/// <param name="Answer">The rule's answer name, or <c>undecidable</c> when nothing confident came back.</param>
/// <param name="Score">The confidence of a choice, or for a <c>noul</c> the probability of the rule's first answer.</param>
/// <param name="Threshold">The floor or ceiling the score was compared with.</param>
public sealed record ReviewAnswer(string Answer, double? Score, double? Threshold)
{
    /// <summary>The answer of a question that was not asked or got no usable reply.</summary>
    public static ReviewAnswer Undecidable { get; } = new(RuleEffects.Undecidable, null, null);
}

/// <summary>One logged decision-model question and what the review did with its answer.</summary>
/// <param name="RuleId">The rule whose question was asked.</param>
/// <param name="Subject">The lens or finding the question was about.</param>
/// <param name="Answer">The answer as the rule's floor left it.</param>
/// <param name="Score">The confidence, or the probability of a <c>noul</c>'s first answer.</param>
/// <param name="Effect">What the review did: for example <c>not seated</c>, <c>dropped</c> or <c>no effect</c>.</param>
public sealed record ReviewDecision(string RuleId, string Subject, string Answer, double? Score, string Effect);
