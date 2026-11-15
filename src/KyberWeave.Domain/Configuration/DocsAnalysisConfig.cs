namespace KyberWeave.Core.Configuration;

/// <summary>Host configuration for bounded documentation analysis.</summary>
public sealed class DocsAnalysisConfig
{
    private static readonly string[] DefaultStatuses = ["current"];

    public IReadOnlyList<string> Statuses { get; init; } = DefaultStatuses;

    /// <summary>
    /// Repository-relative glossary path. When omitted, analysis uses the primary
    /// documentation root's <c>glossary.md</c>.
    /// </summary>
    public string? GlossaryPath { get; init; }

    /// <summary>Effective path including the primary-root default resolved at config load.</summary>
    public string ResolvedGlossaryPath { get; private init; } = "glossary.md";

    /// <summary>Resolves an omitted path to the primary documentation root.</summary>
    public string ResolveGlossaryPath(OntologyConfig ontology)
    {
        ArgumentNullException.ThrowIfNull(ontology);
        return string.IsNullOrWhiteSpace(GlossaryPath)
            ? ontology.DocsRoot == "."
                ? "glossary.md"
                : $"{ontology.DocsRoot.TrimEnd('/')}/glossary.md"
            : GlossaryPath.Replace('\\', '/');
    }

    /// <summary>Returns this analysis configuration with ontology-derived paths resolved.</summary>
    public DocsAnalysisConfig ResolveFor(OntologyConfig ontology) =>
        Clone(resolvedGlossaryPath: ResolveGlossaryPath(ontology));

    public double VerdictConfidence { get; private init; } = 0.80;

    public DocsAnalysisSearchConfig Search { get; init; } = DocsAnalysisSearchConfig.ProductDefaults;

    public DocsAnalysisEmbeddingConfig Embeddings { get; init; } =
        DocsAnalysisEmbeddingConfig.ProductDefaults;

    public static DocsAnalysisConfig ProductDefaults { get; } = new();

    internal DocsAnalysisConfig Clone(
        IReadOnlyList<string>? statuses = null,
        string? glossaryPath = null,
        string? resolvedGlossaryPath = null,
        double? verdictConfidence = null,
        DocsAnalysisSearchConfig? search = null,
        DocsAnalysisEmbeddingConfig? embeddings = null) =>
        new()
        {
            Statuses = statuses ?? Statuses,
            GlossaryPath = glossaryPath ?? GlossaryPath,
            ResolvedGlossaryPath = resolvedGlossaryPath ?? ResolvedGlossaryPath,
            VerdictConfidence = verdictConfidence ?? VerdictConfidence,
            Search = search ?? Search,
            Embeddings = embeddings ?? Embeddings
        };
}

/// <summary>Candidate-generation preset used by documentation analysis.</summary>
public enum DocsAnalysisSearchMode
{
    Graph,
    Hybrid,
    HighRecall
}

/// <summary>Bounded candidate-generation settings.</summary>
public sealed class DocsAnalysisSearchConfig
{
    public DocsAnalysisSearchMode Mode { get; init; } = DocsAnalysisSearchMode.Hybrid;

    public int MinClaimTokens { get; init; } = 5;

    public double LexicalCandidateThreshold { get; init; } = 0.45;

    public double LexicalDuplicateThreshold { get; init; } = 0.90;

    public double SemanticCandidateThreshold { get; init; } = 0.78;

    public double SemanticDuplicateThreshold { get; init; } = 0.92;

    public double TerminologyContextThreshold { get; init; } = 0.30;

    public int MaxNeighborsPerClaim { get; init; } = 10;

    public int MaxCodeNeighbors { get; init; } = 50;

    public int MaxCandidates { get; init; } = 500;

    public static DocsAnalysisSearchConfig ProductDefaults { get; } = new();

    internal DocsAnalysisSearchConfig Clone(
        DocsAnalysisSearchMode? mode = null,
        int? minClaimTokens = null,
        double? lexicalCandidateThreshold = null,
        double? lexicalDuplicateThreshold = null,
        double? semanticCandidateThreshold = null,
        double? semanticDuplicateThreshold = null,
        double? terminologyContextThreshold = null,
        int? maxNeighborsPerClaim = null,
        int? maxCodeNeighbors = null,
        int? maxCandidates = null) =>
        new()
        {
            Mode = mode ?? Mode,
            MinClaimTokens = minClaimTokens ?? MinClaimTokens,
            LexicalCandidateThreshold = lexicalCandidateThreshold ?? LexicalCandidateThreshold,
            LexicalDuplicateThreshold = lexicalDuplicateThreshold ?? LexicalDuplicateThreshold,
            SemanticCandidateThreshold = semanticCandidateThreshold ?? SemanticCandidateThreshold,
            SemanticDuplicateThreshold = semanticDuplicateThreshold ?? SemanticDuplicateThreshold,
            TerminologyContextThreshold = terminologyContextThreshold ?? TerminologyContextThreshold,
            MaxNeighborsPerClaim = maxNeighborsPerClaim ?? MaxNeighborsPerClaim,
            MaxCodeNeighbors = maxCodeNeighbors ?? MaxCodeNeighbors,
            MaxCandidates = maxCandidates ?? MaxCandidates
        };
}

/// <summary>Failure policy for the optional local embedding endpoint.</summary>
public enum DocsAnalysisEmbeddingMode
{
    Off,
    Prefer,
    Required
}

/// <summary>Configuration for an OpenAI-compatible local embedding endpoint.</summary>
public sealed class DocsAnalysisEmbeddingConfig
{
    public DocsAnalysisEmbeddingMode Mode { get; init; } = DocsAnalysisEmbeddingMode.Off;

    public Uri? Endpoint { get; init; }

    public string? Model { get; init; }

    public int? Dimensions { get; init; }

    public int BatchSize { get; init; } = 64;

    public int TimeoutSeconds { get; init; } = 60;

    public string? ApiKeyEnv { get; init; }

    public static DocsAnalysisEmbeddingConfig ProductDefaults { get; } = new();

    internal DocsAnalysisEmbeddingConfig Clone(
        DocsAnalysisEmbeddingMode? mode = null,
        Uri? endpoint = null,
        string? model = null,
        int? dimensions = null,
        int? batchSize = null,
        int? timeoutSeconds = null,
        string? apiKeyEnv = null) =>
        new()
        {
            Mode = mode ?? Mode,
            Endpoint = endpoint ?? Endpoint,
            Model = model ?? Model,
            Dimensions = dimensions ?? Dimensions,
            BatchSize = batchSize ?? BatchSize,
            TimeoutSeconds = timeoutSeconds ?? TimeoutSeconds,
            ApiKeyEnv = apiKeyEnv ?? ApiKeyEnv
        };
}
