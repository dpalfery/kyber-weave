using KyberWeave.Core.Arbiter;

namespace KyberWeave.Contracts.Repositories;

/// <summary>The merged Arbiter provider together with whether the per-user override supplied its endpoint.</summary>
/// <param name="Provider">The effective provider configuration after applying any override.</param>
/// <param name="EndpointOverridden">
/// <see langword="true"/> when the per-user override explicitly supplied <c>provider.endpoint</c>;
/// otherwise <see langword="false"/>.
/// </param>
public sealed record ArbiterUserProviderResolution(
    ArbiterProviderConfig Provider,
    bool EndpointOverridden);

/// <summary>Reads the per-user Arbiter provider override from the operator's home directory.</summary>
public interface IArbiterUserSettings
{
    /// <summary>
    /// Applies the override at <c>~/.config/kyber-weave/arbiter.yml</c> to the repository provider, field by field.
    /// </summary>
    /// <param name="repository">The provider the repository configures.</param>
    /// <param name="homeDirectory">The home directory the override is looked for under.</param>
    /// <returns>The repository provider unchanged when the file is absent, otherwise the merged provider.</returns>
    /// <exception cref="YamlDotNet.Core.YamlException">The file is unreadable, malformed, or holds a key other than <c>provider</c>.</exception>
    ArbiterProviderConfig ApplyTo(
        ArbiterProviderConfig repository,
        string homeDirectory);

    /// <summary>
    /// Applies the override at <c>~/.config/kyber-weave/arbiter.yml</c> to the repository provider, field by field,
    /// and reports whether the override supplied <c>provider.endpoint</c>.
    /// </summary>
    /// <param name="repository">The provider the repository configures.</param>
    /// <param name="homeDirectory">The home directory the override is looked for under.</param>
    /// <returns>The merged provider and whether the override explicitly supplied its endpoint.</returns>
    /// <exception cref="YamlDotNet.Core.YamlException">The file is unreadable, malformed, or holds a key other than <c>provider</c>.</exception>
    ArbiterUserProviderResolution Resolve(
        ArbiterProviderConfig repository,
        string homeDirectory);
}
