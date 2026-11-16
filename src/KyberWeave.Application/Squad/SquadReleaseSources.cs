using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Application.Squad;

/// <summary>Builds the Squad release sources for the callers that run outside the container.</summary>
public static class SquadReleaseSources
{
    /// <summary>Creates the source that downloads a release from the GitHub API at <paramref name="apiRoot"/>.</summary>
    /// <param name="apiRoot">The release API root: github.com, or a validated loopback override.</param>
    /// <returns>A source that owns its HTTP transport.</returns>
    public static ISquadReleaseSource GitHub(Uri apiRoot) => new GitHubSquadReleaseSource(apiRoot);

    /// <summary>Creates the source that stages a local kyber-squad checkout in place of a download.</summary>
    /// <param name="sourceRoot">The checkout's kyber-squad source root.</param>
    /// <returns>A source that copies the checkout.</returns>
    public static ISquadReleaseSource LocalCheckout(string sourceRoot) => new LocalCheckoutSquadReleaseSource(sourceRoot);
}
