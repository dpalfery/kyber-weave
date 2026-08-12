using System.Collections.ObjectModel;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Defines the analysis engine contract independently of its CLI, MCP, persistence, and
/// embedding adapters. These tests intentionally use in-memory ports so candidate
/// generation and classification remain cheap and deterministic.
/// </summary>
public sealed class DocumentationAnalyzerTests
{
    [Fact]
    public void Analyze_FiltersConfiguredStatusesAndGlossaryBeforeExtractingClaims()
    {
        var documents = Set(
            Document("current", "docs/current.md", Status.Current, "Shared claim with enough useful words."),
            Document("draft", "docs/draft.md", Status.Draft, "Shared claim with enough useful words."),
            Document("glossary", "docs/glossary.md", Status.Current, "Shared claim with enough useful words."));
        var config = Config(statuses: ["current"], glossaryPath: "docs/glossary.md");

        var result = Analyzer().Analyze(documents, Graph(documents), config);

        Assert.Equal(1, result.Metrics.ExtractedClaims);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Analyze_DefaultResolvedGlossaryPathWithNoncanonicalIdentity_ExcludesGlossary()
    {
        var documents = Set(
            Document("current", "docs/current.md", Status.Current, "One current claim with enough useful words."),
            Document("custom-terms", "docs/glossary.md", Status.Current, "Glossary prose must never become an analysis claim."));
        var config = KyberWeaveConfigLoader.LoadFromYaml("""
            ontology:
              docs-root: docs
            """).DocsAnalysis;

        var result = Analyzer().Analyze(documents, Graph(documents), config);

        Assert.Equal(1, result.Metrics.ExtractedClaims);
    }

    [Fact]
    public void Analyze_ExactDuplicateClaimsAcrossUnrelatedDocuments_ReturnsOneGlobalCluster()
    {
        var documents = Set(
            Document("first", "docs/first.md", Status.Current, "The processor must retain every approved verdict."),
            Document("second", "docs/second.md", Status.Current, "THE processor must retain every approved verdict!"),
            Document("third", "docs/third.md", Status.Current, "The processor must retain every approved verdict."));

        var result = Analyzer().Analyze(documents, Graph(documents), Config(mode: DocsAnalysisSearchMode.Graph));

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(AnalysisRuleKind.Duplicate, candidate.Kind);
        Assert.True(candidate.IsExact);
        Assert.Equal(3, candidate.Claims.Count);
        var finding = Assert.Single(result.Diagnostics.Items, item => item.Code == DocumentationAnalyzer.DuplicateRuleCode);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(2, finding.RelatedLocations.Count);
    }

    [Fact]
    public void GraphCandidateSource_RelatedDocuments_ReturnsGraphWeightedLexicalEvidence()
    {
        var documents = Set(
            Document("left", "docs/left.md", Status.Current, "The runner records model token usage for automation.", component: "Runtime"),
            Document("right", "docs/right.md", Status.Current, "Automation runners record model token consumption.", component: "Runtime"));
        var claims = Extract(documents);
        var request = new ClaimCandidateSourceRequest(claims, Graph(documents), Config().Search);

        var result = new GraphClaimCandidateSource().FindCandidates(request);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal(CandidateSourceKind.Graph, pair.Source);
        Assert.Equal(1, pair.Score.Graph);
        Assert.True(pair.Score.Lexical >= Config().Search.LexicalCandidateThreshold);
        Assert.Equal(1, result.ComparisonCount);
    }

    [Fact]
    public void SparseLexicalCandidateSource_HybridSearchIsTopKBoundedWithoutAllPairs()
    {
        var documents = Set(Enumerable.Range(0, 100)
            .Select(index => Document(
                $"doc-{index}",
                $"docs/doc-{index}.md",
                Status.Current,
                $"Shared retrieval term group {index % 10} has unique value {index}."))
            .ToArray());
        var claims = Extract(documents);
        var config = Config(maxNeighbors: 2, lexicalCandidateThreshold: 0.10);
        var request = new ClaimCandidateSourceRequest(claims, Graph(documents), config.Search);

        var result = new SparseLexicalCandidateSource().FindCandidates(request);

        Assert.True(result.ComparisonCount < claims.Count * (claims.Count - 1) / 2);
        Assert.True(result.Pairs.Count <= claims.Count * config.Search.MaxNeighborsPerClaim);
        Assert.All(result.Pairs, pair => Assert.Equal(CandidateSourceKind.Lexical, pair.Source));
    }

    [Theory]
    [InlineData(DocsAnalysisSearchMode.Graph, 1, 0)]
    [InlineData(DocsAnalysisSearchMode.Hybrid, 1, 1)]
    [InlineData(DocsAnalysisSearchMode.HighRecall, 1, 1)]
    public void Analyze_SearchModeSelectsGraphAndLexicalCandidateSources(
        DocsAnalysisSearchMode mode,
        int expectedGraphCalls,
        int expectedLexicalCalls)
    {
        var documents = Set(
            Document("left", "docs/left.md", Status.Current, "The runtime wrapper measures elapsed execution time."),
            Document("right", "docs/right.md", Status.Current, "The execution wrapper measures elapsed runtime."));
        var graph = new RecordingCandidateSource(CandidateSourceKind.Graph);
        var lexical = new RecordingCandidateSource(CandidateSourceKind.Lexical);

        Analyzer([graph, lexical]).Analyze(documents, Graph(documents), Config(mode: mode));

        Assert.Equal(expectedGraphCalls, graph.CallCount);
        Assert.Equal(expectedLexicalCalls, lexical.CallCount);
        Assert.All(graph.RequestedModes, requested => Assert.Equal(mode, requested));
        Assert.All(lexical.RequestedModes, requested => Assert.Equal(mode, requested));
    }

    [Theory]
    [InlineData("The runner must emit a token report.", "The runner must not emit a token report.")]
    [InlineData("The runner must emit a token report.", "The runner may emit a token report.")]
    [InlineData("Use protocol version 1 for every request.", "Use protocol version 2 for every request.")]
    [InlineData("Send requests to /api/v1/report.", "Send requests to /api/v2/report.")]
    [InlineData("Run `dotnet test` before review.", "Run `npm test` before review.")]
    [InlineData("Set `ExecutionMode.Local` for analysis.", "Set `ExecutionMode.Remote` for analysis.")]
    public void Analyze_GraphRelatedClaimsWithConflictSignals_ReturnsPendingConflict(
        string leftText,
        string rightText)
    {
        var documents = Set(
            Document("left", "docs/left.md", Status.Current, leftText, component: "Runtime"),
            Document("right", "docs/right.md", Status.Current, rightText, component: "Runtime"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.60, graph: 1);

        var result = Analyzer([source]).Analyze(documents, Graph(documents), Config());

        var conflict = Assert.Single(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
        Assert.False(conflict.IsExact);
        var finding = Assert.Single(result.Diagnostics.Items, item => item.Code == DocumentationAnalyzer.ConflictRuleCode);
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void Analyze_FencedCodeClaimsWithDifferentCommands_ReturnsPendingConflict()
    {
        var documents = Set(
            Document("left", "docs/left.md", Status.Current, "```sh\ndotnet test\n```", component: "Runtime"),
            Document("right", "docs/right.md", Status.Current, "```sh\nnpm test\n```", component: "Runtime"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.50, graph: 1);

        var result = Analyzer([source]).Analyze(documents, Graph(documents), Config(minClaimTokens: 1));

        var conflict = Assert.Single(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
        Assert.All(conflict.Claims, claim => Assert.Equal(ClaimKind.CodeBlock, claim.Kind));
    }

    [Fact]
    public void Analyze_InformativeTermInDivergentDocumentContexts_ReturnsTerminologyCandidate()
    {
        var documents = Set(
            Document(
                "gameplay-loop",
                "docs/gameplay.md",
                Status.Current,
                "The gameplay loop wraps the live-test executable and measures runtime.",
                component: "Gameplay",
                section: "Live testing"),
            Document(
                "codex-loop",
                "docs/codex.md",
                Status.Current,
                "The Codex loop repeatedly churns autonomous tasks and consumes model tokens.",
                component: "Automation",
                section: "Agent execution"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Lexical, lexical: 0.20, graph: 0);

        var result = Analyzer([source]).Analyze(documents, Graph(documents), Config());

        var terminology = Assert.Single(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Terminology);
        Assert.Equal("loop", terminology.Term);
        Assert.Equal(2, terminology.Claims.Count);
        Assert.Contains(
            result.Diagnostics.Items,
            item => item.Code == DocumentationAnalyzer.TerminologyRuleCode && item.Severity == Severity.Warning);
    }

    [Fact]
    public void Analyze_HighConfidenceDuplicateVerdictPromotesNearDuplicateToWarning()
    {
        var documents = NearDuplicateDocuments();
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.95, graph: 1);
        var pending = Analyzer([source]).Analyze(documents, Graph(documents), Config());
        var candidate = Assert.Single(pending.Candidates, item => item.Kind == AnalysisRuleKind.Duplicate);
        var persistence = new StubPersistence(new AnalysisVerdict(
            candidate.Id,
            AnalysisVerdictLabel.Duplicate,
            0.90,
            "Both claims impose the same requirement."));

        var reviewed = Analyzer([source], persistence).Analyze(documents, Graph(documents), Config());

        var finding = Assert.Single(reviewed.Diagnostics.Items, item => item.Code == DocumentationAnalyzer.DuplicateRuleCode);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Fact]
    public void Analyze_HighConfidenceConflictVerdictPromotesConflictToError()
    {
        var documents = Set(
            Document("left", "docs/left.md", Status.Current, "The runner must emit token usage.", component: "Runtime"),
            Document("right", "docs/right.md", Status.Current, "The runner must not emit token usage.", component: "Runtime"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.70, graph: 1);
        var pending = Analyzer([source]).Analyze(documents, Graph(documents), Config());
        var candidate = Assert.Single(pending.Candidates, item => item.Kind == AnalysisRuleKind.Conflict);
        var persistence = new StubPersistence(new AnalysisVerdict(
            candidate.Id,
            AnalysisVerdictLabel.Conflict,
            0.90,
            "The obligations cannot both hold in the same runtime scope."));

        var reviewed = Analyzer([source], persistence).Analyze(documents, Graph(documents), Config());

        var finding = Assert.Single(reviewed.Diagnostics.Items, item => item.Code == DocumentationAnalyzer.ConflictRuleCode);
        Assert.Equal(Severity.Error, finding.Severity);
    }

    [Theory]
    [InlineData(AnalysisVerdictLabel.Benign, 0.90, 0)]
    [InlineData(AnalysisVerdictLabel.Benign, 0.79, 1)]
    [InlineData(AnalysisVerdictLabel.Uncertain, 0.95, 1)]
    public void Analyze_VerdictLabelAndConfidenceControlCandidateSuppression(
        AnalysisVerdictLabel label,
        double confidence,
        int expectedCandidates)
    {
        var documents = NearDuplicateDocuments();
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.95, graph: 1);
        var pending = Analyzer([source]).Analyze(documents, Graph(documents), Config());
        var candidate = Assert.Single(pending.Candidates, item => item.Kind == AnalysisRuleKind.Duplicate);
        var persistence = new StubPersistence(new AnalysisVerdict(
            candidate.Id,
            label,
            confidence,
            "Review disposition."));

        var reviewed = Analyzer([source], persistence).Analyze(documents, Graph(documents), Config());

        Assert.Equal(expectedCandidates, reviewed.Candidates.Count);
    }

    [Fact]
    public void Analyze_ApprovedScopedGlossarySensesCoverEveryOccurrence_SuppressesTerminologyWarning()
    {
        var documents = Set(
            Document("gameplay", "docs/gameplay.md", Status.Current, "The gameplay loop measures live-test runtime.", component: "Gameplay"),
            Document("automation", "docs/automation.md", Status.Current, "The Codex loop consumes model tokens.", component: "Automation"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Lexical, lexical: 0.10, graph: 0);
        var glossary = new AnalysisGlossary(
        [
            new ApprovedGlossarySense(
                "loop-gameplay",
                "loop",
                "The gameplay live-test wrapper.",
                ["component:Gameplay"],
                ["gameplay loop"]),
            new ApprovedGlossarySense(
                "loop-codex",
                "loop",
                "The autonomous Codex churn cycle.",
                ["component:Automation"],
                ["Codex loop"])
        ]);

        var result = Analyzer([source]).Analyze(documents, Graph(documents), Config(), glossary);

        Assert.DoesNotContain(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Terminology);
        Assert.DoesNotContain(result.Diagnostics.Items, item => item.Code == DocumentationAnalyzer.TerminologyRuleCode);
    }

    [Fact]
    public void Analyze_ApprovedCodeRefScopedSensesCoverEveryOccurrence_SuppressesTerminologyWarning()
    {
        var documents = Set(
            Document(
                "gameplay",
                "docs/gameplay.md",
                Status.Current,
                "The gameplay loop measures live-test runtime.",
                component: "Runtime",
                codeRefs: ["Game.Run"]),
            Document(
                "automation",
                "docs/automation.md",
                Status.Current,
                "The Codex loop consumes model tokens.",
                component: "Runtime",
                codeRefs: ["Agent.Run"]));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Lexical, lexical: 0.10, graph: 0);
        var glossary = new AnalysisGlossary(
        [
            new ApprovedGlossarySense(
                "loop-gameplay",
                "loop",
                "The gameplay live-test wrapper.",
                ["code-ref:Game.Run"],
                ["gameplay loop"]),
            new ApprovedGlossarySense(
                "loop-codex",
                "loop",
                "The autonomous Codex churn cycle.",
                ["code-ref:Agent.Run"],
                ["Codex loop"])
        ]);

        var result = Analyzer([source]).Analyze(documents, Graph(documents), Config(), glossary);

        Assert.DoesNotContain(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Terminology);
        Assert.DoesNotContain(result.Diagnostics.Items, item => item.Code == DocumentationAnalyzer.TerminologyRuleCode);
    }

    [Fact]
    public void CandidateId_UsesKindTermSortedContentHashesAndAnalyzerRubricVersions()
    {
        var first = AnalysisCandidateId.Compute(
            AnalysisRuleKind.Terminology,
            "loop",
            ["hash-b", "hash-a"],
            "analyzer/v1",
            "rubric/v1");
        var reordered = AnalysisCandidateId.Compute(
            AnalysisRuleKind.Terminology,
            "loop",
            ["hash-a", "hash-b"],
            "analyzer/v1",
            "rubric/v1");

        Assert.Equal(first, reordered);
        Assert.Matches("^[a-f0-9]{64}$", first);
        Assert.NotEqual(first, AnalysisCandidateId.Compute(
            AnalysisRuleKind.Terminology,
            "cycle",
            ["hash-a", "hash-b"],
            "analyzer/v1",
            "rubric/v1"));
        Assert.NotEqual(first, AnalysisCandidateId.Compute(
            AnalysisRuleKind.Terminology,
            "loop",
            ["hash-a", "hash-b"],
            "analyzer/v2",
            "rubric/v1"));
        Assert.NotEqual(first, AnalysisCandidateId.Compute(
            AnalysisRuleKind.Terminology,
            "loop",
            ["hash-a", "hash-b"],
            "analyzer/v1",
            "rubric/v2"));
    }

    [Fact]
    public void Analyze_InvalidIgnoreMarkupPropagatesOperationalRule004()
    {
        var documents = Set(Document(
            "invalid",
            "docs/invalid.md",
            Status.Current,
            "<kyber-ignore rule=\"unknown\">Claim text.</kyber-ignore>"));

        var result = Analyzer().Analyze(documents, Graph(documents), Config());

        Assert.Contains(
            result.Diagnostics.Items,
            item => item.Code == DocumentationAnalyzer.IgnoreMarkupRuleCode && item.Severity == Severity.Error);
    }

    [Fact]
    public void DocumentationAnalyzer_ReservesPermanentAnalysisRuleIds()
    {
        Assert.Equal("KW-DOC-ANALYSIS-001", DocumentationAnalyzer.DuplicateRuleCode);
        Assert.Equal("KW-DOC-ANALYSIS-002", DocumentationAnalyzer.ConflictRuleCode);
        Assert.Equal("KW-DOC-ANALYSIS-003", DocumentationAnalyzer.TerminologyRuleCode);
        Assert.Equal("KW-DOC-ANALYSIS-004", DocumentationAnalyzer.IgnoreMarkupRuleCode);
        Assert.Equal("KW-DOC-ANALYSIS-005", DocumentationAnalyzer.CodeGraphUnavailableRuleCode);
        Assert.Equal("KW-DOC-ANALYSIS-006", DocumentationAnalyzer.EmbeddingUnavailableRuleCode);
    }

    [Fact]
    public void Analyze_ReportsCandidateSourceMetricsAndAppliesGlobalCandidateCap()
    {
        var documents = Set(Enumerable.Range(0, 6)
            .Select(index => Document(
                $"doc-{index}",
                $"docs/doc-{index}.md",
                Status.Current,
                $"The runner records token usage for execution variant {index}."))
            .ToArray());
        var graph = new AdjacentPairCandidateSource(CandidateSourceKind.Graph, comparisonCount: 5);
        var lexical = new AdjacentPairCandidateSource(CandidateSourceKind.Lexical, comparisonCount: 7);

        var result = Analyzer([graph, lexical]).Analyze(
            documents,
            Graph(documents),
            Config(maxCandidates: 2, lexicalDuplicateThreshold: 0.90));

        Assert.Equal(2, result.Candidates.Count);
        Assert.True(result.Metrics.Truncated);
        Assert.Equal(6, result.Metrics.ExtractedClaims);
        Assert.Equal(5, result.Metrics.GraphComparisons);
        Assert.Equal(7, result.Metrics.LexicalComparisons);
        Assert.True(result.Metrics.GraphCandidates > 0);
        Assert.True(result.Metrics.LexicalCandidates > 0);
        Assert.Equal(0, result.Metrics.EmbeddingComparisons);
        Assert.Equal(0, result.Metrics.EmbeddingCandidates);
    }

    [Fact]
    public void Analyze_WhenEmbeddingsAreEnabled_GeneratesCachesAndSuppliesSemanticCandidates()
    {
        var documents = Set(
            Document(
                "first",
                "docs/first.md",
                Status.Current,
                "Operators archive accepted adjudications for later use."),
            Document(
                "second",
                "docs/second.md",
                Status.Current,
                "The service retains approved review verdicts durably."));
        var lexical = new FirstPairCandidateSource(
            CandidateSourceKind.Lexical,
            lexical: 0.10,
            graph: 0);
        var generator = new SemanticMatchGenerator();
        var persistence = new EmbeddingPersistence();

        var result = Analyzer([lexical], persistence, generator).Analyze(
            documents,
            Graph(documents),
            Config(embeddings: new DocsAnalysisEmbeddingConfig
            {
                Mode = DocsAnalysisEmbeddingMode.Prefer,
                Endpoint = new Uri("http://127.0.0.1:1234/v1/embeddings"),
                Model = "semantic-test",
                Dimensions = 2
            }));

        var candidate = Assert.Single(
            result.Candidates,
            item => item.Kind == AnalysisRuleKind.Duplicate && !item.IsExact);
        Assert.Contains(CandidateSourceKind.Embedding, candidate.Sources);
        Assert.Equal(1, result.Metrics.EmbeddingComparisons);
        Assert.Equal(1, result.Metrics.EmbeddingCandidates);
        Assert.Equal(1, generator.CallCount);
        Assert.Equal(2, persistence.SavedEmbeddings.Count);
        Assert.DoesNotContain(
            result.Diagnostics.Items,
            item => item.Code == DocumentationAnalyzer.EmbeddingUnavailableRuleCode);
    }

    [Fact]
    public void AnalyzerPorts_ArePublicAndInfrastructureNeutral()
    {
        Assert.True(typeof(IClaimCandidateSource).IsInterface);
        Assert.True(typeof(IEmbeddingGenerator).IsInterface);
        Assert.True(typeof(IAnalysisPersistence).IsInterface);
        Assert.DoesNotContain(
            typeof(IAnalysisPersistence).GetMethods(),
            method => method.ReturnType.FullName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Analyze_DefaultSources_SurfaceDivergentTerminologyBelowDuplicateCandidateThreshold()
    {
        var documents = Set(
            Document(
                "gameplay",
                "docs/gameplay.md",
                Status.Current,
                "The gameplay loop wraps live testing and reports elapsed runtime.",
                section: "Gameplay testing"),
            Document(
                "automation",
                "docs/automation.md",
                Status.Current,
                "The Codex loop churns autonomous tasks and consumes model tokens.",
                section: "Agent automation"));

        var result = Analyzer().Analyze(
            documents,
            Graph(documents),
            Config(lexicalCandidateThreshold: 0.80));

        var terminology = Assert.Single(
            result.Candidates,
            candidate => candidate.Kind == AnalysisRuleKind.Terminology);
        Assert.Equal("loop", terminology.Term);
    }

    [Fact]
    public void DocGraphProjection_ExposesIndexedRelatedDocumentNeighborhoods()
    {
        var method = typeof(DocGraphProjection).GetMethod(
            "GetRelatedDocumentIds",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string)],
            modifiers: null);

        Assert.NotNull(method);
    }

    [Fact]
    public void GraphCandidateSource_ScoresEveryIndexedNeighborThenSelectsHighestTopK()
    {
        var documents = Set(
            Document("left", "docs/left.md", Status.Current, "Alpha beta gamma delta epsilon requirement.", component: "Runtime"),
            Document("early", "docs/early.md", Status.Current, "Alpha beta unrelated early candidate text.", component: "Runtime"),
            Document("middle", "docs/middle.md", Status.Current, "Alpha beta another candidate with noise.", component: "Runtime"),
            Document("best", "docs/best.md", Status.Current, "Alpha beta gamma delta epsilon guarantee.", component: "Runtime"));
        var claims = Extract(documents);
        var request = new ClaimCandidateSourceRequest(
            claims,
            Graph(documents),
            Config(maxNeighbors: 1, lexicalCandidateThreshold: 0.10).Search);

        var result = new GraphClaimCandidateSource().FindCandidates(request);

        Assert.True(result.ComparisonCount <= claims.Count);
        Assert.Contains(result.Pairs, pair =>
            PairIdentities(pair).SetEquals(["left", "best"]));
    }

    [Fact]
    public void Analyze_InlineCodeLiteralDifferencesRemainDetectableConflictEvidence()
    {
        var documents = Set(
            Document("local", "docs/local.md", Status.Current, "The analysis mode is `ExecutionMode.Local` for requests.", component: "Runtime"),
            Document("remote", "docs/remote.md", Status.Current, "The analysis mode is `ExecutionMode.Remote` for requests.", component: "Runtime"));
        var claims = Extract(documents);
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.70, graph: 1);

        var result = Analyzer([source]).Analyze(documents, Graph(documents), Config());

        Assert.Contains("ExecutionMode.Local", claims[0].Text, StringComparison.Ordinal);
        Assert.Contains("ExecutionMode.Remote", claims[1].ContextualText, StringComparison.Ordinal);
        Assert.Contains(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
    }

    [Fact]
    public void Analyze_SameContentPairAcrossLocations_MergesEveryDistinctEvidenceClaim()
    {
        var documents = Set(
            Document("left-one", "docs/left-one.md", Status.Current, "The processor retains every imported approved verdict."),
            Document("left-two", "docs/left-two.md", Status.Current, "The processor retains every imported approved verdict."),
            Document("right-one", "docs/right-one.md", Status.Current, "The processor must retain all approved imported verdicts."),
            Document("right-two", "docs/right-two.md", Status.Current, "The processor must retain all approved imported verdicts."));
        var source = new AllPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.95, graph: 1);

        var result = Analyzer([source]).Analyze(documents, Graph(documents), Config());

        var nearDuplicate = Assert.Single(
            result.Candidates,
            candidate => candidate.Kind == AnalysisRuleKind.Duplicate && !candidate.IsExact);
        Assert.Equal(4, nearDuplicate.Claims.Count);
        Assert.Equal(4, nearDuplicate.Claims.Select(claim => claim.FilePath).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SparseLexicalCandidateSource_HighRecallBroadensHybridPoolAndReportsComparisons()
    {
        var documents = Set(Enumerable.Range(0, 20)
            .Select(index => Document(
                $"doc-{index}",
                $"docs/doc-{index}.md",
                Status.Current,
                index is 0 or 19
                    ? $"Alpha beta gamma critical-token-{index} governs runtime behavior."
                    : $"Alpha beta gamma filler-token-{index} documents unrelated behavior."))
            .ToArray());
        var claims = Extract(documents);
        var source = new SparseLexicalCandidateSource();
        var hybrid = source.FindCandidates(new ClaimCandidateSourceRequest(
            claims,
            Graph(documents),
            Config(
                mode: DocsAnalysisSearchMode.Hybrid,
                maxNeighbors: 1,
                lexicalCandidateThreshold: 0.40).Search));
        var highRecall = source.FindCandidates(new ClaimCandidateSourceRequest(
            claims,
            Graph(documents),
            Config(
                mode: DocsAnalysisSearchMode.HighRecall,
                maxNeighbors: 1,
                lexicalCandidateThreshold: 0.40).Search));

        Assert.True(highRecall.ComparisonCount > hybrid.ComparisonCount);
        Assert.Contains(highRecall.Pairs, pair => PairIdentities(pair).SetEquals(["doc-0", "doc-19"]));
    }

    [Fact]
    public void Analyze_TerminologyClustersOneInformativeTermAcrossAllDivergentContexts()
    {
        var documents = Set(
            Document("gameplay", "docs/gameplay.md", Status.Current, "The gameplay loop wraps live testing documentation.", section: "Gameplay"),
            Document("automation", "docs/automation.md", Status.Current, "The autonomous loop churns agent tasks documentation.", section: "Automation"),
            Document("desktop", "docs/desktop.md", Status.Current, "The event loop schedules UI callbacks documentation.", section: "Desktop"));
        var source = new AllPairCandidateSource(CandidateSourceKind.Lexical, lexical: 0.10, graph: 0);

        var result = Analyzer([source]).Analyze(documents, Graph(documents), Config());

        var terminology = Assert.Single(
            result.Candidates,
            candidate => candidate.Kind == AnalysisRuleKind.Terminology);
        Assert.Equal("loop", terminology.Term);
        Assert.Equal(3, terminology.Claims.Count);
    }

    [Fact]
    public void SparseLexicalCandidateSource_AppliesGlobalCandidateCapDuringGeneration()
    {
        var documents = Set(Enumerable.Range(0, 12)
            .Select(index => Document(
                $"doc-{index}",
                $"docs/doc-{index}.md",
                Status.Current,
                $"Shared analysis runtime behavior variant {index} is documented here."))
            .ToArray());
        var request = new ClaimCandidateSourceRequest(
            Extract(documents),
            Graph(documents),
            Config(
                lexicalCandidateThreshold: 0.10,
                maxNeighbors: 10,
                maxCandidates: 3).Search);

        var result = new SparseLexicalCandidateSource().FindCandidates(request);

        Assert.True(result.Pairs.Count <= request.Search.MaxCandidates);
    }

    [Fact]
    public void Analyze_NonidenticalCodeBlocksWithoutSubstantiveDisagreement_AreNotConflicts()
    {
        var documents = Set(
            Document("debug", "docs/debug.md", Status.Current, "```csharp\nlogger.Debug(message);\n```", component: "Runtime"),
            Document("info", "docs/info.md", Status.Current, "```csharp\nlogger.Info(message);\n```", component: "Runtime"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.50, graph: 1);

        var result = Analyzer([source]).Analyze(
            documents,
            Graph(documents),
            Config(minClaimTokens: 1));

        Assert.DoesNotContain(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
    }

    [Fact]
    public void GraphCandidateSource_DenseGraphBoundsScoringBeforeSelectingCorrectPerClaimTopK()
    {
        const int documentCount = 40;
        const int maximumNeighbors = 2;
        var documents = Set(Enumerable.Range(0, documentCount)
            .Select(index => Document(
                $"dense-{index}",
                $"docs/dense-{index}.md",
                Status.Current,
                index switch
                {
                    0 => "Anchor alpha beta gamma delta epsilon omega governs processing.",
                    documentCount - 1 => "Anchor alpha beta gamma delta epsilon omega governs execution.",
                    _ => $"Filler topic-{index} records unrelated operational material."
                },
                component: "DenseRuntime"))
            .ToArray());
        var claims = Extract(documents);
        var request = new ClaimCandidateSourceRequest(
            claims,
            Graph(documents),
            Config(
                maxNeighbors: maximumNeighbors,
                lexicalCandidateThreshold: 0.10).Search);

        var result = new GraphClaimCandidateSource().FindCandidates(request);

        Assert.True(
            result.ComparisonCount <= claims.Count * maximumNeighbors,
            $"Dense graph scoring performed {result.ComparisonCount} comparisons for {claims.Count} claims at top-{maximumNeighbors}.");
        Assert.Contains(result.Pairs, pair =>
            PairIdentities(pair).SetEquals(["dense-0", $"dense-{documentCount - 1}"]));
        Assert.All(
            claims,
            claim => Assert.True(
                result.Pairs.Count(pair => pair.Left == claim || pair.Right == claim) <= maximumNeighbors));
    }

    [Fact]
    public void Analyze_LexicalCandidateThresholdGatesDuplicateAndConflictButNotTerminologyDivergence()
    {
        var ordinaryDocuments = Set(
            Document("duplicate-a", "docs/duplicate-a.md", Status.Current, "The processor securely retains approved imported verdict records."),
            Document("duplicate-b", "docs/duplicate-b.md", Status.Current, "The service safely preserves reviewed verdict records from imports."),
            Document("conflict-a", "docs/conflict-a.md", Status.Current, "The runtime runner must emit token usage reports.", component: "Runtime"),
            Document("conflict-b", "docs/conflict-b.md", Status.Current, "The runtime exporter must not publish model accounting summaries.", component: "Runtime"));
        var terminologyDocuments = Set(
            Document("gameplay", "docs/gameplay.md", Status.Current, "Gameplay loop wraps live testing while measuring elapsed duration.", section: "Gameplay"),
            Document("automation", "docs/automation.md", Status.Current, "Autonomous Codex loop churns agent tasks while consuming model tokens.", section: "Automation"));
        var config = Config(
            lexicalCandidateThreshold: 0.80,
            lexicalDuplicateThreshold: 0.30);

        var ordinary = Analyzer().Analyze(ordinaryDocuments, Graph(ordinaryDocuments), config);
        var terminology = Analyzer().Analyze(terminologyDocuments, Graph(terminologyDocuments), config);

        Assert.DoesNotContain(ordinary.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Duplicate);
        Assert.DoesNotContain(ordinary.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
        Assert.Contains(
            terminology.Candidates,
            candidate => candidate.Kind == AnalysisRuleKind.Terminology && candidate.Term == "loop");
    }

    [Fact]
    public void ClaimExtractor_FencedCodeRetainsLanguageAndInfoOnClaimMetadata()
    {
        var document = Document(
            "shell",
            "docs/shell.md",
            Status.Current,
            "```bash title=\"verification\"\ndotnet test\n```");

        var claim = Assert.Single(Extract(Set(document)));
        var fenceInfo = typeof(Claim).GetProperty("FenceInfo");

        Assert.NotNull(fenceInfo);
        Assert.Equal("bash title=\"verification\"", fenceInfo.GetValue(claim));
    }

    [Theory]
    [InlineData("yaml", "mode: local", "mode: remote")]
    [InlineData("", "plain documentation example", "another documentation example")]
    public void Analyze_NonShellFencedBlocksAreNotShellCommandConflicts(
        string fenceInfo,
        string leftText,
        string rightText)
    {
        var documents = Set(
            Document("left", "docs/left.md", Status.Current, Fence(fenceInfo, leftText), component: "Runtime"),
            Document("right", "docs/right.md", Status.Current, Fence(fenceInfo, rightText), component: "Runtime"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.50, graph: 1);

        var result = Analyzer([source]).Analyze(
            documents,
            Graph(documents),
            Config(minClaimTokens: 1));

        Assert.DoesNotContain(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
    }

    [Theory]
    [InlineData("bash", "MODE=local dotnet test", "MODE=remote dotnet test")]
    [InlineData("sh", "dotnet test && echo local", "dotnet test && echo remote")]
    [InlineData("bash", "cp /src/v1/report /dest", "cp /src/v2/report /dest")]
    public void Analyze_ShellFencesRecognizeAssignmentsAndCompoundCommandsAsConflictEligible(
        string fenceInfo,
        string leftCommand,
        string rightCommand)
    {
        var documents = Set(
            Document("left", "docs/left.md", Status.Current, Fence(fenceInfo, leftCommand), component: "Runtime"),
            Document("right", "docs/right.md", Status.Current, Fence(fenceInfo, rightCommand), component: "Runtime"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.50, graph: 1);

        var result = Analyzer([source]).Analyze(
            documents,
            Graph(documents),
            Config(minClaimTokens: 1));

        Assert.Contains(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
    }

    [Fact]
    public void GraphCandidateSource_DenseSmallCorpusStillBoundsScoringToClaimsTimesTopK()
    {
        const int documentCount = 8;
        const int maximumNeighbors = 2;
        var documents = Set(Enumerable.Range(0, documentCount)
            .Select(index => Document(
                $"small-dense-{index}",
                $"docs/small-dense-{index}.md",
                Status.Current,
                $"Shared dense runtime behavior has variant {index} documentation.",
                component: "DenseRuntime"))
            .ToArray());
        var claims = Extract(documents);

        var result = new GraphClaimCandidateSource().FindCandidates(new ClaimCandidateSourceRequest(
            claims,
            Graph(documents),
            Config(maxNeighbors: maximumNeighbors, lexicalCandidateThreshold: 0.10).Search));

        Assert.True(
            result.ComparisonCount <= claims.Count * maximumNeighbors,
            $"Dense graph scoring performed {result.ComparisonCount} comparisons for {claims.Count} claims at top-{maximumNeighbors}.");
    }

    [Fact]
    public void GraphCandidateSource_SparseRankingKeepsShortPerfectLexicalMatch()
    {
        var documents = Set(
            Document("short", "docs/00-short.md", Status.Current, "alpha beta", component: "Runtime", section: "Short"),
            Document("short-decoy", "docs/01-short-decoy.md", Status.Current, "alpha beta theta", component: "Runtime", section: "Decoy"),
            Document("filler-a", "docs/02-filler.md", Status.Current, "unrelated copper material", component: "Runtime", section: "Filler"),
            Document("filler-b", "docs/03-filler.md", Status.Current, "unrelated silver material", component: "Runtime", section: "Filler"),
            Document("long-decoy", "docs/04-long-decoy.md", Status.Current, "alpha beta gamma delta copper silver bronze quartz", component: "Runtime", section: "Long"),
            Document("anchor", "docs/05-anchor.md", Status.Current, "alpha beta gamma delta epsilon zeta", component: "Runtime", section: "Short"));
        var claims = Extract(documents);

        var result = new GraphClaimCandidateSource().FindCandidates(new ClaimCandidateSourceRequest(
            claims,
            Graph(documents),
            Config(maxNeighbors: 1, lexicalCandidateThreshold: 0.10).Search));

        Assert.Contains(result.Pairs, pair => PairIdentities(pair).SetEquals(["short", "anchor"]));
        Assert.True(result.ComparisonCount <= claims.Count);
    }

    [Theory]
    [InlineData("yaml", "version: 1", "version: 2")]
    [InlineData("text", "Endpoint is /api/v1/report", "Endpoint is /api/v2/report")]
    public void Analyze_NonShellFencesRemainEligibleForSubstantiveValueConflicts(
        string fenceInfo,
        string leftValue,
        string rightValue)
    {
        var documents = Set(
            Document("left", "docs/left.md", Status.Current, Fence(fenceInfo, leftValue), component: "Runtime"),
            Document("right", "docs/right.md", Status.Current, Fence(fenceInfo, rightValue), component: "Runtime"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.50, graph: 1);

        var result = Analyzer([source]).Analyze(
            documents,
            Graph(documents),
            Config(minClaimTokens: 1));

        Assert.Contains(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
    }

    [Fact]
    public void Analyze_ShellFencesCompareMeaningfulCommandsBeyondComments()
    {
        var differingAssignments = Set(
            Document("local", "docs/local.md", Status.Current, Fence("bash", "# shared setup\nMODE=local dotnet test"), component: "Runtime"),
            Document("remote", "docs/remote.md", Status.Current, Fence("bash", "# shared setup\nMODE=remote dotnet test"), component: "Runtime"));
        var commentsOnly = Set(
            Document("comment-a", "docs/comment-a.md", Status.Current, Fence("bash", "# local note\ndotnet test"), component: "Runtime"),
            Document("comment-b", "docs/comment-b.md", Status.Current, Fence("bash", "# remote note\ndotnet test"), component: "Runtime"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.50, graph: 1);

        var assignmentsResult = Analyzer([source]).Analyze(
            differingAssignments,
            Graph(differingAssignments),
            Config(minClaimTokens: 1));
        var commentsResult = Analyzer([source]).Analyze(
            commentsOnly,
            Graph(commentsOnly),
            Config(minClaimTokens: 1));

        Assert.Contains(assignmentsResult.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
        Assert.DoesNotContain(commentsResult.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
    }

    [Theory]
    [InlineData("bash", "# ")]
    [InlineData("powershell", "# ")]
    [InlineData("cmd", "REM ")]
    [InlineData("bat", "::")]
    public void Analyze_RecognizedShellFenceIgnoresNumericAndNegationDifferencesInComments(
        string fenceInfo,
        string commentPrefix)
    {
        var documents = Set(
            Document(
                "left",
                "docs/left.md",
                Status.Current,
                Fence(fenceInfo, $"{commentPrefix}do not use version 1\ndotnet test"),
                component: "Runtime"),
            Document(
                "right",
                "docs/right.md",
                Status.Current,
                Fence(fenceInfo, $"{commentPrefix}use version 2\ndotnet test"),
                component: "Runtime"));
        var source = new FirstPairCandidateSource(CandidateSourceKind.Graph, lexical: 0.50, graph: 1);

        var result = Analyzer([source]).Analyze(
            documents,
            Graph(documents),
            Config(minClaimTokens: 1));

        Assert.DoesNotContain(result.Candidates, candidate => candidate.Kind == AnalysisRuleKind.Conflict);
    }

    private static DocumentationAnalyzer Analyzer(
        IReadOnlyList<IClaimCandidateSource>? sources = null,
        IAnalysisPersistence? persistence = null,
        IEmbeddingGenerator? embeddingGenerator = null) =>
        new(
            new ClaimExtractor(),
            sources ?? [new GraphClaimCandidateSource(), new SparseLexicalCandidateSource()],
            embeddingGenerator,
            persistence);

    private static DocumentSet NearDuplicateDocuments() =>
        Set(
            Document("left", "docs/left.md", Status.Current, "The processor retains every imported approved verdict.", component: "Runtime"),
            Document("right", "docs/right.md", Status.Current, "The processor must retain all approved imported verdicts.", component: "Runtime"));

    private static IReadOnlyList<Claim> Extract(DocumentSet documents) =>
        documents.Documents.SelectMany(document => new ClaimExtractor().Extract(document).Claims).ToArray();

    private static DocGraphProjection Graph(DocumentSet documents) =>
        DocGraphProjection.Build(documents, FakeCodeGraphResolver.WithSymbols());

    private static DocumentSet Set(params DocumentModel[] documents) => new() { Documents = documents };

    private static DocumentModel Document(
        string id,
        string path,
        Status status,
        string claim,
        string? component = null,
        string section = "Behavior",
        IReadOnlyList<string>? codeRefs = null)
    {
        var body = $"## {section}\n\n{claim}\n";
        return new DocumentModel
        {
            RelativePath = path,
            FilePath = "/repo/" + path,
            HasFrontmatter = true,
            Frontmatter = new DocumentFrontmatter
            {
                Id = id,
                Title = id,
                DocType = "reference",
                Status = status.Value,
                Component = component,
                CodeRefs = codeRefs is null ? null : new Collection<string>(codeRefs.ToList())
            },
            DocType = DocType.Reference,
            Status = status.Model,
            Body = body,
            RawMarkdown = body,
            BodyStartLine = 1
        };
    }

    private static DocsAnalysisConfig Config(
        IReadOnlyList<string>? statuses = null,
        string? glossaryPath = null,
        DocsAnalysisSearchMode mode = DocsAnalysisSearchMode.Hybrid,
        int minClaimTokens = 5,
        double lexicalCandidateThreshold = 0.45,
        double lexicalDuplicateThreshold = 0.90,
        int maxNeighbors = 10,
        int maxCandidates = 500,
        DocsAnalysisEmbeddingConfig? embeddings = null) =>
        new()
        {
            Statuses = statuses ?? ["current"],
            GlossaryPath = glossaryPath,
            Search = new DocsAnalysisSearchConfig
            {
                Mode = mode,
                MinClaimTokens = minClaimTokens,
                LexicalCandidateThreshold = lexicalCandidateThreshold,
                LexicalDuplicateThreshold = lexicalDuplicateThreshold,
                SemanticCandidateThreshold = 0.78,
                SemanticDuplicateThreshold = 0.92,
                TerminologyContextThreshold = 0.30,
                MaxNeighborsPerClaim = maxNeighbors,
                MaxCodeNeighbors = 50,
                MaxCandidates = maxCandidates
            },
            Embeddings = embeddings ?? DocsAnalysisEmbeddingConfig.ProductDefaults
        };

    private static HashSet<string> PairIdentities(ClaimPairCandidate pair) =>
        new([pair.Left.DocumentIdentity, pair.Right.DocumentIdentity], StringComparer.Ordinal);

    private static string Fence(string info, string content) => $"```{info}\n{content}\n```";

    private sealed class RecordingCandidateSource(CandidateSourceKind kind) : IClaimCandidateSource
    {
        public CandidateSourceKind Kind { get; } = kind;
        public int CallCount { get; private set; }
        public List<DocsAnalysisSearchMode> RequestedModes { get; } = [];

        public ClaimCandidateSourceResult FindCandidates(ClaimCandidateSourceRequest request)
        {
            CallCount++;
            RequestedModes.Add(request.Search.Mode);
            return new ClaimCandidateSourceResult([], 0);
        }
    }

    private sealed class FirstPairCandidateSource(
        CandidateSourceKind kind,
        double lexical,
        double graph) : IClaimCandidateSource
    {
        public CandidateSourceKind Kind { get; } = kind;

        public ClaimCandidateSourceResult FindCandidates(ClaimCandidateSourceRequest request)
        {
            Assert.True(request.Claims.Count >= 2);
            return new ClaimCandidateSourceResult(
            [
                new ClaimPairCandidate(
                    request.Claims[0],
                    request.Claims[1],
                    Kind,
                    new CandidateScore(lexical, null, graph))
            ], 1);
        }
    }

    private sealed class AdjacentPairCandidateSource(
        CandidateSourceKind kind,
        int comparisonCount) : IClaimCandidateSource
    {
        public CandidateSourceKind Kind { get; } = kind;

        public ClaimCandidateSourceResult FindCandidates(ClaimCandidateSourceRequest request)
        {
            var pairs = request.Claims.Zip(request.Claims.Skip(1))
                .Select(pair => new ClaimPairCandidate(
                    pair.First,
                    pair.Second,
                    Kind,
                    new CandidateScore(0.95, null, Kind == CandidateSourceKind.Graph ? 1 : 0)))
                .ToArray();
            return new ClaimCandidateSourceResult(pairs, comparisonCount);
        }
    }

    private sealed class AllPairCandidateSource(
        CandidateSourceKind kind,
        double lexical,
        double graph) : IClaimCandidateSource
    {
        public CandidateSourceKind Kind { get; } = kind;

        public ClaimCandidateSourceResult FindCandidates(ClaimCandidateSourceRequest request)
        {
            var pairs = new List<ClaimPairCandidate>();
            for (var left = 0; left < request.Claims.Count; left++)
            {
                for (var right = left + 1; right < request.Claims.Count; right++)
                {
                    pairs.Add(new ClaimPairCandidate(
                        request.Claims[left],
                        request.Claims[right],
                        Kind,
                        new CandidateScore(lexical, null, graph)));
                }
            }

            return new ClaimCandidateSourceResult(pairs, pairs.Count);
        }
    }

    private sealed class StubPersistence(params AnalysisVerdict[] verdicts) : IAnalysisPersistence
    {
        private readonly IReadOnlyDictionary<string, AnalysisVerdict> _verdicts =
            verdicts.ToDictionary(verdict => verdict.CandidateId, StringComparer.Ordinal);

        public bool IsAvailable => true;

        public IReadOnlyDictionary<string, AnalysisVerdict> LoadVerdicts(
            IReadOnlyCollection<string> candidateIds) =>
            _verdicts
                .Where(pair => candidateIds.Contains(pair.Key, StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        public IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> LoadEmbeddings(
            IReadOnlyCollection<EmbeddingCacheKey> keys) =>
            new Dictionary<EmbeddingCacheKey, StoredEmbedding>();

        public void SaveEmbeddings(IReadOnlyCollection<StoredEmbedding> embeddings)
        {
        }
    }

    private sealed class SemanticMatchGenerator : IEmbeddingGenerator
    {
        public int CallCount { get; private set; }

        public string GetProviderFingerprint(DocsAnalysisEmbeddingConfig config) => "semantic-provider";

        public EmbeddingGenerationResult Generate(
            IReadOnlyCollection<EmbeddingCacheKey> keys,
            IReadOnlyCollection<string> inputs,
            DocsAnalysisEmbeddingConfig config)
        {
            CallCount++;
            Assert.Equal(keys.Count, inputs.Count);
            return new EmbeddingGenerationResult(
                keys.Select(key => new StoredEmbedding(key, [1f, 0f])).ToArray(),
                EmbeddingUsage.None);
        }
    }

    private sealed class EmbeddingPersistence : IAnalysisPersistence
    {
        private readonly Dictionary<EmbeddingCacheKey, StoredEmbedding> _embeddings = [];

        public bool IsAvailable => true;
        public List<StoredEmbedding> SavedEmbeddings { get; } = [];

        public IReadOnlyDictionary<string, AnalysisVerdict> LoadVerdicts(
            IReadOnlyCollection<string> candidateIds) =>
            new Dictionary<string, AnalysisVerdict>(StringComparer.Ordinal);

        public IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> LoadEmbeddings(
            IReadOnlyCollection<EmbeddingCacheKey> keys) =>
            keys.Where(_embeddings.ContainsKey).ToDictionary(key => key, key => _embeddings[key]);

        public void SaveEmbeddings(IReadOnlyCollection<StoredEmbedding> embeddings)
        {
            foreach (var embedding in embeddings)
            {
                SavedEmbeddings.Add(embedding);
                _embeddings[embedding.Key] = embedding;
            }
        }
    }

    private sealed record Status(string Value, DocStatus Model)
    {
        public static Status Current { get; } = new("current", DocStatus.Current);
        public static Status Draft { get; } = new("draft", DocStatus.Draft);
    }
}
