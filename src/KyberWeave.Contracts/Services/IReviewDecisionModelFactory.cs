using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Providers;

namespace KyberWeave.Contracts.Services;

/// <summary>Builds the decision model of one review command run over the Arbiter's decision log and the cycle's answer file.</summary>
public interface IReviewDecisionModelFactory
{
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
