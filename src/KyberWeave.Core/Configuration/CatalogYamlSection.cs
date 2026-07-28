namespace KyberWeave.Core.Configuration;

/// <summary>Catalog column mapping under <c>ontology.catalog</c>.</summary>
internal sealed class CatalogYamlSection
{
    public int? ComponentColumn { get; set; }

    public int? OwnerColumn { get; set; }
}
