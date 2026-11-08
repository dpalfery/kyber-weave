namespace KyberWeave.Core.Configuration;

/// <summary>Root shape of a <c>kyber-weave.yml</c> file.</summary>
internal sealed class KyberWeaveYamlDocument
{
    public OntologyYamlSection? Ontology { get; set; }

    public HarnessYamlSection? Harness { get; set; }

    public DocsAnalysisYamlSection? DocsAnalysis { get; set; }

    public SquadYamlSection? Squad { get; set; }

    public ReviewYamlSection? Review { get; set; }

    public ArbiterYamlSection? Arbiter { get; set; }

    public McpYamlSection? Mcp { get; set; }

    /// <summary>
    /// Host additions to the configuration registry, as property name to repository-relative
    /// path. Bound as a plain map because the property names are the host's vocabulary, not
    /// a schema this type could enumerate.
    /// </summary>
    public Dictionary<string, string>? ConfigReg { get; set; }

    /// <summary>
    /// Raw <c>autonomy:</c> value. Kept a string so a value outside the two known settings
    /// reaches the loader as a configuration error rather than a deserialization failure.
    /// </summary>
    public string? Autonomy { get; set; }

    /// <summary>
    /// Raw top-level <c>stop-on-tool-failure:</c> value, for the same reason as
    /// <see cref="Autonomy"/>: a value that is neither <c>true</c> nor <c>false</c> has to reach
    /// the loader as a diagnostic naming the setting, not as a binding failure naming a type.
    /// </summary>
    public string? StopOnToolFailure { get; set; }
}
