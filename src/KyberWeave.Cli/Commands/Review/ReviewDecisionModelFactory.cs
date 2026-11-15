using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
/// Builds the review commands' decision model from <c>arbiter.provider</c>, whether or not the Arbiter's hooks are
/// installed or <c>arbiter.enabled</c> is set: the review asks the model directly and needs no hook.
/// </summary>
internal static class ReviewDecisionModelFactory
{
    /// <summary>The provider seam; tests replace it with a fake that answers offline.</summary>
    internal static Func<KyberWeaveConfig, IArbiterProvider?>? ProviderOverride { get; set; }

    /// <summary>
    /// The model for one command run. Switched off, or provider <c>none</c>: a model that asks nothing and prints
    /// nothing. A remote endpoint with no key, or an endpoint that is not a URL: a model that asks nothing and says once
    /// that it is not configured, so no request is sent only to be refused.
    /// </summary>
    /// <param name="root">The repository root; the decision log is appended under <c>artifacts/arbiter</c> there.</param>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="switchedOff">Whether <c>--no-decision-model</c> was given.</param>
    /// <param name="cachePath">The cycle's answer file, reused across runs of one cycle; null keeps none.</param>
    public static ReviewDecisionModel Create(string root, KyberWeaveConfig config, bool switchedOff, string? cachePath)
    {
        if (switchedOff)
            return ReviewDecisionModel.Off;

        string logDirectory = Path.Combine(root, "artifacts", "arbiter");
        if (ProviderOverride is { } fake)
            return new ReviewDecisionModel(fake(config), config.Arbiter.Rules, logDirectory, TimeProvider.System, cachePath: cachePath);

        ArbiterProviderConfig provider = config.Arbiter.Provider;
        if (provider.Kind == ArbiterProviderKind.None)
            return ReviewDecisionModel.Off;

        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
            return new ReviewDecisionModel(null, [], null, TimeProvider.System, "arbiter.provider.endpoint is not an absolute URL");

        if (!endpoint.IsLoopback && string.IsNullOrWhiteSpace(ArbiterCommandComposition.TryResolveKey(config, provider.Endpoint)))
            return new ReviewDecisionModel(null, [], null, TimeProvider.System, $"no key for {endpoint.GetLeftPart(UriPartial.Authority)}");

        DiagnosticReport ignored = new();
        IArbiterProvider? client = ArbiterCommandComposition.CreateProvider(config, null, ignored, out _);
        return client is null
            ? new ReviewDecisionModel(null, [], null, TimeProvider.System, "the provider could not be built")
            : new ReviewDecisionModel(client, config.Arbiter.Rules, logDirectory, TimeProvider.System, cachePath: cachePath);
    }
}
