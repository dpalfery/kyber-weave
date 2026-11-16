using KyberWeave.Arbiter.Hooks;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Loads a repository's <c>.kyber-weave/kyber-weave.yml</c> for the hook host.</summary>
public interface IHostConfigLoader
{
    /// <summary>Attempts to load the host configuration under <paramref name="repoRoot"/>.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <returns>The result; invalid YAML and I/O failures come back as a failed result, and a repository with no file loads as the product defaults.</returns>
    HostConfigLoadResult TryLoad(string repoRoot);
}
