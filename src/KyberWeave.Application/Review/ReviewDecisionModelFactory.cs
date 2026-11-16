using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Application.Review;

/// <summary>Builds the decision model of one review command run over the Arbiter's decision log and the cycle's answer file.</summary>
/// <param name="state">Opens the decision log under an arbiter directory.</param>
/// <param name="cache">Keeps the cycle's answers.</param>
public sealed class ReviewDecisionModelFactory(IArbiterStateFactory state, IReviewDecisionCacheStore cache) : IReviewDecisionModelFactory
{
    /// <inheritdoc />
    public IReviewDecisionModel Create(
        IArbiterProvider? provider,
        IReadOnlyList<ArbiterRule> rules,
        string? logDirectory,
        TimeProvider clock,
        string? unavailableReason = null,
        string? cachePath = null) =>
        new ReviewDecisionSession(
            provider,
            rules,
            logDirectory is null ? null : state.OpenDecisionLog(logDirectory),
            clock,
            unavailableReason,
            cache,
            cachePath);
}
