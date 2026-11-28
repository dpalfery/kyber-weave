using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Services;

/// <summary>Builds the decision model of one review command run over the Arbiter's decision log and the cycle's answer file.</summary>
public interface IReviewDecisionModelFactory
{
    /// <summary>
    /// Creates the model the review commands ask, from the repository's <c>arbiter.provider</c>, without blocking a
    /// thread: the configured provider, the decision log under the repository, and the cycle's answer file. It asks
    /// whether or not the Arbiter's hooks are installed, because the review puts its questions to the model directly
    /// and needs no hook.
    /// </summary>
    /// <param name="repositoryRoot">The repository root; the decision log is appended under <c>artifacts/arbiter</c> there.</param>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="switchedOff">Whether <c>--no-decision-model</c> was given.</param>
    /// <param name="cachePath">The cycle's answer file, reused across the runs of one cycle; null keeps none.</param>
    /// <param name="cancellationToken">Cancels the credential-store lookup.</param>
    /// <returns>
    /// The model. Switched off, or no provider named: one that asks nothing. A remote endpoint with no key, or an
    /// endpoint that is not a URL: one that asks nothing and says once why, so no request is sent only to be refused.
    /// </returns>
    Task<IReviewDecisionModel> CreateForRepositoryAsync(
        string repositoryRoot,
        KyberWeaveConfig config,
        bool switchedOff,
        string? cachePath,
        CancellationToken cancellationToken = default);

    /// <summary>Creates the model over a provider; a null provider is the model switched off.</summary>
    /// <param name="provider">The provider that answers; null when no decision model is configured.</param>
    /// <param name="rules">The effective Arbiter rules, with the host's overrides applied.</param>
    /// <param name="logDirectory">The Arbiter directory the decision log is appended in; null logs nothing.</param>
    /// <param name="clock">Stamps the decision records.</param>
    /// <param name="unavailableReason">Why a configured model cannot be used (no key, a bad endpoint); null otherwise.</param>
    /// <param name="cachePath">A file the answers of this review cycle are kept in and reused from; null keeps none.</param>
    IReviewDecisionModel Create(
        IArbiterProvider? provider,
        IReadOnlyList<ArbiterRule> rules,
        string? logDirectory,
        TimeProvider clock,
        string? unavailableReason = null,
        string? cachePath = null);
}
