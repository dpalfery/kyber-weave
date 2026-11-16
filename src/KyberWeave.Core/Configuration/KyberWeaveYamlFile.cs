using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>The file-reading half of <see cref="KyberWeaveYamlParser"/>: reads a config file and parses it.</summary>
/// <remarks>
/// The parse and the section merges are pure and live in the Domain layer; reading the file is not, so it stays here
/// until the combined loader (blocked on the Arbiter section's types) moves to Data behind a repository port. The
/// per-section entry points below have no caller outside tests.
/// </remarks>
internal static class KyberWeaveYamlFile
{
    public static KyberWeaveYamlDocument ParseFile(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);
        string yaml = File.ReadAllText(yamlPath);
        return KyberWeaveYamlParser.Parse(yaml);
    }

    /// <summary>Merges the <c>ontology:</c> section of a config file over <paramref name="defaults"/>.</summary>
    public static OntologyConfig LoadOntologyMerged(OntologyConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);

        KyberWeaveYamlDocument document = ParseFile(yamlPath);
        return OntologyConfigLoader.Merge(defaults, document.Ontology);
    }

    /// <summary>Loads the <c>ontology:</c> section of a config file, reporting a failure instead of throwing.</summary>
    public static OntologyConfigLoadResult TryLoadOntology(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);

        try
        {
            KyberWeaveYamlDocument document = ParseFile(yamlPath);
            return OntologyConfigLoadResult.Ok(OntologyConfigLoader.Merge(OntologyConfig.ProductDefaults, document.Ontology));
        }
        catch (YamlException ex)
        {
            return OntologyConfigLoadResult.Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            // YamlDotNet throws ArgumentException for a null mapping key (e.g. required-keys
            // with an empty key). Surface it as a config failure rather than an unhandled crash.
            return OntologyConfigLoadResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OntologyConfigLoadResult.Fail(ex.Message);
        }
    }

    /// <summary>Merges the <c>harness:</c> section of a config file over the product defaults.</summary>
    public static HarnessProfileConfig LoadHarness(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);
        KyberWeaveYamlDocument document = ParseFile(yamlPath);
        return HarnessProfileConfigLoader.Merge(HarnessProfileConfig.ProductDefaults, document.Harness);
    }

    /// <summary>Merges the <c>squad:</c> section of a config file over <paramref name="defaults"/>.</summary>
    public static SquadConfig LoadSquadMerged(SquadConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);

        KyberWeaveYamlDocument document = ParseFile(yamlPath);
        return SquadConfigLoader.Merge(defaults, document.Squad);
    }
}
