namespace KyberWeave.Core.Configuration;

/// <summary>The <c>ontology:</c> section of <c>kyber-weave.yml</c>.</summary>
internal sealed class OntologyYamlSection
{
    public string? DocsRoot { get; set; }

    public List<string>? ExcludedSegments { get; set; }

    public List<string>? ExcludedFiles { get; set; }

    public List<string>? DocTypes { get; set; }

    public List<string>? Statuses { get; set; }

    public List<string>? BaseRequiredKeys { get; set; }

    public Dictionary<string, List<string>?>? RequiredKeys { get; set; }

    public CatalogYamlSection? Catalog { get; set; }
}
