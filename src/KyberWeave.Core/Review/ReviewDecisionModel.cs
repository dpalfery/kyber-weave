using KyberWeave.Application.Review;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Data.Review;

namespace KyberWeave.Core.Review;

/// <summary>
/// The review commands' decision model for the callers that have no container: it asks the model the Arbiter's provider
/// configuration names, and logs and caches over the real file system.
/// </summary>
/// <remarks>
/// The logic is <see cref="ReviewDecisionSession"/> in the application layer; a caller that holds the container takes
/// <c>IReviewDecisionModelFactory</c> from it instead. This stays for the review commands that build the model
/// themselves, and for the tests that do.
/// </remarks>
public sealed class ReviewDecisionModel : ReviewDecisionSession
{
    /// <summary>Creates the model over a provider; a null provider is the model switched off.</summary>
    /// <param name="provider">The provider that answers; null when no decision model is configured.</param>
    /// <param name="rules">The effective Arbiter rules, with the host's overrides applied.</param>
    /// <param name="logDirectory">The Arbiter directory the decision log is appended in; null logs nothing.</param>
    /// <param name="clock">Stamps the decision records.</param>
    /// <param name="unavailableReason">Why a configured model cannot be used (no key, a bad endpoint); null otherwise.</param>
    /// <param name="cachePath">A file the answers of this review cycle are kept in and reused from; null keeps none.</param>
    public ReviewDecisionModel(
        IArbiterProvider? provider,
        IReadOnlyList<ArbiterRule> rules,
        string? logDirectory,
        TimeProvider clock,
        string? unavailableReason = null,
        string? cachePath = null)
        : base(
            provider,
            rules,
            logDirectory is null ? null : new DecisionLog(logDirectory),
            clock,
            unavailableReason,
            new FileReviewDecisionCacheStore(),
            cachePath)
    {
    }

    /// <summary>The model switched off: nothing is asked, logged or printed.</summary>
    public static ReviewDecisionModel Off { get; } = new(null, [], null, TimeProvider.System);

    /// <inheritdoc cref="ReviewDecisionText.Format" />
    public static string Format(double? score) => ReviewDecisionText.Format(score);

    /// <inheritdoc cref="ReviewDecisionText.FindingText" />
    public static string FindingText(ReviewFinding finding) => ReviewDecisionText.FindingText(finding);
}
