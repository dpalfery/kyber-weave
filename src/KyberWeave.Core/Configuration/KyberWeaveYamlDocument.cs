namespace KyberWeave.Core.Configuration;

/// <summary>Root shape of a <c>kyber-weave.yml</c> file.</summary>
internal sealed class KyberWeaveYamlDocument
{
    public OntologyYamlSection? Ontology { get; set; }

    public HarnessYamlSection? Harness { get; set; }

    public DocsAnalysisYamlSection? DocsAnalysis { get; set; }

    public SquadYamlSection? Squad { get; set; }

    public ReviewYamlSection? Review { get; set; }

    /// <summary>
    /// Host additions to the configuration registry, as property name to repository-relative
    /// path. Bound as a plain map because the property names are the host's vocabulary, not
    /// a schema this type could enumerate.
    /// </summary>
    public Dictionary<string, string>? ConfigReg { get; set; }
}
