using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;

namespace KyberWeave.Tests.Configuration;

// The section-at-a-time entry points the retired KyberWeaveYamlFile offered: a test that reads one section of a
// config file keeps reading exactly one section.

/// <summary>
/// Reads a configuration file from the real disk for a test that arranges one, usually under a temporary root.
/// </summary>
/// <remarks>
/// A test of behaviour takes <c>FakeKyberWeaveConfigRepository</c> instead, so it touches no disk. This helper exists
/// for the classes that build a real repository fixture first and then ask what the product reads from it; it is the
/// production adapter, not a second implementation.
/// </remarks>
public static class ConfigFileReader
{
    /// <summary>The real adapter, for a test that hands the seam itself to the code under test.</summary>
    public static IKyberWeaveConfigRepository Repository { get; } =
        new FileKyberWeaveConfigRepository(new FileRepositoryTreeProbe());

    private static readonly FileKyberWeaveConfigRepository Files =
        new(new FileRepositoryTreeProbe());

    /// <summary>Reads and parses one config file into its sections, without merging them over defaults.</summary>
    internal static KyberWeaveYamlDocument ParseFile(string yamlPath) => Files.ReadDocument(yamlPath);

    /// <summary>Loads the <c>ontology:</c> section of a config file, reporting a failure instead of throwing.</summary>
    public static OntologyConfigLoadResult TryLoadOntology(string yamlPath) => Files.TryLoadOntology(yamlPath);

    /// <summary>Merges the <c>ontology:</c> section of a config file over <paramref name="defaults"/>.</summary>
    public static OntologyConfig LoadOntologyMerged(OntologyConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return OntologyConfigLoader.Merge(defaults, ParseFile(yamlPath).Ontology);
    }

    /// <summary>Merges the <c>harness:</c> section of a config file over the product defaults.</summary>
    public static HarnessProfileConfig LoadHarness(string yamlPath) =>
        HarnessProfileConfigLoader.Merge(HarnessProfileConfig.ProductDefaults, ParseFile(yamlPath).Harness);

    /// <summary>Merges the <c>squad:</c> section of a config file over <paramref name="defaults"/>.</summary>
    public static SquadConfig LoadSquadMerged(SquadConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return SquadConfigLoader.Merge(defaults, ParseFile(yamlPath).Squad);
    }

    /// <inheritdoc cref="IKyberWeaveConfigRepository.Load" />
    public static KyberWeaveConfig Load(string repoRoot, string? configPath = null) =>
        Repository.Load(repoRoot, configPath);

    /// <inheritdoc cref="IKyberWeaveConfigRepository.TryLoad" />
    public static KyberWeaveConfigLoadResult TryLoad(string repoRoot, string? configPath = null) =>
        Repository.TryLoad(repoRoot, configPath);

    /// <inheritdoc cref="IKyberWeaveConfigRepository.FindConfigPath" />
    public static string? FindConfigPath(string repoRoot) => Repository.FindConfigPath(repoRoot);

    /// <inheritdoc cref="IKyberWeaveConfigRepository.LoadUserConfig" />
    public static KyberWeaveConfig LoadUserConfig(string homeDirectory, string? configPath = null) =>
        Repository.LoadUserConfig(homeDirectory, configPath);

    /// <inheritdoc cref="IKyberWeaveConfigRepository.TryLoadUserConfig" />
    public static KyberWeaveConfigLoadResult TryLoadUserConfig(string homeDirectory, string? configPath = null) =>
        Repository.TryLoadUserConfig(homeDirectory, configPath);
}
