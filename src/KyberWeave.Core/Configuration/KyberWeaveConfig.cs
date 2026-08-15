namespace KyberWeave.Core.Configuration;

/// <summary>Combined Kyber-Weave host configuration.</summary>
public sealed class KyberWeaveConfig
{
    public OntologyConfig Ontology { get; init; } = OntologyConfig.ProductDefaults;

    public HarnessProfileConfig Harness { get; init; } = HarnessProfileConfig.ProductDefaults;

    public DocsAnalysisConfig DocsAnalysis { get; init; } = DocsAnalysisConfig.ProductDefaults;

    public static KyberWeaveConfig ProductDefaults { get; } = new();
}
