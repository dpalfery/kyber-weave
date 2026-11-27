using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Application.Review;

/// <summary>Builds the decision model of one review command run over the Arbiter's decision log and the cycle's answer file.</summary>
/// <param name="state">Opens the decision log under an arbiter directory.</param>
/// <param name="cache">Keeps the cycle's answers.</param>
/// <param name="keys">Resolves the provider's key, so an unreachable endpoint costs no request.</param>
/// <param name="arbiter">Builds the provider the configuration names.</param>
/// <param name="clock">Stamps the decision records.</param>
/// <param name="ids">Makes the decision record ids.</param>
public sealed class ReviewDecisionModelFactory(
    IArbiterStateFactory state,
    IReviewDecisionCacheStore cache,
    IArbiterKeyService keys,
    IArbiterDryRunService arbiter,
    TimeProvider clock,
    IIdGenerator ids) : IReviewDecisionModelFactory
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
            ids,
            unavailableReason,
            cache,
            cachePath);

    /// <inheritdoc />
    public IReviewDecisionModel CreateForRepository(
        string repositoryRoot, KyberWeaveConfig config, bool switchedOff, string? cachePath)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (switchedOff)
            return Create(null, [], null, clock, cachePath: cachePath);

        ArbiterProviderConfig provider = config.Arbiter.Provider;
        if (provider.Kind == ArbiterProviderKind.None)
            return Create(null, [], null, clock, cachePath: cachePath);

        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            return Create(
                null, [], null, clock, "arbiter.provider.endpoint is not an absolute URL", cachePath);
        }

        // A loopback endpoint needs no key, and asking for one first is what keeps a missing key from costing a
        // request that the provider would have refused anyway.
        if (!endpoint.IsLoopback && string.IsNullOrWhiteSpace(keys.TryResolveKey(config, provider.Endpoint)))
        {
            return Create(
                null, [], null, clock, $"no key for {endpoint.GetLeftPart(UriPartial.Authority)}", cachePath);
        }

        DiagnosticReport ignored = new();
        IArbiterProvider? client = arbiter.CreateProvider(config, null, ignored, out _);
        return client is null
            ? Create(null, [], null, clock, "the provider could not be built", cachePath)
            : Create(client, config.Arbiter.Rules, ArbiterStateDirectory.ForProject(repositoryRoot), clock, cachePath: cachePath);
    }
}
