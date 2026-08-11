namespace KyberWeave.Core.Configuration;

/// <summary>The <c>ontology:</c> section of <c>kyber-weave.yml</c>.</summary>
internal sealed class OntologyYamlSection
{
    /// <summary>
    /// A directory, or a list of them. Bound untyped because the key accepts both shapes:
    /// the deserializer yields a <see cref="string"/> for a scalar and a list for a
    /// sequence, which <c>OntologyConfigLoader</c> normalizes. A typed
    /// <see cref="string"/> would fail a host's list with YamlDotNet's own message rather
    /// than one naming the key.
    /// </summary>
    public object? DocsRoot { get; set; }

    public string? CatalogPath { get; set; }

    public List<string>? ExcludedSegments { get; set; }

    public List<string>? ExcludedFiles { get; set; }

    public List<string>? DocTypes { get; set; }

    public List<string>? Statuses { get; set; }

    public List<string>? BaseRequiredKeys { get; set; }

    public Dictionary<string, List<string>?>? RequiredKeys { get; set; }

    public CatalogYamlSection? Catalog { get; set; }
}
