using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// The decision model the review commands put their questions to: one command run's questions, answers and faults.
/// Optional by design: with no provider, no key, a fault or a timeout every answer is <c>undecidable</c>.
/// </summary>
public interface IReviewDecisionModel
{
    /// <summary>Whether a provider is configured, so questions are asked until the first fault.</summary>
    bool Configured { get; }

    /// <summary>Why a decision model the configuration names cannot be used; null when it can, or when none is named.</summary>
    string? UnavailableReason { get; }

    /// <summary>Whether a fault stopped further calls in this command run.</summary>
    bool Faulted { get; }

    /// <summary>Every question asked so far, in order, with its answer and effect.</summary>
    IReadOnlyList<ReviewDecision> Decisions { get; }

    /// <summary>The effective rule with this id, when it is enabled and asks a question; null otherwise.</summary>
    /// <param name="id">The rule id.</param>
    ArbiterRule? Rule(string id);

    /// <summary>Asks the rule's question once per subject and logs one decision per subject.</summary>
    /// <param name="rule">The rule to ask.</param>
    /// <param name="questions">The subjects.</param>
    /// <param name="effectOf">What the review did with an answer, for the log and the report.</param>
    /// <param name="cancellationToken">Cancels the provider calls.</param>
    /// <returns>The answer per subject; undecidable for every subject when nothing was asked.</returns>
    Task<IReadOnlyDictionary<string, ReviewAnswer>> AskAsync(
        ArbiterRule? rule,
        IReadOnlyList<ReviewQuestion> questions,
        Func<ReviewQuestion, ReviewAnswer, string> effectOf,
        CancellationToken cancellationToken = default);

    /// <summary>The one line a review prints about the model; null when no model is configured.</summary>
    /// <param name="counts">The non-zero effects to report, in order.</param>
    string? SummaryLine(IReadOnlyList<string> counts);

    /// <summary>Records comparisons a step left unasked because it reached its cap, for the summary line.</summary>
    /// <param name="count">How many were left unasked.</param>
    void NoteSkipped(int count);

    /// <summary>Records findings a step did not compare at all because it reached its per-file or per-step limit.</summary>
    /// <param name="count">How many were left out.</param>
    void NoteFindingsLeftOut(int count);
}
