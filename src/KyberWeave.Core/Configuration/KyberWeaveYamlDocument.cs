namespace KyberWeave.Core.Configuration;

/// <summary>Root shape of a <c>kyber-weave.yml</c> file.</summary>
internal sealed class KyberWeaveYamlDocument
{
    public OntologyYamlSection? Ontology { get; set; }

    public HarnessYamlSection? Harness { get; set; }

    public DocsAnalysisYamlSection? DocsAnalysis { get; set; }
}
