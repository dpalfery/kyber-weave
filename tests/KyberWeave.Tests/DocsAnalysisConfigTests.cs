using KyberWeave.Core.Configuration;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// T01 — documentation-analysis configuration defaults, merge semantics, and validation.
/// Invalid analysis settings must fail host configuration loading rather than silently
/// falling back to a cheaper or broader analysis mode.
/// </summary>
public class DocsAnalysisConfigTests
{
    [Fact]
    public void ProductDefaults_ExactlyMatchTheBoundedAnalysisPreset()
    {
        var config = DocsAnalysisConfig.ProductDefaults;

        Assert.Equal(["current"], config.Statuses);
        Assert.Null(config.GlossaryPath);
        Assert.Equal(0.80, config.VerdictConfidence);

        Assert.Equal(DocsAnalysisSearchMode.Hybrid, config.Search.Mode);
        Assert.Equal(5, config.Search.MinClaimTokens);
        Assert.Equal(0.45, config.Search.LexicalCandidateThreshold);
        Assert.Equal(0.90, config.Search.LexicalDuplicateThreshold);
        Assert.Equal(0.78, config.Search.SemanticCandidateThreshold);
        Assert.Equal(0.92, config.Search.SemanticDuplicateThreshold);
        Assert.Equal(0.30, config.Search.TerminologyContextThreshold);
        Assert.Equal(10, config.Search.MaxNeighborsPerClaim);
        Assert.Equal(50, config.Search.MaxCodeNeighbors);
        Assert.Equal(500, config.Search.MaxCandidates);

        Assert.Equal(DocsAnalysisEmbeddingMode.Off, config.Embeddings.Mode);
        Assert.Null(config.Embeddings.Endpoint);
        Assert.Null(config.Embeddings.Model);
        Assert.Null(config.Embeddings.Dimensions);
        Assert.Equal(64, config.Embeddings.BatchSize);
        Assert.Equal(60, config.Embeddings.TimeoutSeconds);
        Assert.Null(config.Embeddings.ApiKeyEnv);
    }

    [Fact]
    public void LoadFromYaml_ParsesEveryDocsAnalysisSetting()
    {
        var config = KyberWeaveConfigLoader.LoadFromYaml("""
            ontology:
              docs-root: [docs, components/gameplay/docs]
            docs-analysis:
              statuses: [draft, needs-review]
              glossary-path: components/gameplay/docs/terms.md
              verdict-confidence: 0.73
              search:
                mode: high-recall
                min-claim-tokens: 8
                lexical-candidate-threshold: 0.46
                lexical-duplicate-threshold: 0.91
                semantic-candidate-threshold: 0.79
                semantic-duplicate-threshold: 0.93
                terminology-context-threshold: 0.29
                max-neighbors-per-claim: 11
                max-code-neighbors: 51
                max-candidates: 501
              embeddings:
                mode: prefer
                endpoint: http://localhost:1234/v1/embeddings
                model: text-embedding-local
                dimensions: 768
                batch-size: 32
                timeout-seconds: 45
                api-key-env: LOCAL_EMBEDDING_TOKEN
            """);

        var analysis = config.DocsAnalysis;
        Assert.Equal(["draft", "needs-review"], analysis.Statuses);
        Assert.Equal("components/gameplay/docs/terms.md", analysis.GlossaryPath);
        Assert.Equal(0.73, analysis.VerdictConfidence);
        Assert.Equal(DocsAnalysisSearchMode.HighRecall, analysis.Search.Mode);
        Assert.Equal(8, analysis.Search.MinClaimTokens);
        Assert.Equal(0.46, analysis.Search.LexicalCandidateThreshold);
        Assert.Equal(0.91, analysis.Search.LexicalDuplicateThreshold);
        Assert.Equal(0.79, analysis.Search.SemanticCandidateThreshold);
        Assert.Equal(0.93, analysis.Search.SemanticDuplicateThreshold);
        Assert.Equal(0.29, analysis.Search.TerminologyContextThreshold);
        Assert.Equal(11, analysis.Search.MaxNeighborsPerClaim);
        Assert.Equal(51, analysis.Search.MaxCodeNeighbors);
        Assert.Equal(501, analysis.Search.MaxCandidates);
        Assert.Equal(DocsAnalysisEmbeddingMode.Prefer, analysis.Embeddings.Mode);
        Assert.Equal(new Uri("http://localhost:1234/v1/embeddings"), analysis.Embeddings.Endpoint);
        Assert.Equal("text-embedding-local", analysis.Embeddings.Model);
        Assert.Equal(768, analysis.Embeddings.Dimensions);
        Assert.Equal(32, analysis.Embeddings.BatchSize);
        Assert.Equal(45, analysis.Embeddings.TimeoutSeconds);
        Assert.Equal("LOCAL_EMBEDDING_TOKEN", analysis.Embeddings.ApiKeyEnv);
    }

    [Fact]
    public void LoadFromYaml_ReplacesStatusesAndRetainsUnspecifiedNestedDefaults()
    {
        var config = KyberWeaveConfigLoader.LoadFromYaml("""
            ontology:
              statuses: [current, editorial]
            docs-analysis:
              statuses: [editorial]
              search:
                max-candidates: 25
              embeddings:
                batch-size: 16
            """).DocsAnalysis;

        Assert.Equal(["editorial"], config.Statuses);
        Assert.Equal(25, config.Search.MaxCandidates);
        Assert.Equal(DocsAnalysisSearchMode.Hybrid, config.Search.Mode);
        Assert.Equal(0.45, config.Search.LexicalCandidateThreshold);
        Assert.Equal(16, config.Embeddings.BatchSize);
        Assert.Equal(DocsAnalysisEmbeddingMode.Off, config.Embeddings.Mode);
        Assert.Equal(60, config.Embeddings.TimeoutSeconds);
    }

    [Fact]
    public void LoadFromYaml_WhenAnalysisStatusIsNotInMergedOntology_RejectsIt()
    {
        var exception = AssertInvalid("""
            ontology:
              statuses: [current, editorial]
            docs-analysis:
              statuses: [draft]
            """);

        Assert.Contains("docs-analysis.statuses", exception.Message, StringComparison.Ordinal);
        Assert.Contains("draft", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidThresholds))]
    public void LoadFromYaml_WhenThresholdIsNotFiniteAndBetweenZeroAndOne_RejectsIt(
        string yamlKey,
        bool underSearch,
        string invalidValue)
    {
        var yaml = underSearch
            ? $"docs-analysis:\n  search:\n    {yamlKey}: {invalidValue}\n"
            : $"docs-analysis:\n  {yamlKey}: {invalidValue}\n";

        var exception = AssertInvalid(yaml);

        Assert.Contains(yamlKey, exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, bool, string> InvalidThresholds()
    {
        var data = new TheoryData<string, bool, string>();
        foreach (var (key, underSearch) in new[]
                 {
                     ("verdict-confidence", false),
                     ("lexical-candidate-threshold", true),
                     ("lexical-duplicate-threshold", true),
                     ("semantic-candidate-threshold", true),
                     ("semantic-duplicate-threshold", true),
                     ("terminology-context-threshold", true)
                 })
        {
            data.Add(key, underSearch, "-.inf");
            data.Add(key, underSearch, ".nan");
            data.Add(key, underSearch, "-0.01");
            data.Add(key, underSearch, "1.01");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NonPositiveIntegerSettings))]
    public void LoadFromYaml_WhenIntegerSettingIsNotPositive_RejectsIt(
        string section,
        string yamlKey)
    {
        var exception = AssertInvalid($"""
            docs-analysis:
              {section}:
                {yamlKey}: 0
            """);

        Assert.Contains(yamlKey, exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> NonPositiveIntegerSettings() => new()
    {
        { "search", "min-claim-tokens" },
        { "search", "max-neighbors-per-claim" },
        { "search", "max-code-neighbors" },
        { "search", "max-candidates" },
        { "embeddings", "dimensions" },
        { "embeddings", "batch-size" },
        { "embeddings", "timeout-seconds" }
    };

    [Theory]
    [InlineData("prefer")]
    [InlineData("required")]
    public void LoadFromYaml_WhenEnabledEmbeddingsOmitEndpoint_RejectsIt(string mode)
    {
        var exception = AssertInvalid($"""
            docs-analysis:
              embeddings:
                mode: {mode}
                model: text-embedding-local
            """);

        Assert.Contains("endpoint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("prefer")]
    [InlineData("required")]
    public void LoadFromYaml_WhenEnabledEmbeddingsOmitModel_RejectsIt(string mode)
    {
        var exception = AssertInvalid($"""
            docs-analysis:
              embeddings:
                mode: {mode}
                endpoint: http://127.0.0.1:1234/v1/embeddings
            """);

        Assert.Contains("model", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("prefer", "/v1/embeddings")]
    [InlineData("required", "ftp://127.0.0.1/embeddings")]
    [InlineData("prefer", "https://example.com/v1/embeddings")]
    public void LoadFromYaml_WhenEmbeddingEndpointIsNotAbsoluteLoopbackHttp_RejectsIt(
        string mode,
        string endpoint)
    {
        var exception = AssertInvalid($"""
            docs-analysis:
              embeddings:
                mode: {mode}
                endpoint: {endpoint}
                model: text-embedding-local
            """);

        Assert.Contains("endpoint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/tmp/glossary.md")]
    [InlineData("../glossary.md")]
    [InlineData("other-docs/glossary.md")]
    public void LoadFromYaml_WhenGlossaryPathIsOutsideConfiguredDocsRoots_RejectsIt(string path)
    {
        var exception = AssertInvalid($"""
            ontology:
              docs-root: [docs, components/gameplay/docs]
            docs-analysis:
              glossary-path: {path}
            """);

        Assert.Contains("glossary-path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadFromYaml_WhenGlossaryPathIsUnderAnyConfiguredDocsRoot_AcceptsIt()
    {
        var config = KyberWeaveConfigLoader.LoadFromYaml("""
            ontology:
              docs-root: [docs, components/gameplay/docs]
            docs-analysis:
              glossary-path: components/gameplay/docs/glossary.md
            """);

        Assert.Equal("components/gameplay/docs/glossary.md", config.DocsAnalysis.GlossaryPath);
    }

    private static YamlException AssertInvalid(string yaml) =>
        Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigLoader.LoadFromYaml(yaml));
}
