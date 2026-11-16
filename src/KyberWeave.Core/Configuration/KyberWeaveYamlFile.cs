using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;

namespace KyberWeave.Core.Configuration;

/// <summary>The per-section entry points over a config file, for the tests that read one section at a time.</summary>
/// <remarks>
/// The parse and the section merges are in the Domain layer and the file read is
/// <see cref="FileKyberWeaveConfigRepository"/> in the data layer. These entry points have no caller outside tests.
/// </remarks>
internal static class KyberWeaveYamlFile
{
    private static readonly FileKyberWeaveConfigRepository Repository =
        new FileKyberWeaveConfigRepository(new FileRepositoryTreeProbe());

    public static KyberWeaveYamlDocument ParseFile(string yamlPath) => Repository.ReadDocument(yamlPath);

    /// <summary>Merges the <c>ontology:</c> section of a config file over <paramref name="defaults"/>.</summary>
    public static OntologyConfig LoadOntologyMerged(OntologyConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return OntologyConfigLoader.Merge(defaults, ParseFile(yamlPath).Ontology);
    }

    /// <summary>Loads the <c>ontology:</c> section of a config file, reporting a failure instead of throwing.</summary>
    public static OntologyConfigLoadResult TryLoadOntology(string yamlPath) => Repository.TryLoadOntology(yamlPath);

    /// <summary>Merges the <c>harness:</c> section of a config file over the product defaults.</summary>
    public static HarnessProfileConfig LoadHarness(string yamlPath) =>
        HarnessProfileConfigLoader.Merge(HarnessProfileConfig.ProductDefaults, ParseFile(yamlPath).Harness);

    /// <summary>Merges the <c>squad:</c> section of a config file over <paramref name="defaults"/>.</summary>
    public static SquadConfig LoadSquadMerged(SquadConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return SquadConfigLoader.Merge(defaults, ParseFile(yamlPath).Squad);
    }
}
