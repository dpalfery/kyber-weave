using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IArbiterUserSettings"/>: returns the repository provider unchanged, so no home directory is read.</summary>
/// <remarks>A test that models an override assigns <see cref="Override"/> to the provider the merge produces.</remarks>
internal sealed class FakeArbiterUserSettings : IArbiterUserSettings
{
    /// <summary>The provider the override produces; null leaves the repository provider unchanged.</summary>
    public Func<ArbiterProviderConfig, string, ArbiterProviderConfig>? Override { get; init; }

    /// <summary>
    /// Overrides the inferred <see cref="ArbiterUserProviderResolution.EndpointOverridden"/> flag;
    /// when null, inferred from whether the trimmed endpoint changed.
    /// </summary>
    public bool? EndpointOverridden { get; init; }

    /// <summary>The home directories the factory asked about, in order.</summary>
    public List<string> Asked { get; } = [];

    /// <inheritdoc />
    public ArbiterProviderConfig ApplyTo(ArbiterProviderConfig repository, string homeDirectory) =>
        Resolve(repository, homeDirectory).Provider;

    /// <inheritdoc />
    public ArbiterUserProviderResolution Resolve(ArbiterProviderConfig repository, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        Asked.Add(homeDirectory);
        if (Override is null)
        {
            return new ArbiterUserProviderResolution(repository, EndpointOverridden: false);
        }

        ArbiterProviderConfig merged = Override(repository, homeDirectory);
        bool endpointOverridden = EndpointOverridden ?? !ArbiterUserSettings.EndpointsEqual(merged.Endpoint, repository.Endpoint);
        return new ArbiterUserProviderResolution(merged, endpointOverridden);
    }
}
