namespace KyberWeave.Core.Configuration;

/// <summary>The <c>docs-analysis:</c> section of <c>kyber-weave.yml</c>.</summary>
internal sealed class DocsAnalysisYamlSection
{
    public List<string>? Statuses { get; set; }

    public string? GlossaryPath { get; set; }

    public double? VerdictConfidence { get; set; }

    public DocsAnalysisSearchYamlSection? Search { get; set; }

    public DocsAnalysisEmbeddingYamlSection? Embeddings { get; set; }
}

internal sealed class DocsAnalysisSearchYamlSection
{
    public string? Mode { get; set; }

    public int? MinClaimTokens { get; set; }

    public double? LexicalCandidateThreshold { get; set; }

    public double? LexicalDuplicateThreshold { get; set; }

    public double? SemanticCandidateThreshold { get; set; }

    public double? SemanticDuplicateThreshold { get; set; }

    public double? TerminologyContextThreshold { get; set; }

    public int? MaxNeighborsPerClaim { get; set; }

    public int? MaxCodeNeighbors { get; set; }

    public int? MaxCandidates { get; set; }
}

internal sealed class DocsAnalysisEmbeddingYamlSection
{
    public string? Mode { get; set; }

    public string? Endpoint { get; set; }

    public string? Model { get; set; }

    public int? Dimensions { get; set; }

    public int? BatchSize { get; set; }

    public int? TimeoutSeconds { get; set; }

    public string? ApiKeyEnv { get; set; }
}
