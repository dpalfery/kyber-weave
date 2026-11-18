using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IArbiterUserSettings"/>: returns the repository provider unchanged, so no home directory is read.</summary>
/// <remarks>A test that models an override assigns <see cref="Override"/> to the provider the merge produces.</remarks>
internal sealed class FakeArbiterUserSettings : IArbiterUserSettings
{
    /// <summary>The provider the override produces; null leaves the repository provider unchanged.</summary>
    public Func<ArbiterProviderConfig, string, ArbiterProviderConfig>? Override { get; init; }

    /// <summary>The home directories the factory asked about, in order.</summary>
    public List<string> Asked { get; } = [];

    /// <inheritdoc />
    public ArbiterProviderConfig ApplyTo(ArbiterProviderConfig repository, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        Asked.Add(homeDirectory);
        return Override?.Invoke(repository, homeDirectory) ?? repository;
    }
}
