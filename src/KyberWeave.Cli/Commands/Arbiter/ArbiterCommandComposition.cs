using KyberWeave.Application;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// The forwarding shim the callers that have no container still use: it answers over the application container's
/// Arbiter services and holds no seam of its own. The Arbiter commands take those services by constructor.
/// </summary>
/// <remarks>
/// Kept only while three callers outside the Arbiter commands remain: <c>DocsTasksCommand</c> (BLOCKED-BY DC),
/// <c>ReviewDecisionModelFactory</c> (BLOCKED-BY RV) and <c>SquadHostDefaults</c> (BLOCKED-BY SQ). Delete it when the last
/// of them takes <see cref="IArbiterKeyService"/> and <see cref="IArbiterDryRunService"/> from the container.
/// </remarks>
public static class ArbiterCommandComposition
{
    private static readonly Lazy<ServiceProvider> Container = new(
        () => new ServiceCollection().AddWeaveApplication().BuildServiceProvider());

    /// <summary>The operator home directory.</summary>
    public static Func<string> HomeDirectory { get; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Resolves the key for <paramref name="endpoint"/>, treating a failing store as an absent key.
    /// </summary>
    /// <param name="config">The configuration whose provider the user override applies to.</param>
    /// <param name="endpoint">The provider endpoint.</param>
    /// <returns>The key, or null when none resolves.</returns>
    public static string? TryResolveKey(KyberWeaveConfig config, string endpoint) =>
        Container.Value.GetRequiredService<IArbiterKeyService>().TryResolveKey(config, endpoint);

    /// <summary>
    /// Creates the step-1 provider: the configured provider, or the override when one is supplied. Returns null and
    /// reports when the override or endpoint is unusable.
    /// </summary>
    /// <param name="config">The effective configuration.</param>
    /// <param name="providerOverride">The provider override, or null.</param>
    /// <param name="report">Receives the diagnostic when the provider is unusable.</param>
    /// <param name="keyResolver">Receives the resolver the provider asks for its key.</param>
    /// <returns>The provider, which the caller disposes.</returns>
    public static IArbiterProvider? CreateProvider(
        KyberWeaveConfig config,
        string? providerOverride,
        DiagnosticReport report,
        out Func<Uri, string?> keyResolver) =>
        Container.Value.GetRequiredService<IArbiterDryRunService>()
            .CreateProvider(config, providerOverride, report, out keyResolver);
}
