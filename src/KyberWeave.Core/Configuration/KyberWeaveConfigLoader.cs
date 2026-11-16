using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;

namespace KyberWeave.Core.Configuration;

/// <summary>
/// Loads the combined Kyber-Weave configuration from YAML text or a repository root, for the callers that have no
/// container.
/// </summary>
/// <remarks>
/// The reads are <see cref="FileKyberWeaveConfigRepository"/> in the data layer behind
/// <see cref="IKyberWeaveConfigRepository"/>, and the parse and merge are <see cref="KyberWeaveConfigAssembler"/> in the
/// domain layer. A caller that holds the container takes the repository from it instead.
/// </remarks>
public static class KyberWeaveConfigLoader
{
    /// <summary>Stable rule code for invalid or unreadable <c>kyber-weave.yml</c>.</summary>
    public const string ConfigLoadErrorCode = "KW-CONFIG-001";

    /// <summary>Stable rule code for a non-fatal problem in <c>kyber-weave.yml</c>, such as an unknown key.</summary>
    public const string ConfigWarningCode = "KW-CONFIG-002";

    internal static IKyberWeaveConfigRepository Repository { get; } =
        new FileKyberWeaveConfigRepository(new FileRepositoryTreeProbe());

    /// <inheritdoc cref="KyberWeaveConfigAssembler.FromYaml" />
    public static KyberWeaveConfig LoadFromYaml(string yaml) => KyberWeaveConfigAssembler.FromYaml(yaml);

    /// <inheritdoc cref="IKyberWeaveConfigRepository.LoadUserConfig" />
    public static KyberWeaveConfig LoadUserConfig(string homeDirectory, string? configPath = null) =>
        Repository.LoadUserConfig(homeDirectory, configPath);

    /// <inheritdoc cref="IKyberWeaveConfigRepository.TryLoadUserConfig" />
    public static KyberWeaveConfigLoadResult TryLoadUserConfig(string homeDirectory, string? configPath = null) =>
        Repository.TryLoadUserConfig(homeDirectory, configPath);

    /// <inheritdoc cref="IKyberWeaveConfigRepository.Load" />
    public static KyberWeaveConfig Load(string repoRoot, string? configPath = null) => Repository.Load(repoRoot, configPath);

    /// <inheritdoc cref="IKyberWeaveConfigRepository.TryLoad" />
    public static KyberWeaveConfigLoadResult TryLoad(string repoRoot, string? configPath = null) =>
        Repository.TryLoad(repoRoot, configPath);

    /// <inheritdoc cref="IKyberWeaveConfigRepository.FindConfigPath" />
    public static string? FindConfigPath(string repoRoot) => Repository.FindConfigPath(repoRoot);
}
